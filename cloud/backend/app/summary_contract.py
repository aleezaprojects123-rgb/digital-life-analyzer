"""Checks a daily summary against Contract 2 (contracts/summary-format.md).

Two parts: the JSON Schema (shape, types, no extra fields) and the "Checks the server makes"
(numbers that must add up, which a schema can not express). Both return a list of short problems;
an empty list means the summary is valid. The messages name fields, never the values' meaning,
so they are safe to send back to the agent.
"""

import json
from functools import lru_cache

from jsonschema import Draft202012Validator

from .config import REPO_ROOT

SCHEMA_PATH = REPO_ROOT / "contracts" / "summary.schema.json"
CATEGORIES = ("Study", "Work", "Entertainment", "Social Media", "Other")
PRESENCE_WITH_CATEGORY = ("active", "passive", "uncertain")
MAX_TRACKED_SECONDS = 90000  # 25 hours: room for a daylight-saving day
MAX_HOUR_SECONDS = 3600


@lru_cache
def _validator() -> Draft202012Validator:
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def schema_problems(summary: object) -> list[str]:
    """Shape problems, sorted so the answer is the same every time."""
    problems = []
    for error in _validator().iter_errors(summary):
        path = "$" + "".join(f"[{p}]" if isinstance(p, int) else f".{p}" for p in error.absolute_path)
        problems.append(f"{path}: {error.message}")
    return sorted(problems)


def consistency_problems(summary: dict) -> list[str]:
    """The seven checks of Contract 2. Call this only on a summary that passed schema_problems."""
    problems: list[str] = []
    presence = summary["presence_seconds"]
    by_category = summary["category_seconds"]
    labels = summary["labels"]
    hourly = summary["hourly"]

    # 1. The five categories add up to each presence total.
    for state in PRESENCE_WITH_CATEGORY:
        total = sum(by_category[c][state] for c in CATEGORIES)
        if total != presence[state]:
            problems.append(f"presence_seconds.{state} is {presence[state]} but the categories add up to {total}")

    counted = sum(presence[s] for s in PRESENCE_WITH_CATEGORY)

    # 2. Every counted second has a label source.
    if labels["model_seconds"] + labels["rules_seconds"] != counted:
        problems.append("labels.model_seconds + labels.rules_seconds is not the counted time")

    # 3. Uncertain Other is part of Other, and only the model makes it.
    other_total = sum(by_category["Other"][s] for s in PRESENCE_WITH_CATEGORY)
    if labels["uncertain_other_seconds"] > other_total:
        problems.append("labels.uncertain_other_seconds is more than the Other total")
    if labels["uncertain_other_seconds"] > labels["model_seconds"]:
        problems.append("labels.uncertain_other_seconds is more than labels.model_seconds")

    # 4. Each hour is in its place, interruptions are switches, and an hour has at most 3600 seconds.
    for position, hour in enumerate(hourly):
        if hour["hour"] != position:
            problems.append(f"hourly[{position}].hour is {hour['hour']}")
        if hour["interruptions"] > hour["switches"]:
            problems.append(f"hourly[{position}].interruptions is more than switches")
        if sum(hour["category_seconds"].values()) + hour["away_seconds"] > MAX_HOUR_SECONDS:
            problems.append(f"hourly[{position}] has more than {MAX_HOUR_SECONDS} seconds")

    # 5. The 24 hours add up to the day.
    for category in CATEGORIES:
        day_total = sum(by_category[category][s] for s in PRESENCE_WITH_CATEGORY)
        hour_total = sum(h["category_seconds"][category] for h in hourly)
        if day_total != hour_total:
            problems.append(f"{category}: the hours add up to {hour_total} but category_seconds says {day_total}")
    if sum(h["away_seconds"] for h in hourly) != presence["away"]:
        problems.append("hourly away_seconds do not add up to presence_seconds.away")

    # 6. Top lists: sorted, no repeats, and not more than the day holds.
    apps, sites = summary["top_apps"], summary["top_sites"]
    for name, items in (("top_apps", apps), ("top_sites", sites)):
        if any(items[i]["seconds"] > items[i - 1]["seconds"] for i in range(1, len(items))):
            problems.append(f"{name} is not sorted, largest first")
    if len({a["name"].lower() for a in apps}) != len(apps):
        problems.append("top_apps lists an app twice")
    if len({s["domain"] for s in sites}) != len(sites):
        problems.append("top_sites lists a site twice")
    listed = sum(a["seconds"] for a in apps) + sum(s["seconds"] for s in sites)
    if listed > counted:
        problems.append("top_apps and top_sites together are more than the counted time")
    for category in CATEGORIES:
        in_lists = sum(i["seconds"] for i in (*apps, *sites) if i["category"] == category)
        if in_lists > sum(by_category[category][s] for s in PRESENCE_WITH_CATEGORY):
            problems.append(f"the top lists hold more {category} time than the {category} total")

    # 7. A day has at most 25 hours.
    if counted + presence["away"] > MAX_TRACKED_SECONDS:
        problems.append("the tracked time is more than 25 hours")

    return problems


def validate_summary(summary: object) -> list[str]:
    """Shape first; the number checks only run on a summary of the right shape."""
    problems = schema_problems(summary)
    return problems if problems else consistency_problems(summary)
