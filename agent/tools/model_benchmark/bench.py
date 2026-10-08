#!/usr/bin/env python
"""
DLA on-device model benchmark. Offline tool, not part of the agent.

  python bench.py check-data   --data labeled.csv
  python bench.py export-titles --db %LOCALAPPDATA%\\DLA\\dla.db --out data\\to_label.csv
  python bench.py gate         --model path\\model.onnx          (size + load memory only: for any ONNX incl. LLMs)
  python bench.py run          --data labeled.csv --embedder onnx --model m.onnx --tokenizer tokenizer.json --candidate minilm
  python bench.py run          --data synthetic\\synthetic_titles.csv --embedder hashing      (script self-test)

Reports are written to ./reports (git-ignored). Real labeled data lives in ./data (git-ignored).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime
from pathlib import Path

import numpy as np

from dla_bench import SYNTHETIC_BANNER
from dla_bench import data as D
from dla_bench import metrics as M
from dla_bench import resources as R
from dla_bench.classifier import train
from dla_bench.embedders import CANDIDATES, HashingEmbedder, OnnxEmbedder

HERE = Path(__file__).resolve().parent


# ---------------------------------------------------------------------------------------------------------------
def cmd_check_data(args) -> int:
    rows = D.load_dataset(args.data)
    problems = D.validate(rows, require_labels=not args.allow_unlabeled)
    synthetic = D.is_synthetic(rows)
    if synthetic:
        print(SYNTHETIC_BANNER)
    labeled = [r for r in rows if r["label"]]
    print(f"rows: {len(rows)}   labeled: {len(labeled)}")
    for label in D.LABELS:
        print(f"  {label:<14} {sum(r['label'] == label for r in labeled)}")

    if labeled and not problems:
        split = D.assign_split(labeled, args.seed, mode=args.split_mode)
        counts = {s: sum(v == s for v in split.values()) for s in ("train", "cal", "test")}
        lo, hi = M.wilson_ci(round(0.9 * counts["test"]), counts["test"]) if counts["test"] else (float("nan"),) * 2
        print(f"split ({args.split_mode}): {counts}")
        print(f"test set of {counts['test']} gives a 95% interval of about [{lo:.3f}, {hi:.3f}] around 90%")
        print(f"test size needed for +-2%: {M.required_test_size()}   for +-3%: {M.required_test_size(half_width=0.03)}")
        problems += D.split_problems(labeled, split)

    pair = [(r["label"], r["label_b"]) for r in rows if r["label"] and r.get("label_b")]
    if pair:
        a, b = zip(*pair)
        print(f"second labeler on {len(pair)} rows: agreement {sum(x == y for x, y in pair) / len(pair):.3f}, "
              f"Cohen's kappa {M.cohen_kappa(list(a), list(b)):.3f}  (the model cannot beat human agreement)")

    for p in problems:
        print("PROBLEM:", p)
    print("OK" if not problems else f"{len(problems)} problem(s)")
    return 0 if not problems else 1


def cmd_export_titles(args) -> int:
    n = D.export_titles(args.db, args.out, limit=args.limit, seed=args.seed)
    print(f"wrote {n} scrubbed rows to {args.out}. Label them by hand, keep the file out of git, never upload it.")
    return 0


# ---------------------------------------------------------------------------------------------------------------
def cmd_gate(args) -> int:
    """Gate 0: does the model even fit? Size on disk and memory after loading. No accuracy work needed to fail."""
    import onnxruntime as ort

    size = R.files_mb(args.model, args.tokenizer)
    before = R.rss_mb()
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 1
    t0 = time.perf_counter()
    ort.InferenceSession(str(args.model), opts, providers=["CPUExecutionProvider"])
    load_s = time.perf_counter() - t0
    delta, peak = R.rss_mb() - before, R.peak_mb() - before
    res = {
        "model": str(args.model), "disk_mb": round(size, 1), "load_seconds": round(load_s, 2),
        "ram_delta_after_load_mb": round(delta, 1), "ram_peak_delta_mb": round(peak, 1),
        "pass_disk": size <= R.GATES["disk_mb"], "pass_ram": peak <= R.GATES["model_ram_mb"],
    }
    print(json.dumps(res, indent=2))
    return 0 if res["pass_disk"] and res["pass_ram"] else 2


# ---------------------------------------------------------------------------------------------------------------
def build_embedder(args, synthetic: bool):
    if args.embedder == "hashing":
        if not synthetic:
            sys.exit("refusing: the hashing embedder is a synthetic stand-in and may not run on real data")
        return HashingEmbedder(), None
    if not (args.model and args.tokenizer):
        sys.exit("--model and --tokenizer are required for --embedder onnx")
    preset = CANDIDATES.get(args.candidate, {})
    emb = OnnxEmbedder(args.model, args.tokenizer, pooling=preset.get("pooling", args.pooling),
                       prefix=preset.get("prefix", args.prefix), max_len=args.max_len,
                       pad_id=preset.get("pad_id", 0), pad_token=preset.get("pad_token", "[PAD]"))
    return emb, args.model


def cmd_run(args) -> int:
    rows = D.load_dataset(args.data)
    problems = D.validate(rows)
    if problems:
        print("\n".join("PROBLEM: " + p for p in problems[:20]))
        return 1
    synthetic = D.is_synthetic(rows)
    split = D.assign_split(rows, args.seed, mode=args.split_mode)
    problems = D.split_problems(rows, split)
    if problems:
        print("\n".join("PROBLEM: " + p for p in problems))
        return 1
    if synthetic:
        print(SYNTHETIC_BANNER)

    label_id = {l: i for i, l in enumerate(D.LABELS)}
    part = {s: [r for r in rows if split[r["id"]] == s] for s in ("train", "cal", "test")}
    text = lambda rs: [D.compose_text(r, args.text_format) for r in rs]  # noqa: E731
    y = {s: np.array([label_id[r["label"]] for r in rs]) for s, rs in part.items()}

    # memory baseline is taken after the heavy imports, so only the model's own cost is attributed to it
    base_rss = R.rss_mb()
    emb, model_path = build_embedder(args, synthetic)
    rss_loaded = R.rss_mb()

    x_train, x_cal = emb.encode(text(part["train"])), emb.encode(text(part["cal"]))

    # per-event latency: one title at a time, like the agent will do it
    test_texts = text(part["test"])
    lat_ms, vecs = [], []
    cpu0, wall0 = time.process_time(), time.perf_counter()
    for t in test_texts:
        t0 = time.perf_counter()
        vecs.append(emb.encode([t])[0])
        lat_ms.append((time.perf_counter() - t0) * 1000)
    cpu_s, wall_s = time.process_time() - cpu0, time.perf_counter() - wall0
    x_test = np.vstack(vecs)
    peak_delta = R.peak_mb() - base_rss

    clf = train(x_train, y["train"], x_cal, y["cal"], seed=args.seed)
    export_kb = None
    if args.export_dir:
        export_kb = clf.export(Path(args.export_dir) / "classifier.json",
                               {"candidate": args.candidate or emb.name, "text_format": args.text_format,
                                "synthetic": synthetic}) / 1024

    raw_p, cal_p = clf.proba(x_test, calibrated=False), clf.proba(x_test, calibrated=True)
    pred = cal_p.argmax(axis=1)
    correct = pred == y["test"]
    conf_raw, conf_cal = raw_p.max(axis=1), cal_p.max(axis=1)

    n, k = len(correct), int(correct.sum())
    lo, hi = M.wilson_ci(k, n)
    cm = M.confusion_matrix(y["test"], pred, len(D.LABELS))
    pc = M.per_class(cm, D.LABELS)
    min_recall = min(v["recall"] for v in pc.values())
    model_mb = R.files_mb(model_path, args.tokenizer) if model_path else 0.0

    report = {
        "synthetic": synthetic,
        "banner": SYNTHETIC_BANNER if synthetic else None,
        "created": datetime.now().isoformat(timespec="seconds"),
        "data": {"file": str(args.data), "rows": len(rows), "split_mode": args.split_mode, "seed": args.seed,
                 "sizes": {s: len(rs) for s, rs in part.items()}, "text_format": args.text_format},
        "embedder": emb.name, "classifier": {"C": clf.c, "temperature": round(clf.temperature, 4)},
        "accuracy": {"value": M.accuracy(y["test"], pred), "ci95": [lo, hi], "n": n,
                     "macro_f1": M.macro_f1(pc), "per_class": pc, "confusion_matrix": cm.tolist(), "labels": D.LABELS},
        "latency_ms": {**M.percentiles(lat_ms), "cpu_seconds_per_event": cpu_s / n, "cpu_fraction_while_running": cpu_s / wall_s},
        "memory_mb": {"baseline_after_imports": round(base_rss, 1), "model_load_delta": round(rss_loaded - base_rss, 1),
                      "peak_delta": round(peak_delta, 1)},
        "disk_mb": {"model_and_tokenizer": round(model_mb, 2), "classifier_json_kb": export_kb},
        "calibration": {
            "ece_before": M.ece(conf_raw, correct), "ece_after": M.ece(conf_cal, correct),
            "brier_before": M.brier(raw_p, y["test"]), "brier_after": M.brier(cal_p, y["test"]),
            "nll_before": M.nll(raw_p, y["test"]), "nll_after": M.nll(cal_p, y["test"]),
            "reliability_after": M.reliability_bins(conf_cal, correct),
            "needs_review": M.needs_review_stats(conf_cal, correct),
        },
    }
    g = R.GATES
    report["gates"] = {
        "disk_<=_500MB": model_mb <= g["disk_mb"],
        "accuracy_>=_90%_(point_estimate)": report["accuracy"]["value"] >= g["accuracy"],
        "accuracy_>=_90%_(lower_95%_bound)": lo >= g["accuracy"],
        "model_ram_<=_100MB_(250_cap_minus_150_base)": peak_delta <= g["model_ram_mb"],
        "latency_p95_<=_100ms_(assumed)": report["latency_ms"]["p95"] <= g["latency_p95_ms"],
        "ece_after_<=_0.05_(assumed)": report["calibration"]["ece_after"] <= g["ece"],
        "every_class_recall_>=_80%_(assumed)": min_recall >= g["min_class_recall"],
    }

    out_dir = HERE / "reports"
    out_dir.mkdir(exist_ok=True)
    tag = "SYNTHETIC_" if synthetic else ""
    safe = "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in (args.candidate or emb.name))
    out = out_dir / f"{tag}{safe}_{datetime.now():%Y%m%d_%H%M%S}.json"
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print_summary(report, out)
    return 0


def print_summary(r: dict, out: Path) -> None:
    a, c, m, lat = r["accuracy"], r["calibration"], r["memory_mb"], r["latency_ms"]
    print(f"\nembedder {r['embedder']}   split {r['data']['split_mode']}   test n={a['n']}")
    print(f"accuracy {a['value']:.3f}  (95% CI {a['ci95'][0]:.3f} to {a['ci95'][1]:.3f})   macro-F1 {a['macro_f1']:.3f}")
    for name, v in a["per_class"].items():
        print(f"  {name:<14} precision {v['precision']:.2f}  recall {v['recall']:.2f}  n={v['support']}")
    print(f"latency  p50 {lat['p50']:.1f} ms   p95 {lat['p95']:.1f} ms   p99 {lat['p99']:.1f} ms")
    print(f"memory   load +{m['model_load_delta']} MB   peak +{m['peak_delta']} MB   disk {r['disk_mb']['model_and_tokenizer']} MB")
    nr = c["needs_review"]
    print(f"calibration  ECE {c['ece_before']:.3f} -> {c['ece_after']:.3f}   Brier {c['brier_before']:.3f} -> {c['brier_after']:.3f}")
    pct = lambda v: "n/a" if v is None else f"{v:.1%}"  # noqa: E731
    print(f"needs review (<60%): {pct(nr['review_rate'])} of events; accuracy auto-accepted {pct(nr['accuracy_auto_accepted'])}, "
          f"in review {pct(nr['accuracy_in_review'])}; errors caught by review {pct(nr['errors_caught_by_review'])}")
    print("gates:")
    for name, ok in r["gates"].items():
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    if r["synthetic"]:
        print("\n" + SYNTHETIC_BANNER)
    print(f"report: {out}")


# ---------------------------------------------------------------------------------------------------------------
def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("check-data")
    c.add_argument("--data", required=True)
    c.add_argument("--seed", type=int, default=13)
    c.add_argument("--split-mode", choices=["group", "random"], default="group")
    c.add_argument("--allow-unlabeled", action="store_true")
    c.set_defaults(fn=cmd_check_data)

    e = sub.add_parser("export-titles")
    e.add_argument("--db", required=True)
    e.add_argument("--out", required=True)
    e.add_argument("--limit", type=int, default=3000)
    e.add_argument("--seed", type=int, default=13)
    e.set_defaults(fn=cmd_export_titles)

    g = sub.add_parser("gate")
    g.add_argument("--model", required=True)
    g.add_argument("--tokenizer")
    g.set_defaults(fn=cmd_gate)

    r = sub.add_parser("run")
    r.add_argument("--data", required=True)
    r.add_argument("--embedder", choices=["onnx", "hashing"], default="onnx")
    r.add_argument("--model")
    r.add_argument("--tokenizer")
    r.add_argument("--candidate", choices=list(CANDIDATES), help="preset for pooling/prefix/pad")
    r.add_argument("--pooling", choices=["mean", "cls"], default="mean")
    r.add_argument("--prefix", default="")
    r.add_argument("--max-len", type=int, default=64)
    r.add_argument("--text-format", choices=["full", "title"], default="full")
    r.add_argument("--split-mode", choices=["group", "random"], default="group")
    r.add_argument("--seed", type=int, default=13)
    r.add_argument("--export-dir")
    r.set_defaults(fn=cmd_run)

    args = p.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
