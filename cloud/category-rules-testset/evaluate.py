"""Measures how well the category rules do on the labelled test set.

Run from the repository root (needs only Python, no packages):

    python cloud/category-rules-testset/evaluate.py
    python cloud/category-rules-testset/evaluate.py --show-errors     also list every wrong answer
    python cloud/category-rules-testset/evaluate.py --domain-only     sites matched on the registrable domain only
    python cloud/category-rules-testset/evaluate.py --json            machine-readable result

Three things are kept apart, so no number claims more than it shows:
  - batch B (written AFTER the rules were frozen) is the headline; batch A (the rules were written with it in view)
    is only a check that the rules file works;
  - clear examples are the score; ambiguous examples (where sensible people disagree) are reported separately.
"""

import argparse
import json
import math
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from category_rules import (  # noqa: E402
    CATEGORIES,
    RULES_PATH,
    TESTSET_PATH,
    categorize_detailed,
    load_json,
    registrable_domain,
)

BATCH_TEXT = {
    "A": "written first; the rules were written with these in view, so a high score here proves little",
    "B": "written AFTER the rules were frozen, from other angles: the honest test",
}


def wilson(correct: int, n: int, z: float = 1.96) -> tuple[float, float]:
    """95% confidence interval for a share (Wilson). Shows how little a small group of examples proves."""
    if n == 0:
        return (0.0, 0.0)
    p = correct / n
    centre = (p + z * z / (2 * n)) / (1 + z * z / n)
    spread = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return (max(0.0, centre - spread), min(1.0, centre + spread))


def predict(rules: dict, example: dict, domain_only: bool = False) -> tuple[str, bool]:
    """(category, matched a rule?). domain_only: pretend the agent passes only the registrable domain of a site."""
    site = example["site"]
    if site and domain_only:
        site = registrable_domain(site)
    category, rule = categorize_detailed(rules, app=example["app"], site=site)
    return category, rule is not None


def score(rules: dict, examples: list[dict], domain_only: bool = False) -> dict:
    """All numbers for one group of examples."""
    rows = [(e, *predict(rules, e, domain_only)) for e in examples]  # (example, predicted, matched)

    per_true = {}
    for category in CATEGORIES:
        group = [(e, p) for e, p, _ in rows if e["category"] == category]
        per_true[category] = {"n": len(group), "correct": sum(1 for e, p in group if p == category)}

    per_predicted = {}
    for category in CATEGORIES:
        said = [(e, p) for e, p, _ in rows if p == category]
        per_predicted[category] = {"said": len(said), "right": sum(1 for e, p in said if e["category"] == category)}

    # Coverage: how often no rule matched at all, and how often that was a real miss (the true answer was not Other).
    unmatched = [e for e, _, m in rows if not m]

    by_kind = {}
    for kind in ("app", "site"):
        group = [(e, p) for e, p, _ in rows if (e["app"] is not None) == (kind == "app")]
        by_kind[kind] = {"n": len(group), "correct": sum(1 for e, p in group if p == e["category"])}

    return {
        "n": len(rows),
        "correct": sum(1 for e, p, _ in rows if p == e["category"]),
        "acceptable": sum(1 for e, p, _ in rows if p == e["category"] or p in e["also_acceptable"]),
        "per_true_category": per_true,
        "per_predicted_category": per_predicted,
        "by_kind": by_kind,
        "no_rule_matched": len(unmatched),
        "no_rule_matched_but_not_other": sum(1 for e in unmatched if e["category"] != "Other"),
        "mistakes": {f"{t} -> {p}": c for (t, p), c in Counter((e["category"], p) for e, p, _ in rows if p != e["category"]).most_common()},
        "wrong": [
            {"id": e["id"], "batch": e["batch"], "name": e["app"] or e["site"], "title": e["title"], "true": e["category"],
             "rules_said": p, "also_acceptable": e["also_acceptable"]}
            for e, p, _ in rows if p != e["category"]
        ],
    }


def evaluate(rules: dict, testset: dict, domain_only: bool = False) -> dict:
    examples = testset["examples"]
    batches = {}
    for batch in sorted({e["batch"] for e in examples}):
        in_batch = [e for e in examples if e["batch"] == batch]
        batches[batch] = {
            "clear": score(rules, [e for e in in_batch if not e["ambiguous"]], domain_only),
            "ambiguous": score(rules, [e for e in in_batch if e["ambiguous"]], domain_only),
        }
    return {
        "rules_version": rules["rules_version"],
        "rule_counts": {"apps": len(rules["apps"]), "sites": len(rules["sites"])},
        "total_examples": len(examples),
        "mode": "domain_only" if domain_only else "host",
        "batches": batches,
        "ambiguous_all_batches": score(rules, [e for e in examples if e["ambiguous"]], domain_only),
    }


# ------------------------------------------------------------------------------------------------ printing


def pct(correct: int, n: int) -> str:
    return "  n/a" if n == 0 else f"{100 * correct / n:5.1f}%"


def ci(correct: int, n: int) -> str:
    low, high = wilson(correct, n)
    return f"95% CI {100 * low:3.0f}-{100 * high:3.0f}%"


