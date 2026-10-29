"""Tests for the category rules file, the matching logic, the test set and the evaluation script.

Run from the repository root with the project's Python (it needs pytest and jsonschema):

    python -m pytest cloud/category-rules-testset
"""

import copy
import json
from collections import Counter

import pytest
from jsonschema import Draft202012Validator

import evaluate
from category_rules import (
    CATEGORIES,
    REPO_ROOT,
    RULES_PATH,
    TESTSET_PATH,
    categorize,
    categorize_detailed,
    covers,
    load_json,
    normalize_app,
    normalize_site,
    registrable_domain,
    repeated_processes,
    site_matches,
    unreachable_site_rules,
)

RULES_SCHEMA = REPO_ROOT / "contracts" / "category-rules.schema.json"
TESTSET_SCHEMA = TESTSET_PATH.with_name("testset.schema.json")


@pytest.fixture(scope="module")
def rules() -> dict:
    return load_json(RULES_PATH)


@pytest.fixture(scope="module")
def testset() -> dict:
    return load_json(TESTSET_PATH)


def problems(schema_path, data) -> list[str]:
    validator = Draft202012Validator(load_json(schema_path))
    return sorted(f"{list(e.absolute_path)}: {e.message}" for e in validator.iter_errors(data))


# ------------------------------------------------------------------ the rules file against its schema


def test_the_rules_schema_is_itself_a_valid_schema():
    Draft202012Validator.check_schema(load_json(RULES_SCHEMA))


def test_the_rules_file_follows_its_schema(rules):
    assert problems(RULES_SCHEMA, rules) == []


def test_the_rules_file_has_rules_and_the_default_is_other(rules):
    assert rules["default_category"] == "Other"
    assert len(rules["apps"]) > 50 and len(rules["sites"]) > 100


def mutated(rules: dict, change) -> dict:
    copy_ = copy.deepcopy(rules)
    change(copy_)
    return copy_


@pytest.mark.parametrize(
    "change, needle",
    [
        (lambda r: r["apps"].append({"process": "my app", "category": "Work"}), "does not match"),      # a space
        (lambda r: r["apps"].append({"process": "Code", "category": "Work"}), "does not match"),         # capital letter
        (lambda r: r["apps"].append({"process": "code.exe", "category": "Work"}), "should not be valid"),  # .exe
        (lambda r: r["apps"].append({"process": "c:\\x\\code", "category": "Work"}), "does not match"),  # a path
        (lambda r: r["apps"].append({"process": "tool", "category": "Gaming"}), "is not one of"),
        (lambda r: r["apps"].append({"process": "tool"}), "'category' is a required property"),
        (lambda r: r["apps"].append({"process": "tool", "category": "Work", "title": "x"}), "'title' was unexpected"),
        (lambda r: r["sites"].append({"match": "prefix", "value": "a.com", "category": "Work"}), "is not one of"),
        (lambda r: r["sites"].append({"match": "suffix", "value": "https://a.com/x", "category": "Work"}), "does not match"),
        (lambda r: r["sites"].append({"match": "suffix", "value": "WWW.A.COM", "category": "Work"}), "does not match"),
        (lambda r: r["sites"].append({"match": "exact", "value": "edu", "category": "Study"}), "does not match"),  # exact needs a dot
        (lambda r: r["sites"].append({"match": "suffix", "value": "a.com"}), "'category' is a required property"),
        (lambda r: r.update(default_category="Work"), "was expected"),
        (lambda r: r.update(schema_version="2"), "was expected"),
        (lambda r: r.update(rules_version=0), "less than the minimum"),
        (lambda r: r.update(updated="29/10/2026"), "does not match"),
        (lambda r: r.pop("rules_version"), "'rules_version' is a required property"),
        (lambda r: r.update(extra_field=1), "'extra_field' was unexpected"),
    ],
)
def test_a_bad_rules_file_is_refused(rules, change, needle):
    found = problems(RULES_SCHEMA, mutated(rules, change))
    assert any(needle in p for p in found), found


def test_a_single_label_suffix_such_as_edu_is_allowed(rules):
    ok = mutated(rules, lambda r: r["sites"].append({"match": "suffix", "value": "edu", "category": "Study"}))
    assert problems(RULES_SCHEMA, ok) == []


# ------------------------------------------------- order and repeats (what a schema can not check)


def test_no_process_appears_twice(rules):
    assert repeated_processes(rules) == []


def test_no_site_rule_is_hidden_by_an_earlier_one(rules):
    hidden = [(rules["sites"][i], rules["sites"][j]) for i, j in unreachable_site_rules(rules)]
    assert hidden == [], f"never reached: {[(a['value'], b['value']) for a, b in hidden]}"


