# Contract 2: Daily summary format (version 1)

Status: **draft written by Fatima, needs Aleeza's approval.** This is a shared file: any change needs approval from both Aleeza and Fatima.
The machine-readable version is [summary.schema.json](summary.schema.json). If the two ever disagree, fix them together.
Samples with made-up test data: [samples/summary-valid.json](samples/summary-valid.json) (passes) and [samples/summary-invalid.json](samples/summary-invalid.json) (must be rejected, see "Samples").
Anything marked **[PROPOSAL]** is Fatima's suggestion and is not agreed until Aleeza approves it.
The rules below match the sections "Rules from the PRD (fixed)" and "Team decisions" in [CLAUDE.md](../CLAUDE.md).

## Purpose

The Windows agent turns one day of events into **one small summary** and syncs it to the cloud (F8). The server uses it to build the reports, score, forecast, nudge input and recovery plan (F10 to F13, F16, F18) and the dashboard (F14). The raw events (Contract 1) never leave the PC.

## What leaves the PC, and what never does

| Leaves the PC (in the summary) | Never leaves the PC |
|---|---|
| Seconds per presence state and per category | Raw events |
| Switches and interruptions per hour | URLs (even sanitized ones) |
| Up to 10 app names (process names) and 10 domains with seconds | Window or tab titles |
| Label quality numbers (model/rules seconds, mean confidence) | Page, media or document text |
| The date, the UTC offset and the time it was built | Screenshots, keystrokes, typed text |

The schema has **no field** that could hold a title, a URL or text, and it rejects any extra field. Hidden apps and hidden sites, and private windows, produce no events (Contract 1), so they cannot appear here at all. Summaries are built only from data that was already allowed.

## How it is sent (what "encrypted" means) **[PROPOSAL]**

- Decision of 27 Oct 2026 (in Contract 1): the summary travels over **TLS (HTTPS)**, and the server **encrypts it at rest** with a key held in the server's environment. The dashboard does not decrypt anything. This is not end-to-end encryption: the server can read summaries, because it must compute the score and forecast. The consent screen must say so.
- So the request body **is** the summary JSON, nothing added. Test (from the decision): through a network proxy, check the traffic is TLS and the payload holds only the fields in this contract.
- One request per day: `PUT /api/v1/summaries/{date}` with the header `Authorization: Bearer <token>`.
  - `{date}` must equal `date` in the body.
  - One summary per user per date. A new upload for the same date **replaces** the old one if its `generated_at` is newer; an older or equal one is ignored.
  - The agent may upload a partial day (today so far) and upload again later.
  - The server rejects a body over 64 KB, a body that fails the schema, and a body that fails the checks below, with a short error that names the field and never echoes the content.
- The exact API (errors, token handling) is written in the API guide, not here. This contract fixes only the body and these rules.

## Fields

Every field is required. A value that does not apply is `0` or `null` (only `mean_confidence` can be `null`). No other fields are allowed anywhere.

### Top level

| Name | Type | Meaning |
|---|---|---|
| `schema_version` | string, always `"1"` | Version of this format. A change that breaks readers raises it. |
| `date` | string `YYYY-MM-DD` | The user's **local** calendar day. |
| `utc_offset_minutes` | integer, -720 to 840 | Local time minus UTC, in minutes (Pakistan: `300`). Places the 24 hours on the clock. |
| `generated_at` | UTC time | When the agent built this summary (ISO 8601, trailing `Z`, whole seconds, as in Contract 1). |
| `presence_seconds` | object | Seconds in each presence state. See below. |
| `category_seconds` | object | Seconds per category, split by presence. See below. |
| `labels` | object | How the labels were made. See below. |
| `hourly` | list of 24 | One entry per local hour. See below. |
| `top_apps` | list, at most 10 | Apps with the most time. |
| `top_sites` | list, at most 10 | Websites with the most time. |

There is no user id, device name or account name in the summary. The server knows the user from the login token.

### `presence_seconds`

`active`, `passive`, `uncertain`, `away`: whole seconds, as set by the agent (Contract 1, "Presence").

- Their total is the **tracked** time of the day. Time when tracking was paused or the PC was off is in none of them, so nothing is invented for it.
- `active + passive + uncertain` is the **counted** time. `away` is idle time and is never counted as active (F3).

### `category_seconds`

