# Category rules: test set and measuring script

A labelled test set for the category rules ([contracts/category-rules.md](../../contracts/category-rules.md)), a script that measures how well the rules do on it, and tests. It is also meant to be the test set for choosing the on-device model on Day 7.

**This repository is public.** The test set holds only well-known public apps and websites and made-up generic titles. No real browsing, no real window titles, no personal data. Keep it that way.

## What is here

| File | What it is |
|---|---|
| `testset.json` | 510 labelled examples (one per line). |
| `testset.schema.json` | The format of the test set. |
| `category_rules.py` | The reference implementation of how the rules are applied. The C# loader in the contract must give the same answers. |
| `evaluate.py` | Measures the rules on the test set. Plain Python, no packages. |
| `test_category_rules.py` | 72 automatic tests (rules against their schema, matching logic, test set, the script). |

## An example

```json
{"id": "t169", "batch": "A", "app": null, "site": "youtube.com", "title": "Lecture 12 - Operating systems full course", "category": "Study", "ambiguous": true, "also_acceptable": ["Entertainment"], "note": "a study video on a video site: the site says entertainment, the content says study"}
```

- `app` (a process name as the agent reports it, such as `Code.exe`) **or** `site` (the host of the page, such as `docs.google.com`): exactly one of them.
- `title`: an optional, made-up, generic title, or `null`. Titles are for the on-device model on Day 7. The rules do not use them.
- `category`: the **true** category (see "How the labels were decided").
- `ambiguous`: `true` when sensible people could label it differently, or when it depends on how the app is used. Then `also_acceptable` lists the other categories that would not be wrong, and `note` says why.
- `batch`: `A` or `B` (see below).

## How the labels were decided

One person (Claude, working for Fatima) gave each label from what the app or site is **mainly used for by a typical user**, not by one person:

- **Study**: courses, learning platforms, encyclopedias, academic papers, flashcards, study tools, school and university sites.
- **Work**: office and productivity tools, programming tools, work chat and meetings, email, cloud consoles, professional design tools.
- **Entertainment**: video and music streaming, games and game stores, entertainment media.
- **Social Media**: social networks, forums, personal messaging apps.
- **Other**: system tools, browsers themselves, search engines, shopping, banking, news, maps, weather, utilities, and anything that can not reasonably be placed.

These are one person's judgements and have not been checked by anyone else. **Fatima or Aleeza should review the labels**, especially the ambiguous ones.

## The two batches, and why

The first version of the rules scored **288 out of 288** on the clear examples. That number says almost nothing: the same author wrote the rules and the examples in the same session, so the rules naturally know the examples. To get a number that means something:

- **Batch A** (349 examples) was written first. The rules were written with it in mind. Its score only shows that the rules file works.
- **The rules were then frozen** (SHA-256 of `category-rules.json` at that moment: `8223CB700CF17E6B2E68FAE99F7456091010B47F32863E649649CD83CF2FC7C7`) and **batch B** (161 examples) was written afterwards, from other angles: regional websites, engineering and course tools, Google sub-sites, game launchers, system helpers. The rules were **not** changed after seeing the batch B results.

**Rule for the future:** if you add the apps and sites that batch B missed to the rules, batch B is no longer a test, it is training data. Then write a **new batch** (give it the letter `C` and add it to the `batch` list in `testset.schema.json`) before quoting any new accuracy.

## Run it

You need the project's Python with `pytest` and `jsonschema` installed (see [cloud/backend/README.md](../backend/README.md), step 1, on the `fatima/cloud` branch). From the repository root:

```
python cloud/category-rules-testset/evaluate.py
python cloud/category-rules-testset/evaluate.py --show-errors
python cloud/category-rules-testset/evaluate.py --domain-only
python -m pytest cloud/category-rules-testset
```

`evaluate.py` needs only Python. `--show-errors` lists every wrong answer. `--domain-only` pretends the agent passes only the registrable domain (`docs.google.com` becomes `google.com`).

## Results for rules version 1

Measured on 29 Oct 2026. **Clear** examples are the score. **Ambiguous** examples are reported separately and are not part of it. "95% CI" is a 95% confidence interval (Wilson): it shows how much a number could move because the group is small.

