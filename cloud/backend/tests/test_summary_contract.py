"""The Contract 2 checker, tried on the contract's own samples and on broken copies of them."""

import copy
import json

import pytest

from app.config import REPO_ROOT
from app.summary_contract import consistency_problems, schema_problems, validate_summary

SAMPLES = REPO_ROOT / "contracts" / "samples"


def load(name: str) -> dict:
    return json.loads((SAMPLES / name).read_text(encoding="utf-8"))


@pytest.fixture
def valid() -> dict:
    return load("summary-valid.json")


def mutated(valid: dict, change) -> dict:
    copy_ = copy.deepcopy(valid)
    change(copy_)
    return copy_


def test_the_valid_sample_passes_everything(valid):
    assert validate_summary(valid) == []


def test_the_invalid_sample_is_rejected_for_exactly_the_six_planted_mistakes():
    problems = schema_problems(load("summary-invalid.json"))
    assert len(problems) == 6, problems
    expected = [
        "'window_titles' was unexpected",
        "$.date: '29/10/2026' does not match",
        "$.presence_seconds.uncertain: -5 is less than the minimum",
        "$.labels.mean_confidence: 1.4 is greater than the maximum",
        "$.top_apps[0].name: 'Chapter 4 - Thermodynamics.pdf' does not match",
        "'url' was unexpected",
    ]
    for needle in expected:
        assert any(needle in p for p in problems), f"no problem mentions: {needle}"


def test_validate_summary_reports_only_shape_problems_for_the_invalid_sample():
    assert len(validate_summary(load("summary-invalid.json"))) == 6


# ------------------------------------------------ shape problems (schema)


@pytest.mark.parametrize(
    "change, needle",
    [
        (lambda s: s["hourly"].pop(), "is too short"),
        (lambda s: s["top_sites"].extend([dict(s["top_sites"][0])] * 10), "is too long"),
        (lambda s: s["presence_seconds"].pop("away"), "'away' is a required property"),
        (lambda s: s.pop("labels"), "'labels' is a required property"),
        (lambda s: s["top_sites"][0].update(domain="https://youtube.com/watch"), "does not match"),
        (lambda s: s["top_apps"][0].update(name="main.cs - dla-agent"), "does not match"),
        (lambda s: s["category_seconds"]["Work"].update(title="x"), "'title' was unexpected"),
        (lambda s: s["hourly"][9]["category_seconds"].update(Study=3601), "greater than the maximum"),
        (lambda s: s.update(schema_version="2"), "was expected"),
        (lambda s: s["category_seconds"].pop("Social Media"), "'Social Media' is a required property"),
        (lambda s: s["labels"].update(model_seconds=0), "is not of type 'null'"),
        (lambda s: s.update(page_text="hello"), "'page_text' was unexpected"),
        (lambda s: s["top_apps"][0].update(category="Gaming"), "is not one of"),
    ],
)
def test_a_wrong_shape_is_found(valid, change, needle):
    problems = schema_problems(mutated(valid, change))
    assert problems, "expected a problem"
    assert any(needle in p for p in problems), problems


def test_no_model_labels_means_no_mean_confidence(valid):
    def model_off(s):
        s["labels"].update(model_seconds=0, rules_seconds=s["labels"]["model_seconds"] + s["labels"]["rules_seconds"],
                           uncertain_other_seconds=0, mean_confidence=None)

    assert validate_summary(mutated(valid, model_off)) == []


# --------------------------------- numbers that must add up (the 7 checks)


@pytest.mark.parametrize(
    "change, needle",
    [
        (lambda s: s["presence_seconds"].update(active=s["presence_seconds"]["active"] + 10), "presence_seconds.active"),
        (lambda s: s["labels"].update(rules_seconds=s["labels"]["rules_seconds"] + 1), "is not the counted time"),
        (lambda s: s["labels"].update(uncertain_other_seconds=999999), "more than the Other total"),
        (lambda s: s["hourly"][5].update(hour=7), "hourly[5].hour is 7"),
        (lambda s: s["hourly"][9].update(interruptions=99), "interruptions is more than switches"),
        (lambda s: s["hourly"][9]["category_seconds"].update(Study=3500), "more than 3600 seconds"),
        (lambda s: s["hourly"][10].update(away_seconds=s["hourly"][10]["away_seconds"] + 5), "away_seconds do not add up"),
        (lambda s: s["top_apps"].reverse(), "top_apps is not sorted"),
        (lambda s: s["top_sites"].reverse(), "top_sites is not sorted"),
        (lambda s: s["top_sites"].__setitem__(1, {**s["top_sites"][0], "seconds": s["top_sites"][1]["seconds"]}), "top_sites lists a site twice"),
        (lambda s: s["top_apps"].__setitem__(1, {**s["top_apps"][0], "name": s["top_apps"][0]["name"].upper(), "seconds": s["top_apps"][1]["seconds"]}), "top_apps lists an app twice"),
        (lambda s: s["top_apps"][0].update(seconds=10**5 - 1), "more than the counted time"),
        (lambda s: s["presence_seconds"].update(away=s["presence_seconds"]["away"] + 60000), "25 hours"),
    ],
)
def test_numbers_that_do_not_add_up_are_found(valid, change, needle):
    summary = mutated(valid, change)
    problems = consistency_problems(summary)
    assert any(needle in p for p in problems), problems


def test_a_category_total_that_does_not_match_its_hours_is_found(valid):
    valid["category_seconds"]["Study"]["active"] += 5
    valid["presence_seconds"]["active"] += 5          # keep check 1 happy so only the hours disagree
    valid["labels"]["model_seconds"] += 5             # keep check 2 happy
    problems = consistency_problems(valid)
    assert problems == ["Study: the hours add up to 9600 but category_seconds says 9605"]


def test_validate_summary_does_not_run_number_checks_on_a_wrong_shape(valid):
    del valid["hourly"]
    problems = validate_summary(valid)
    assert problems and all(p.startswith("$") for p in problems)