def print_clear(title: str, clear: dict) -> None:
    print(f"=== {title} ===")
    print(f"Overall: {clear['correct']}/{clear['n']} = {pct(clear['correct'], clear['n'])}   {ci(clear['correct'], clear['n'])}")
    for kind in ("app", "site"):
        k = clear["by_kind"][kind]
        print(f"  {kind + 's':5}: {k['correct']:3}/{k['n']:3} = {pct(k['correct'], k['n'])}")
    print("By TRUE category (of the examples that really are X, how many did the rules label X):")
    for category in CATEGORIES:
        t = clear["per_true_category"][category]
        print(f"  {category:14} {t['correct']:3}/{t['n']:3} = {pct(t['correct'], t['n'])}   {ci(t['correct'], t['n'])}")
    print("By category THE RULES SAID (when the rules say X, how often is it really X):")
    for category in CATEGORIES:
        p = clear["per_predicted_category"][category]
        print(f"  {category:14} {p['right']:3}/{p['said']:3} = {pct(p['right'], p['said'])}")
    print(f"No rule matched (so the answer was the default Other): {clear['no_rule_matched']} examples; "
          f"{clear['no_rule_matched_but_not_other']} of those were wrong because the true category is not Other.")
    if clear["mistakes"]:
        print("Mistakes (true -> rules said):", "; ".join(f"{k} x{v}" for k, v in clear["mistakes"].items()))
    print()


def print_report(result: dict, show_errors: bool) -> None:
    batches = result["batches"]
    print(f"Category rules v{result['rules_version']}: {result['rule_counts']['apps']} app rules, "
          f"{result['rule_counts']['sites']} site rules")
    sizes = ", ".join(f"batch {b}: {v['clear']['n']} clear + {v['ambiguous']['n']} ambiguous" for b, v in batches.items())
    print(f"Test set: {result['total_examples']} examples ({sizes})")
    where = "host of the page" if result["mode"] == "host" else "registrable domain only"
    print(f"Input mode: {result['mode']} (sites matched on the {where})")
    print()
    for batch in sorted(batches, reverse=True):  # B first: it is the headline
        print_clear(f"CLEAR examples, batch {batch} ({BATCH_TEXT.get(batch, '')})", batches[batch]["clear"])

    amb = result["ambiguous_all_batches"]
    print("=== AMBIGUOUS examples, all batches (reported separately; not part of any score above) ===")
    print(f"Same as the first label : {amb['correct']}/{amb['n']} = {pct(amb['correct'], amb['n'])}   {ci(amb['correct'], amb['n'])}")
    print(f"Any acceptable label    : {amb['acceptable']}/{amb['n']} = {pct(amb['acceptable'], amb['n'])}   {ci(amb['acceptable'], amb['n'])}")
    for batch in sorted(batches):
        b = batches[batch]["ambiguous"]
        print(f"  batch {batch}: first label {b['correct']}/{b['n']} = {pct(b['correct'], b['n'])}, "
              f"any acceptable {b['acceptable']}/{b['n']} = {pct(b['acceptable'], b['n'])}")
    print("By first label:")
    for category in CATEGORIES:
        t = amb["per_true_category"][category]
        print(f"  {category:14} {t['correct']:3}/{t['n']:3} = {pct(t['correct'], t['n'])}")
    print()
    print("What these numbers do NOT show:")
    print("  - The rules and every example were written by the same author. Batch B was written after the rules were")
    print("    frozen, which removes fitting, but it is still one person's idea of 'well-known', and the labels are one")
    print("    person's judgement. This is NOT accuracy on real users' activity.")
    print("  - Real activity has many apps and sites that are in neither list.")
    print("  - Rules never read titles, so they can not tell a lecture from a film on the same site.")

    if show_errors:
        print()
        for batch in sorted(batches):
            for label in ("clear", "ambiguous"):
                group = batches[batch][label]
                print(f"--- wrong answers, batch {batch}, {label} ({len(group['wrong'])}) ---")
                for w in group["wrong"]:
                    extra = f"  (also acceptable: {', '.join(w['also_acceptable'])})" if w["also_acceptable"] else ""
                    title = f'  title="{w["title"]}"' if w["title"] else ""
                    print(f"  {w['id']}  {w['name']:34} true={w['true']:14} rules={w['rules_said']}{title}{extra}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Accuracy of the category rules on the labelled test set.")
    parser.add_argument("--rules", type=Path, default=RULES_PATH)
    parser.add_argument("--testset", type=Path, default=TESTSET_PATH)
    parser.add_argument("--show-errors", action="store_true", help="list every wrong answer")
    parser.add_argument("--domain-only", action="store_true", help="match sites on the registrable domain only (what-if)")
    parser.add_argument("--json", action="store_true", help="print the result as JSON")
    args = parser.parse_args()

    rules, testset = load_json(args.rules), load_json(args.testset)
    result = evaluate(rules, testset, domain_only=args.domain_only)
    if args.json:
        print(json.dumps(result, indent=2))
        return
    print_report(result, args.show_errors)
    if not args.domain_only:
        domain = evaluate(rules, testset, domain_only=True)
        print()
        print("=== WHAT IF the agent passes only the domain (docs.google.com -> google.com)? Clear examples only ===")
        for batch in sorted(result["batches"], reverse=True):
            h, d = result["batches"][batch]["clear"], domain["batches"][batch]["clear"]
            print(f"  batch {batch}: {h['correct']}/{h['n']} = {pct(h['correct'], h['n'])} with the host of the page,  "
                  f"{d['correct']}/{d['n']} = {pct(d['correct'], d['n'])} with the registrable domain only")


if __name__ == "__main__":
    main()