### Clear examples

| | Right | Of | Share | 95% CI |
|---|---:|---:|---:|---|
| **Batch B (the honest test)** | **41** | **132** | **31.1%** | 24-39% |
| &nbsp;&nbsp;apps only | 11 | 41 | 26.8% | |
| &nbsp;&nbsp;sites only | 30 | 91 | 33.0% | |
| Batch A (rules written with it in view) | 288 | 288 | 100.0% | 99-100% |

Batch B, by the **true** category (of the examples that really are X, how many did the rules label X):

| True category | Right | Of | Share | 95% CI |
|---|---:|---:|---:|---|
| Study | 4 | 23 | 17.4% | 7-37% |
| Work | 1 | 32 | 3.1% | 1-16% |
| Entertainment | 0 | 30 | 0.0% | 0-11% |
| Social Media | 0 | 11 | 0.0% | 0-26% |
| Other | 36 | 36 | 100.0% | 90-100% |

The `Other` row is **not** a success: `Other` is what the rules say when they know nothing, so every real `Other` is "right" by default.

Batch B, by the category the **rules said** (when the rules say X, how often is it really X): Study 4 of 4, Work 1 of 1, Entertainment and Social Media never said, `Other` 36 of 127 (28.3%).

**What this shows:**

- In batch B, **127 of 132 answers were the default `Other`** because no rule matched, and **91 of those were wrong**. Every mistake was of this kind ("Work/Entertainment/Study/Social Media -> Other"). The rules never gave a wrong specific category; in this batch they only said a specific category 5 times (all right), a small number.
- So the rules are **cautious but know few names**. They behave as a fallback should (they do not guess), but alone they would label most of an unfamiliar day as `Other`.

### Ambiguous examples (90)

| | Right | Of | Share | 95% CI |
|---|---:|---:|---:|---|
| Same as the first label | 58 | 90 | 64.4% | 54-74% |
| Any acceptable label | 72 | 90 | 80.0% | 71-87% |
| &nbsp;&nbsp;batch A, first label / any acceptable | 53 / 58 | 61 | 86.9% / 95.1% | |
| &nbsp;&nbsp;batch B, first label / any acceptable | 5 / 14 | 29 | 17.2% / 48.3% | |

The rules decide by name, so a study video on YouTube is always `Entertainment` (the two "Lecture" and "Calculus" examples), and a PDF reader is always `Other`. Telling these apart needs the title, the text or the context: that is the model's job (F6).

### If the agent passes only the domain

| | With the host of the page | With the registrable domain only |
|---|---:|---:|
| Batch A, clear | 288 / 288 (100.0%) | 272 / 288 (94.4%) |
| Batch B, clear | 41 / 132 (31.1%) | 41 / 132 (31.1%) |

16 clear examples in batch A need the host: `docs.google.com`, `sheets.google.com`, `mail.google.com`, `meet.google.com`, `scholar.google.com`, `teams.microsoft.com`, `aws.amazon.com`, `music.apple.com` and 8 more. Without the host they fall to `Other`. This is open question 1 for Aleeza in the contract.

## What these numbers do NOT show

- **They are not the accuracy on real users.** Everything was written by one author. Batch B was written after the rules were frozen, which removes fitting, but it is still one person's idea of "well-known", and it was written to look outside what the rules cover (regional and specialist names), so **31% is probably lower than a real user would see**, while 100% on batch A is far higher. The real figure lies somewhere in between and **has not been measured**.
- The labels are one person's judgement and have not been double-checked.
- Real activity includes apps and sites that are in no list. The `Other` default is correct for some of them and wrong for others.
- The sample is small: the intervals above show it.
- Rules never read titles, so they can not separate a lecture from a film on the same site.

To measure real accuracy, a second person should label a sample of **names only** (no titles, no content) taken from their own real usage, which is a separate, privacy-checked step. Do not put real usage in this public repository.

## Adding examples

Add a line to `testset.json` with the next id (`t511`...), in the same shape. Run `python -m pytest cloud/category-rules-testset`: the schema, the id order, the privacy checks and the "no example twice" check run on it. Use only well-known public names and made-up generic titles. Never add a title for a webmail or chat site (the extension never keeps those titles).