def test_the_hidden_rule_check_really_finds_hidden_rules():
    suffix = {"match": "suffix", "value": "google.com", "category": "Other"}
    docs = {"match": "suffix", "value": "docs.google.com", "category": "Work"}
    exact = {"match": "exact", "value": "google.com", "category": "Other"}
    assert covers(suffix, docs) and covers(suffix, exact) and covers(suffix, suffix)
    assert not covers(docs, suffix)                       # specific before general is the right way round
    assert not covers(exact, docs) and not covers(exact, suffix)
    assert unreachable_site_rules({"sites": [suffix, docs]}) == [(0, 1)]
    assert unreachable_site_rules({"sites": [docs, suffix]}) == []
    assert repeated_processes({"apps": [{"process": "a"}, {"process": "b"}, {"process": "a"}]}) == ["a"]


# --------------------------------------------------------------------------- how a rule is applied


@pytest.mark.parametrize("given, expected", [("Code.exe", "code"), ("CODE.EXE", "code"), ("code", "code"), ("  Chrome.exe ", "chrome"),
                                             ("notepad++.exe", "notepad++"), ("x.exe.exe", "x.exe")])
def test_app_names_are_tidied(given, expected):
    assert normalize_app(given) == expected


@pytest.mark.parametrize("given, expected", [("WWW.YouTube.com", "youtube.com"), ("youtube.com.", "youtube.com"),
                                             ("www.www.a.com", "www.a.com"), ("docs.google.com", "docs.google.com"), (" a.com ", "a.com")])
def test_site_names_are_tidied(given, expected):
    assert normalize_site(given) == expected


def test_suffix_matches_the_domain_itself_and_its_subdomains_only():
    rule = {"match": "suffix", "value": "google.com", "category": "Other"}
    assert site_matches(rule, "google.com")
    assert site_matches(rule, "docs.google.com")
    assert site_matches(rule, "a.b.google.com")
    assert not site_matches(rule, "notgoogle.com")        # the dot matters
    assert not site_matches(rule, "google.com.evil.example")
    assert not site_matches(rule, "oogle.com")


def test_exact_matches_only_that_site():
    rule = {"match": "exact", "value": "google.com", "category": "Other"}
    assert site_matches(rule, "google.com")
    assert not site_matches(rule, "docs.google.com")


def tiny(**kw) -> dict:
    base = {"schema_version": "1", "rules_version": 1, "updated": "2026-10-29", "default_category": "Other", "apps": [], "sites": []}
    return {**base, **kw}


def test_the_first_matching_rule_wins():
    first_specific = tiny(sites=[{"match": "suffix", "value": "docs.google.com", "category": "Work"},
                                 {"match": "suffix", "value": "google.com", "category": "Entertainment"}])
    assert categorize(first_specific, site="docs.google.com") == "Work"
    assert categorize(first_specific, site="maps.google.com") == "Entertainment"
    first_general = tiny(sites=list(reversed(first_specific["sites"])))
    assert categorize(first_general, site="docs.google.com") == "Entertainment"   # the order is the meaning


def test_a_rule_reports_which_rule_matched():
    rule = {"match": "suffix", "value": "a.com", "category": "Study"}
    assert categorize_detailed(tiny(sites=[rule]), site="www.a.com") == ("Study", rule)
    assert categorize_detailed(tiny(sites=[rule]), site="b.com") == ("Other", None)


def test_an_unknown_app_or_site_is_other():
    assert categorize(tiny(), app="whatever.exe") == "Other"
    assert categorize(tiny(), site="whatever.example.com") == "Other"


@pytest.mark.parametrize("app, site", [(None, None), ("", ""), ("  ", " "), ("", None)])
def test_nothing_to_look_at_is_other(rules, app, site):
    assert categorize(rules, app=app, site=site) == "Other"


def test_an_event_with_a_site_uses_the_site_rules_not_the_app_rules(rules):
    assert categorize(rules, app="chrome.exe", site="github.com") == "Work"
    assert categorize(rules, app="Code.exe", site="netflix.com") == "Entertainment"


def test_apps_match_the_whole_name_not_a_part(rules):
    assert categorize(rules, app="Code.exe") == "Work"
    assert categorize(rules, app="codex.exe") == "Other"
    assert categorize(rules, app="vscode.exe") == "Other"


