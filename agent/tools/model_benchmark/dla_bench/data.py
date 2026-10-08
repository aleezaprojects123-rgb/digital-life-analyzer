"""Evaluation-set format, validation, deterministic splitting, scrubbing and title export."""
from __future__ import annotations

import csv
import hashlib
import re
import sqlite3
from pathlib import Path

LABELS = ["Study", "Work", "Entertainment", "Social Media", "Other"]
FIELDS = ["id", "origin", "source", "app", "site", "title", "label", "label_b", "notes"]
ORIGINS = ("real", "synthetic")
SOURCES = ("agent", "extension")


def load_dataset(path: str | Path) -> list[dict]:
    """Reads the CSV (UTF-8, Excel's BOM tolerated). Lines starting with '#' are comments."""
    with open(path, newline="", encoding="utf-8-sig") as f:
        lines = [ln for ln in f if not ln.startswith("#")]
    return [{k: (v or "").strip() for k, v in row.items()} for row in csv.DictReader(lines)]


def validate(rows: list[dict], require_labels: bool = True) -> list[str]:
    """Returns a list of human-readable problems; empty means the file is usable."""
    problems: list[str] = []
    if not rows:
        return ["file has no rows"]
    missing = [c for c in ("id", "origin", "title", "label") if c not in rows[0]]
    if missing:
        return [f"missing columns: {missing}"]

    seen: set[str] = set()
    for n, r in enumerate(rows, start=2):  # row 1 is the header
        rid = r["id"]
        if not rid:
            problems.append(f"line {n}: empty id")
        elif rid in seen:
            problems.append(f"line {n}: duplicate id {rid!r}")
        seen.add(rid)
        if r["origin"] not in ORIGINS:
            problems.append(f"line {n}: origin must be one of {ORIGINS}, got {r['origin']!r}")
        if r.get("source") and r["source"] not in SOURCES:
            problems.append(f"line {n}: source must be one of {SOURCES}, got {r['source']!r}")
        if not r["title"] and not r.get("app") and not r.get("site"):
            problems.append(f"line {n}: needs at least one of title, app, site")
        for col in ("label", "label_b"):
            v = r.get(col, "")
            if v and v not in LABELS:
                problems.append(f"line {n}: {col} {v!r} is not one of {LABELS}")
        if require_labels and not r["label"]:
            problems.append(f"line {n}: label is empty")

    origins = {r["origin"] for r in rows}
    if len(origins) > 1:
        problems.append(f"mixed origins {sorted(origins)}: never mix synthetic and real rows")
    return problems


def is_synthetic(rows: list[dict]) -> bool:
    return any(r["origin"] == "synthetic" for r in rows)


def group_key(row: dict) -> str:
    """Titles from the same site/app look alike, so they must not straddle train and test."""
    return (row.get("site") or row.get("app") or row["id"]).lower()


def _unit(text: str) -> float:
    return int.from_bytes(hashlib.sha256(text.encode("utf-8")).digest()[:8], "big") / 2**64


