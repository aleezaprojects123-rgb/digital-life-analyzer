"""Reference implementation of the category rules (contracts/category-rules.md).

It defines exactly how a rules file is applied, so the C# agent can be checked against it:
the same rules file and the same test set must give the same answers. Plain Python, no packages.
"""

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
RULES_PATH = REPO_ROOT / "contracts" / "category-rules.json"
TESTSET_PATH = Path(__file__).with_name("testset.json")

CATEGORIES = ("Study", "Work", "Entertainment", "Social Media", "Other")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def load_rules(path: Path = RULES_PATH) -> dict:
    return load_json(path)


def normalize_app(app: str) -> str:
    """Code.exe, CODE.EXE and code all become code."""
    name = app.strip().lower()
    return name[:-4] if name.endswith(".exe") else name


def normalize_site(site: str) -> str:
    """WWW.YouTube.com. becomes youtube.com. Only one leading www. is removed."""
    host = site.strip().lower().rstrip(".")
    return host[4:] if host.startswith("www.") else host


def site_matches(rule: dict, site: str) -> bool:
    """site must already be normalized."""
    value = rule["value"]
    if rule["match"] == "exact":
        return site == value
    return site == value or site.endswith("." + value)


def categorize_detailed(rules: dict, app: str | None = None, site: str | None = None) -> tuple[str, dict | None]:
    """Returns (category, the rule that matched or None).

    An extension event has a site and uses the sites rules. An agent event has only an app and uses the apps rules.
    If both are given, the site wins. Nothing given, or nothing matched: the default category.
    """
    if site and site.strip():
        host = normalize_site(site)
        for rule in rules["sites"]:
            if site_matches(rule, host):
                return rule["category"], rule
    elif app and app.strip():
        name = normalize_app(app)
        for rule in rules["apps"]:
            if rule["process"] == name:
                return rule["category"], rule
    return rules["default_category"], None


def categorize(rules: dict, app: str | None = None, site: str | None = None) -> str:
    return categorize_detailed(rules, app, site)[0]


def covers(earlier: dict, later: dict) -> bool:
    """True if every site that matches `later` also matches `earlier`, so `later` can never be reached."""
    if earlier["match"] == "exact":
        return later["match"] == "exact" and later["value"] == earlier["value"]
    return later["value"] == earlier["value"] or later["value"].endswith("." + earlier["value"])


def unreachable_site_rules(rules: dict) -> list[tuple[int, int]]:
    """(earlier index, later index) for every site rule hidden by an earlier one. Should be empty."""
    sites = rules["sites"]
    return [(i, j) for j in range(len(sites)) for i in range(j) if covers(sites[i], sites[j])]


def repeated_processes(rules: dict) -> list[str]:
    """Processes that appear more than once in `apps`. Should be empty."""
    seen, repeated = set(), []
    for rule in rules["apps"]:
        if rule["process"] in seen:
            repeated.append(rule["process"])
        seen.add(rule["process"])
    return repeated


# ---- only for the "what if the agent passes only the domain" comparison in evaluate.py ----------------------------
# Copied from device/extension/url-sanitizer.js (getDomain). Keep in sync if that list changes.
_MULTI_PART_SUFFIXES = {
    "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk",
    "com.pk", "edu.pk", "org.pk", "gov.pk", "net.pk",
    "co.in", "net.in", "org.in", "ac.in", "gov.in", "edu.in",
    "com.au", "net.au", "org.au", "edu.au", "gov.au",
    "co.nz", "org.nz", "net.nz", "ac.nz",
    "co.jp", "ne.jp", "or.jp", "ac.jp",
    "co.kr", "co.za", "co.id", "co.il", "co.th",
    "com.br", "com.cn", "com.hk", "com.sg", "com.my", "com.tr", "com.mx", "com.ar", "com.sa", "com.eg", "com.ng",
    "com.bd", "com.lk", "com.ph", "com.vn", "com.tw", "com.ua", "com.pe", "com.co", "com.np", "edu.au",
}


def registrable_domain(host: str) -> str:
    """What the extension stores in `domain`: docs.google.com -> google.com, news.bbc.co.uk -> bbc.co.uk."""
    labels = normalize_site(host).split(".")
    if ".".join(labels[-2:]) in _MULTI_PART_SUFFIXES:
        return ".".join(labels[-3:]) if len(labels) >= 3 else ".".join(labels)
    return ".".join(labels[-2:])