def test_the_real_rules_on_a_few_well_known_cases(rules):
    cases = [
        ("app", "Code.exe", "Work"), ("app", "WINWORD.EXE", "Work"), ("app", "Spotify.exe", "Entertainment"),
        ("app", "Discord.exe", "Social Media"), ("app", "Anki.exe", "Study"), ("app", "chrome.exe", "Other"),
        ("app", "explorer.exe", "Other"),
        ("site", "docs.google.com", "Work"), ("site", "www.google.com", "Other"), ("site", "maps.google.com", "Other"),
        ("site", "scholar.google.com", "Study"), ("site", "m.facebook.com", "Social Media"), ("site", "old.reddit.com", "Social Media"),
        ("site", "ocw.mit.edu", "Study"), ("site", "nust.edu.pk", "Study"), ("site", "irs.gov", "Other"),
        ("site", "pubmed.ncbi.nlm.nih.gov", "Study"), ("site", "music.youtube.com", "Entertainment"),
        ("site", "notgithub.com", "Other"), ("site", "github.com.evil.example", "Other"),
    ]
    for kind, name, expected in cases:
        got = categorize(rules, **({"app": name} if kind == "app" else {"site": name}))
        assert got == expected, f"{name}: {got}"


@pytest.mark.parametrize("host, domain", [("www.youtube.com", "youtube.com"), ("docs.google.com", "google.com"),
                                          ("news.bbc.co.uk", "bbc.co.uk"), ("nust.edu.pk", "nust.edu.pk"),
                                          ("cs.nust.edu.pk", "nust.edu.pk"), ("youtube.com", "youtube.com")])
def test_registrable_domain_follows_the_extension(host, domain):
    assert registrable_domain(host) == domain


# ------------------------------------------------------------------------------- the test set


def test_the_testset_schema_is_itself_a_valid_schema():
    Draft202012Validator.check_schema(load_json(TESTSET_SCHEMA))


def test_the_testset_follows_its_schema(testset):
    assert problems(TESTSET_SCHEMA, testset) == []


def test_the_testset_has_at_least_200_examples(testset):
    assert len(testset["examples"]) >= 200


def test_ids_are_unique_and_in_order(testset):
    ids = [e["id"] for e in testset["examples"]]
    assert ids == [f"t{i:03d}" for i in range(1, len(ids) + 1)]


def test_no_example_is_listed_twice(testset):
    keys = Counter((e["app"] and e["app"].lower(), e["site"], e["title"]) for e in testset["examples"])
    assert [k for k, c in keys.items() if c > 1] == []


def test_every_category_has_plenty_of_clear_examples(testset):
    counts = Counter(e["category"] for e in testset["examples"] if not e["ambiguous"])
    assert all(counts[c] >= 30 for c in CATEGORIES), counts


def test_there_are_both_apps_and_sites_and_both_batches(testset):
    ex = testset["examples"]
    assert sum(1 for e in ex if e["app"]) >= 80 and sum(1 for e in ex if e["site"]) >= 100
    assert {e["batch"] for e in ex} == {"A", "B"}
    assert all(sum(1 for e in ex if e["batch"] == b and not e["ambiguous"]) >= 100 for b in "AB")


def test_there_are_hard_cases_marked_ambiguous(testset):
    ambiguous = [e for e in testset["examples"] if e["ambiguous"]]
    assert len(ambiguous) >= 40
    assert all(e["also_acceptable"] and e["category"] not in e["also_acceptable"] for e in ambiguous)
    assert all(not e["also_acceptable"] for e in testset["examples"] if not e["ambiguous"])


def test_a_study_video_and_an_entertainment_video_on_youtube_are_both_there(testset):
    youtube = [e for e in testset["examples"] if e["site"] == "youtube.com"]
    study = [e for e in youtube if e["category"] == "Study"]
    leisure = [e for e in youtube if e["category"] == "Entertainment"]
    assert study and leisure
    assert all(e["ambiguous"] and "Entertainment" in e["also_acceptable"] for e in study)


def test_batch_b_was_not_fitted_to_the_rules_in_the_sense_that_it_is_not_all_covered(rules, testset):
    """Not a quality bar: a guard that batch B still contains things the rules do not know. If this fails, the rules
    were extended with batch B and batch B has become training data (write a new batch instead)."""
    clear_b = [e for e in testset["examples"] if e["batch"] == "B" and not e["ambiguous"] and e["category"] != "Other"]
    unmatched = [e for e in clear_b if categorize_detailed(rules, e["app"], e["site"])[1] is None]
    assert len(unmatched) >= len(clear_b) * 0.3


