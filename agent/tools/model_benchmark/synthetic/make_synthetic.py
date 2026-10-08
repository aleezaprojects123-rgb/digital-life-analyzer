#!/usr/bin/env python
"""
Generates synthetic_titles.csv: INVENTED window/site titles, only for testing the benchmark scripts.
Nothing here comes from a real person or a real model evaluation. Results on this file mean nothing.
"""
import csv
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from dla_bench.data import FIELDS  # noqa: E402

TOPICS = ["thermodynamics", "linear algebra", "organic chemistry", "world history", "calculus", "statistics", "python loops", "cell biology"]
SHOWS = ["The Long Harbor", "Night Shift", "Dragon Kitchen", "Space Cadets", "Blue Mountain"]
GAMES = ["Block Quest", "Turbo Kart", "Farm Valley", "Sky Raiders"]
PROJECTS = ["billing-api", "website-redesign", "q3-report", "onboarding-flow", "data-pipeline"]
PEOPLE = ["Sam", "Priya", "Lee", "Maya", "Omar"]
GENERIC = ["Home", "New Tab", "Untitled", "Loading...", ""]

# (class, app, site, title templates). {t} topic, {s} show, {g} game, {p} project, {n} number, {w} person
GROUPS = [
    ("Study", "chrome.exe", "coursera.org", ["Week {n}: {t} | Coursera", "{t} - Quiz {n}", "Lecture {n} - {t}"]),
    ("Study", "chrome.exe", "khanacademy.org", ["{t} | Khan Academy", "Practice: {t} ({n} questions)"]),
    ("Study", "chrome.exe", "docs.python.org", ["{t} - Python 3 documentation", "The Python Tutorial: {t}"]),
    ("Study", "chrome.exe", "en.wikipedia.org", ["{t} - Wikipedia", "History of {t} - Wikipedia"]),
    ("Study", "chrome.exe", "ocw.mit.edu", ["{t} | Lecture Notes | MIT OpenCourseWare"]),
    ("Study", "acrobat.exe", "", ["Chapter {n} - {t}.pdf", "{t} textbook.pdf - Adobe Acrobat"]),
    ("Study", "anki.exe", "", ["Anki - {t} deck", "Reviewing: {t}"]),
    ("Work", "code.exe", "", ["main.py - {p} - Visual Studio Code", "{p}/README.md - Visual Studio Code"]),
    ("Work", "excel.exe", "", ["{p}-budget.xlsx - Excel", "Sheet{n} - {p} - Excel"]),
    ("Work", "outlook.exe", "", ["Inbox - Outlook", "RE: {p} deadline - Message"]),
    ("Work", "teams.exe", "", ["Standup {p} | Microsoft Teams", "Chat with {w} | Microsoft Teams"]),
    ("Work", "chrome.exe", "github.com", ["Pull request #{n}: fix {p} · GitHub", "{p} · Issues · GitHub"]),
    ("Work", "chrome.exe", "docs.google.com", ["{p} proposal - Google Docs", "{p} notes - Google Docs"]),
    ("Work", "chrome.exe", "jira.atlassian.net", ["[{p}-{n}] Sprint board - Jira"]),
    ("Entertainment", "chrome.exe", "youtube.com", ["{s} full episode - YouTube", "Funny cats compilation #{n} - YouTube", "{g} gameplay - YouTube"]),
    ("Entertainment", "chrome.exe", "netflix.com", ["{s}: Season {n} | Netflix", "Watch {s} | Netflix"]),
    ("Entertainment", "chrome.exe", "twitch.tv", ["{g} - live | Twitch", "{w}'s stream - Twitch"]),
    ("Entertainment", "steam.exe", "", ["{g}", "{g} - Steam"]),
    ("Entertainment", "spotify.exe", "", ["Spotify - Chill mix {n}", "Spotify Premium"]),
    ("Entertainment", "vlc.exe", "", ["{s} S01E0{n}.mkv - VLC media player"]),
    ("Social Media", "chrome.exe", "instagram.com", ["Instagram", "{w} (@{w}) • Instagram photos"]),
    ("Social Media", "chrome.exe", "x.com", ["Home / X", "{w} on X: \"new post {n}\""]),
    ("Social Media", "chrome.exe", "facebook.com", ["Facebook", "({n}) Facebook"]),
    ("Social Media", "chrome.exe", "reddit.com", ["r/{t} - Reddit", "Hot posts - Reddit"]),
    ("Social Media", "chrome.exe", "tiktok.com", ["TikTok - Make Your Day", "For You | TikTok"]),
    ("Social Media", "whatsapp.exe", "", ["WhatsApp", "({n}) WhatsApp"]),
    ("Social Media", "discord.exe", "", ["#general | Study Group - Discord", "@{w} - Discord"]),
    ("Other", "explorer.exe", "", ["Downloads", "This PC", "Documents"]),
    ("Other", "notepad.exe", "", ["Untitled - Notepad", "notes{n}.txt - Notepad"]),
    ("Other", "calc.exe", "", ["Calculator"]),
    ("Other", "chrome.exe", "weather.com", ["Weather forecast - {w}'s city"]),
    ("Other", "chrome.exe", "maps.google.com", ["Directions to station - Google Maps"]),
    ("Other", "systemsettings.exe", "", ["Settings", "Windows Update"]),
]


def main(n_rows: int = 300, seed: int = 7) -> None:
    rng = random.Random(seed)
    out = Path(__file__).with_name("synthetic_titles.csv")
    with open(out, "w", newline="", encoding="utf-8") as f:
        f.write("# SYNTHETIC DATA. Invented titles for testing the benchmark scripts. NOT real activity, NOT a real result.\n")
        w = csv.DictWriter(f, FIELDS)
        w.writeheader()
        for i in range(1, n_rows + 1):
            label, app, site, templates = rng.choice(GROUPS)
            title = rng.choice(templates).format(t=rng.choice(TOPICS), s=rng.choice(SHOWS), g=rng.choice(GAMES),
                                                 p=rng.choice(PROJECTS), n=rng.randint(1, 9), w=rng.choice(PEOPLE))
            if rng.random() < 0.12:               # some titles say nothing: confidence must drop on these
                title = rng.choice(GENERIC)
            w.writerow({"id": f"s{i:04d}", "origin": "synthetic", "source": "extension" if site else "agent",
                        "app": app, "site": site, "title": title, "label": label, "label_b": "", "notes": ""})
    print(f"wrote {n_rows} synthetic rows to {out}")


if __name__ == "__main__":
    main()