One entry for each of `Study`, `Work`, `Entertainment`, `Social Media`, `Other`. Each entry has `active`, `passive`, `uncertain` (presence states; **seconds of that category in that presence state**). `away` is not here, because away time has no category.

- `Other` includes the uncertain `Other` (low confidence). How much of it is uncertain is in `labels`.
- The category is the label from Contract 1. If an event is still unlabelled when the summary is built, the agent counts it as `Other` and as uncertain (see the open questions).

### `labels`

| Name | Meaning |
|---|---|
| `model_seconds` | Counted seconds labelled by the on-device model. |
| `rules_seconds` | Counted seconds labelled by the rule fallback. `model_seconds + rules_seconds` equals all counted seconds. |
| `uncertain_other_seconds` | Seconds inside `Other` that are the uncertain fallback. At most the `Other` total. |
| `mean_confidence` | Average confidence of the model labels, weighted by seconds (0 to 1). `null` exactly when `model_seconds` is `0` (model off). |

### `hourly`

Exactly **24** entries, in order, for local clock hours 0 to 23. Each entry:

| Name | Meaning |
|---|---|
| `hour` | 0 to 23. Equals its position in the list. |
| `switches` | Changes of app or site in that hour (F13). |
| `interruptions` | Interruptions in that hour (F13). Never more than `switches`. |
| `away_seconds` | Away seconds in that hour. |
| `category_seconds` | Counted seconds per category in that hour (five numbers, no presence split). With `away_seconds` the total is at most 3600. |

The hourly category seconds are what the forecast (F16) and the "best focus hours" (F15) need: the switches alone do not say *what* the user was doing in a risky hour.

### `top_apps` and `top_sites`

- `top_apps` items: `name`, `seconds`, `category`. `name` is the **process name only** (for example `code.exe`): letters, digits and `. _ ( ) + -`, no spaces, no path. This keeps a window title from being put in this field.
- `top_sites` items: `domain`, `seconds`, `category`. `domain` is the domain only, lowercase, as in Contract 1. Never a URL.
- `seconds` is the counted time (active, passive and uncertain, not away) in that app or site.
- `category` is the category with the most time for that app or site today.
- Sorted by `seconds`, largest first. An app or site must not be listed twice. Browser time that the extension covered is listed under the site (Contract 1, overlap rule), not under the browser app.

## Rules

### Time
- Seconds are whole numbers, 0 or more. One day is at most 90000 seconds (25 hours leaves room for a daylight-saving day); one hour is at most 3600.
- The summary of a day is built from the day's local clock (`utc_offset_minutes`). Hours are local clock hours.

### Switch **[PROPOSAL]**
A **switch** is a change of app (agent events) or site (extension events) between two **consecutive counted events with no away time between them**. Moving between pages of the same domain is not a switch. The switch is counted in the hour in which the new event starts.

### Interruption **[PROPOSAL, a v1 rule so Aleeza can build; the final rule is settled on Day 7]**
An **interruption** is a switch where the event before it was `Study` or `Work` and lasted at least **5 minutes**, and the event after it is `Entertainment` or `Social Media`. It is counted in the hour in which the new event starts. Every interruption is also a switch.
Changing this rule changes what the numbers mean, so it needs a change to this file, with approval from both.

### How presence counts in the score **[PROPOSAL]**
The numbers in the summary are plain seconds. The **weights live on the server**, in the score formula (F11), so they can be tuned without changing the agent.

| Presence | Weight | Productive? |
|---|---|---|
| `active` | 1.0 | Yes, if the category is Study or Work |
| `passive` | 1.0 | Yes, if the category is Study or Work |
| `uncertain` | **0.5 (proposal)** | **Never productive** (Contract 1) |
| `away` | 0 | No |

- Weighted active time = `active + passive + 0.5 × uncertain`.
- Productive time = Study and Work seconds in `active` and `passive` only.
- **Why 0.5 for `uncertain`:** `uncertain` means no input and no media, but a reading app is in front, so reading is *possible*, not proven. Contract 1 says it must count less than active time and must never be guessed as active. Half is the middle between "assume the user is there" (1) and "assume the user is away" (0), it is easy to show in the visible formula (F11), and the idle thresholds are not tested yet, so a more exact number would be a made-up precision. After real data arrives (Day 8), if `uncertain` is a large share of the day, we revisit this number.
- Worked example, from the valid sample: weighted active = 24935 + 9080 + 0.5 × 3505 = 35767.5 s. Productive = (5760 + 2400) Study + (7650 + 450) Work = 16260 s.