def test_nothing_private_is_in_the_testset(testset):
    text = TESTSET_PATH.read_text(encoding="utf-8")
    for forbidden in ("@", "http://", "https://", "\\", "password", "token"):
        assert forbidden not in text.lower(), forbidden
    for e in testset["examples"]:
        assert e["title"] is None or len(e["title"]) <= 60


def test_no_title_belongs_to_a_mail_or_chat_site(testset):
    """Webmail and chat titles show subjects and names; the extension never keeps them (device/extension/sensitive-domains.js)."""
    private_sites = {"mail.google.com", "outlook.office.com", "outlook.live.com", "web.whatsapp.com", "web.telegram.org",
                     "messenger.com", "discord.com", "app.slack.com", "teams.microsoft.com", "mail.proton.me"}
    assert [e["id"] for e in testset["examples"] if e["site"] in private_sites and e["title"]] == []
    private_apps = {"whatsapp", "telegram", "signal", "messenger", "discord", "slack", "teams", "ms-teams", "outlook", "viber", "skype"}
    assert [e["id"] for e in testset["examples"] if e["app"] and normalize_app(e["app"]) in private_apps and e["title"]] == []


# ------------------------------------------------------------------------ the evaluation script


def test_wilson_interval_known_values():
    assert evaluate.wilson(0, 0) == (0.0, 0.0)
    low, high = evaluate.wilson(10, 10)
    assert high == pytest.approx(1.0) and low == pytest.approx(0.722, abs=0.002)
    low, high = evaluate.wilson(5, 10)
    assert low == pytest.approx(0.237, abs=0.002) and high == pytest.approx(0.763, abs=0.002)
    low, high = evaluate.wilson(0, 30)
    assert low == 0.0 and high == pytest.approx(0.114, abs=0.002)


def test_evaluation_numbers_add_up(rules, testset):
    result = evaluate.evaluate(rules, testset)
    total = 0
    for batch in result["batches"].values():
        for kind in ("clear", "ambiguous"):
            s = batch[kind]
            total += s["n"]
            assert sum(c["n"] for c in s["per_true_category"].values()) == s["n"]
            assert sum(c["said"] for c in s["per_predicted_category"].values()) == s["n"]
            assert sum(c["correct"] for c in s["per_true_category"].values()) == s["correct"]
            assert sum(c["right"] for c in s["per_predicted_category"].values()) == s["correct"]
            assert s["by_kind"]["app"]["n"] + s["by_kind"]["site"]["n"] == s["n"]
            assert s["correct"] <= s["acceptable"] <= s["n"]
            assert len(s["wrong"]) == s["n"] - s["correct"]
            assert s["no_rule_matched_but_not_other"] <= s["no_rule_matched"] <= s["n"]
    assert total == result["total_examples"] == len(testset["examples"])
    assert result["ambiguous_all_batches"]["n"] == sum(1 for e in testset["examples"] if e["ambiguous"])


def test_evaluation_is_the_same_every_time(rules, testset):
    assert json.dumps(evaluate.evaluate(rules, testset), sort_keys=True) == json.dumps(evaluate.evaluate(rules, testset), sort_keys=True)


def test_evaluation_catches_a_wrong_rule(rules, testset):
    """If a rule is changed to a wrong category, the clear score must go down."""
    before = evaluate.evaluate(rules, testset)["batches"]["A"]["clear"]["correct"]
    broken = copy.deepcopy(rules)
    next(r for r in broken["apps"] if r["process"] == "code")["category"] = "Entertainment"
    next(r for r in broken["sites"] if r["value"] == "github.com")["category"] = "Entertainment"
    after = evaluate.evaluate(broken, testset)["batches"]["A"]["clear"]
    assert after["correct"] < before
    assert after["per_true_category"]["Work"]["correct"] < evaluate.evaluate(rules, testset)["batches"]["A"]["clear"]["per_true_category"]["Work"]["correct"]


def test_evaluation_with_no_rules_says_other_for_everything(rules, testset):
    empty = {**rules, "apps": [], "sites": []}
    for batch in evaluate.evaluate(empty, testset)["batches"].values():
        clear = batch["clear"]
        assert clear["per_predicted_category"]["Other"]["said"] == clear["n"]
        assert clear["correct"] == clear["per_true_category"]["Other"]["n"]


def test_domain_only_mode_can_only_lose_host_specific_rules(rules, testset):
    host = evaluate.evaluate(rules, testset)["batches"]["A"]["clear"]["correct"]
    domain = evaluate.evaluate(rules, testset, domain_only=True)["batches"]["A"]["clear"]["correct"]
    assert domain < host   # docs.google.com, mail.google.com and others need the host