def assign_split(rows: list[dict], seed: int = 13, fractions=(0.60, 0.15, 0.25), mode: str = "group") -> dict[str, str]:
    """
    Deterministic split into train / cal / test.
    group  (default, pessimistic): every site/app lands in exactly one split, so the test set measures unseen sites.
    random: titles split independently; optimistic, because the same site appears in train and test.
    """
    if mode not in ("group", "random"):
        raise ValueError("mode must be 'group' or 'random'")
    names = ("train", "cal", "test")
    if mode == "random":
        a, b = fractions[0], fractions[0] + fractions[1]
        return {r["id"]: ("train" if (u := _unit(f"{seed}|{r['id']}")) < a else "cal" if u < b else "test") for r in rows}

    # Stratified group split. Hashing alone is too lumpy when there are only dozens of sites, so groups are visited in
    # a seeded pseudo-random order and each goes to the split that is furthest below its target for that group's classes.
    groups: dict[str, list[dict]] = {}
    for r in rows:
        groups.setdefault(group_key(r), []).append(r)
    class_total: dict[str, int] = {}
    for r in rows:
        class_total[r["label"]] = class_total.get(r["label"], 0) + 1
    have = {s: {} for s in names}
    out = {}
    for key in sorted(groups, key=lambda g: _unit(f"{seed}|group|{g}")):
        counts: dict[str, int] = {}
        for r in groups[key]:
            counts[r["label"]] = counts.get(r["label"], 0) + 1

        def need(i):  # how far split i is below target, weighted by this group's class mix
            s = names[i]
            return sum(n * (fractions[i] - have[s].get(c, 0) / class_total[c]) for c, n in counts.items())

        best = names[max(range(3), key=need)]
        for c, n in counts.items():
            have[best][c] = have[best].get(c, 0) + n
        for r in groups[key]:
            out[r["id"]] = best
    return out


def split_problems(rows: list[dict], split: dict[str, str], min_per_class: int = 1) -> list[str]:
    """Every split needs every class, or training and measurement are meaningless."""
    problems = []
    for name in ("train", "cal", "test"):
        for label in LABELS:
            n = sum(1 for r in rows if split[r["id"]] == name and r["label"] == label)
            if n < min_per_class:
                problems.append(f"split {name!r} has {n} examples of {label!r}")
    return problems


def compose_text(row: dict, fmt: str = "full") -> str:
    """What the model reads. 'full' = app, site and title; 'title' = title only (an ablation)."""
    if fmt == "title":
        return row["title"] or row.get("site") or row.get("app") or ""
    parts = []
    if row.get("app"):
        parts.append(f"app: {row['app']}")
    if row.get("site"):
        parts.append(f"site: {row['site']}")
    if row.get("title"):
        parts.append(f"title: {row['title']}")
    return " | ".join(parts)


# ---- privacy helpers for real titles --------------------------------------------------------------------------

_EMAIL = re.compile(r"[\w.+-]+@[\w-]+\.[\w.-]+")
_DIGITS = re.compile(r"\d{6,}")
_URL_QUERY = re.compile(r"(https?://[^\s?#]+)[?#]\S*")


def scrub_title(title: str) -> str:
    """Masks e-mail addresses, long digit runs (ids, phone and card numbers) and URL query strings."""
    t = _EMAIL.sub("<email>", title)
    t = _URL_QUERY.sub(r"\1", t)
    return _DIGITS.sub("<number>", t)


def export_titles(db_path: str | Path, out_csv: str | Path, limit: int = 3000, seed: int = 13) -> int:
    """
    Reads DISTINCT (app, site, title) triples from the local DLA database (read-only) and writes a CSV with an empty
    label column for hand labeling. Titles are scrubbed. The output holds real browsing data: keep it out of git
    (agent/tools/model_benchmark/data/ is ignored) and never upload it.
    """
    con = sqlite3.connect(f"file:{Path(db_path).as_posix()}?mode=ro", uri=True)
    try:
        rows = con.execute(
            "SELECT DISTINCT source, COALESCE(app,''), COALESCE(site,''), COALESCE(title,'') FROM events "
            "WHERE is_unknown = 0 AND (title IS NOT NULL OR site IS NOT NULL OR app IS NOT NULL)"
        ).fetchall()
    finally:
        con.close()

    rows.sort(key=lambda r: _unit(f"{seed}|{r[1]}|{r[2]}|{r[3]}"))  # stable pseudo-random sample
    rows = rows[:limit]
    out = Path(out_csv)
    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, FIELDS)
        w.writeheader()
        for i, (source, app, site, title) in enumerate(rows, start=1):
            w.writerow({"id": f"r{i:05d}", "origin": "real", "source": source, "app": app, "site": site,
                        "title": scrub_title(title), "label": "", "label_b": "", "notes": ""})
    return len(rows)