## Checks the server makes

JSON Schema cannot add numbers up. The agent must build the summary so these hold, and the server rejects a summary where they do not (so a wrong summary is found at once, not weeks later):

1. For each of `active`, `passive`, `uncertain`: the five categories add up to the value in `presence_seconds`.
2. `labels.model_seconds + labels.rules_seconds` equals `active + passive + uncertain`.
3. `labels.uncertain_other_seconds` is at most the `Other` total and at most `model_seconds`.
4. `hourly[i].hour` equals `i`. Each hour's `interruptions` is at most its `switches`. Each hour's category seconds plus `away_seconds` is at most 3600.
5. For each category, the 24 hourly values add up to the category total (active + passive + uncertain). The 24 `away_seconds` add up to `presence_seconds.away`.
6. `top_apps` and `top_sites` are sorted largest first and list no app or site twice. Together they are at most the counted time, and for each category the listed items are at most that category's total.
7. The tracked time (`active + passive + uncertain + away`) is at most 90000.

## Samples

- [samples/summary-valid.json](samples/summary-valid.json): one made-up day (27 Oct 2026, UTC+5, tracked from about 08:00 to 22:00). Passes the schema and all seven checks.
- [samples/summary-invalid.json](samples/summary-invalid.json): the same day with **six planted mistakes**, so it must be rejected by the schema, with exactly these problems:
  1. an extra field `window_titles` (titles are never allowed);
  2. `date` written as `29/10/2026`;
  3. `presence_seconds.uncertain` is `-5`;
  4. `labels.mean_confidence` is `1.4`;
  5. `top_apps[0].name` is a document title instead of a process name;
  6. `top_sites[0]` has an extra field `url`.

All data is made up. Never put real activity in a sample: the repository is public.

## Open questions for Aleeza

1. **Hourly category seconds and the presence split are more than the plan's list** (category totals, top apps and sites, switches per hour, label confidence, date). I added them because the score needs the presence split (uncertain is never productive) and the forecast needs *what* happened in each hour. They are still only totals. Do you accept them, and can the agent build them?
2. **Switch and interruption rules** above: can the agent compute them as written? Is the 5-minute / Study-Work-to-Entertainment-Social rule workable as a v1?
3. **Time zone and daylight saving.** I use a UTC offset in minutes for the day. Is that easy to get in C# (`TimeZoneInfo`)? On a day with a repeated or missing hour, I propose to ignore the odd hour and clip to 3600 seconds.
4. **App names.** Is the process name (no spaces) always enough? The schema rejects spaces on purpose, to keep titles out. If some process names have spaces, tell me and we choose a rule (for example replace each space with `_`).
5. **Unlabelled events.** If an event has no label when the summary is built (model still loading, a crash), I propose the agent counts it as `Other`, uncertain, with source `model`. Alternatively it waits and labels first. Which?
6. **Uncertain `Other` from the rules fallback.** The Contract 1 schema says `uncertain: true` only with `label_source: "model"`, but team decision 4 (`Other` as a last resort) can also happen with the rules. If you change Contract 1 for that, `uncertain_other_seconds` and check 3 should follow. (This is the same point I raised in the Contract 1 review.)
7. **When does the agent upload, and for which days?** I propose: after midnight for the finished day, at start-up for any missed day in the last 30 days, and optionally every few hours for today. Same `date` replaces the old summary. Is that fine?
8. **One PC per account.** One summary per user per date means a second PC would overwrite the first. I propose one PC per account for the 15 days. OK?
9. **How does the agent log in?** The sync client needs a token (`Authorization: Bearer`). Options: the user logs in inside the agent once, or the dashboard shows a short pairing code. This is a Day 8 problem, but it affects the agent's UI. Which do you prefer?
10. **Privacy-control status and the audit log for the dashboard.** The plan says the dashboard needs them. I left them out of version 1 so the summary stays small. Do you want a small `privacy` block (for example paused seconds, a count of content reads) in version 1, or later?
11. **"Encrypted before upload" (plan) against "TLS plus encryption at rest" (decision of 27 Oct).** This contract follows the decision: the body is plain JSON inside TLS. Do you agree that this satisfies the PRD test "traffic contains only encrypted summaries"?
