"""The mock data in cloud/mock/ must always follow Contract 2."""

import json

import pytest

from app.config import REPO_ROOT
from app.summary_contract import validate_summary

MOCK_DIR = REPO_ROOT / "cloud" / "mock"
MOCK_FILES = sorted(MOCK_DIR.glob("summary-*.json"))


def test_there_is_at_least_one_mock_summary():
    assert MOCK_FILES, "cloud/mock/ has no summary-*.json file"


@pytest.mark.parametrize("path", MOCK_FILES, ids=lambda p: p.name)
def test_mock_summary_follows_contract_2(path):
    summary = json.loads(path.read_text(encoding="utf-8"))
    assert validate_summary(summary) == []


@pytest.mark.parametrize("path", MOCK_FILES, ids=lambda p: p.name)
def test_mock_file_name_matches_its_date(path):
    summary = json.loads(path.read_text(encoding="utf-8"))
    assert path.name == f"summary-{summary['date']}.json"


def test_mock_summary_is_the_contract_sample_with_only_the_day_moved():
    sample = json.loads((REPO_ROOT / "contracts" / "samples" / "summary-valid.json").read_text(encoding="utf-8"))
    mock = json.loads((MOCK_DIR / "summary-2026-10-28.json").read_text(encoding="utf-8"))
    assert mock["date"] == "2026-10-28" and mock["generated_at"] == "2026-10-28T17:30:00Z"
    for key in ("date", "generated_at"):
        del sample[key], mock[key]
    assert mock == sample


@pytest.mark.parametrize("path", MOCK_FILES, ids=lambda p: p.name)
def test_mock_summary_holds_nothing_that_could_identify_a_page_or_person(path):
    text = path.read_text(encoding="utf-8")
    for forbidden in ("http://", "https://", "@", "title", "\\", "password"):
        assert forbidden not in text.lower(), f"found {forbidden!r} in {path.name}"
