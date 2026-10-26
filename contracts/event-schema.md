# Contract 1: Event schema (version 1)

Status: **draft for Fatima's review.** This is a shared file: any change needs approval from both Aleeza and Fatima.
The machine-readable version is [event.schema.json](event.schema.json). If the two ever disagree, fix them together.
The rules below match the sections "Rules from the PRD (fixed)" and "Team decisions" in [CLAUDE.md](../CLAUDE.md).

## Purpose

The browser extension (F1) and the Windows agent (F2, F3) both record events. All events share this one schema, so they merge into one timeline in time order (F5).

Events are stored **only on the user's PC**, in SQLite, for 30 days (F4). They never leave the PC. Only encrypted summaries are synced to the cloud; that is Contract 2 and is not covered here.

## Fields

Every event contains **all** of these fields. A value that does not apply is `null`, so nobody has to guess whether a field is missing.

| Name | Type | Required | Meaning |
|---|---|---|---|
| `schema_version` | string, always `"1"` | required | Version of this schema. |
| `id` | string | required | Unique id of the event (a UUID is recommended). |
| `start` | string, UTC time | required | When the event began. |
| `end` | string, UTC time | required | When the event ended. Not earlier than `start`. |
| `duration_seconds` | integer | required | Equals `end` minus `start`, in whole seconds. |
| `source` | `"extension"` or `"agent"` | required | Who recorded it: the browser extension or the Windows agent. |
| `app` | string or `null` | required | Process name, for agent events (for example `code.exe`). `null` for extension events and for idle events. |
| `domain` | string or `null` | required | Website domain only (for example `youtube.com`), for extension events. `null` otherwise. Reports use `domain`. |
| `url` | string or `null` | required | **Sanitized** URL (scheme, domain and path only), for extension events. `null` otherwise. Stored only on the PC, never synced. See "URL" below. |
| `title` | string or `null` | required | Window or tab title, only when permitted. `null` or masked otherwise. |
| `idle` | boolean | required | `true` exactly when `presence` is `"away"`. Set by the agent only. |
| `presence` | `"active"`, `"passive"`, `"uncertain"`, `"away"` or `null` | required | Is the user there? The extension writes `null`; the Windows agent fills it when it merges events into the timeline. See "Presence" below. |
| `category` | `Study`, `Work`, `Entertainment`, `Social Media`, `Other`, or `null` | required | Label from the on-device AI (F6). `null` until labelled. |
| `confidence` | number from 0 to 1, or `null` | required | How sure the labelling was. `null` until labelled. |
| `uncertain` | boolean | required | `true` when low confidence made the label fall back to `Other`. `false` otherwise. |
| `label_source` | `"model"`, `"rules"` or `null` | required | Who produced the label. `null` until labelled. |

No other fields are allowed. There is no field for page text, message bodies, a screenshot, keystrokes or typed text.

## Examples

All examples use made-up test data.

**1. Extension event** (browser, a YouTube Shorts page, 2 min 30 s; the video id in the path is masked with `*`). This is how the extension writes it, so `presence` is `null`; the agent fills it in when it merges the timeline:

```json
{
  "schema_version": "1",
  "id": "3f6c1a9e-5b2d-4e7a-9c11-0a4d8e2b7f10",
  "start": "2026-10-27T05:15:00Z",
  "end": "2026-10-27T05:17:30Z",
  "duration_seconds": 150,
  "source": "extension",
  "app": null,
  "domain": "youtube.com",
  "url": "https://www.youtube.com/shorts/*",
  "title": "Funny cats compilation",
  "idle": false,
  "presence": null,
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**2. Agent event** (desktop app in the foreground, 7 min 30 s, recent input, so presence is `active`):

```json
{
  "schema_version": "1",
  "id": "9a2e4c71-0d3b-4f58-8e62-7b1c5d9a3e44",
  "start": "2026-10-27T05:17:30Z",
  "end": "2026-10-27T05:25:00Z",
  "duration_seconds": 450,
  "source": "agent",
  "app": "code.exe",
  "domain": null,
  "url": null,
  "title": "main.cs - dla-agent",
  "idle": false,
  "presence": "active",
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**3. Idle event** (user away for 6 minutes, recorded by the agent; `idle` is `true` because presence is `away`):

```json
{
  "schema_version": "1",
  "id": "c47d0b2f-81a6-4a3e-b5f9-2e6d9c0a1b73",
  "start": "2026-10-27T05:25:00Z",
  "end": "2026-10-27T05:31:00Z",
  "duration_seconds": 360,
  "source": "agent",
  "app": null,
  "domain": null,
  "url": null,
  "title": null,
  "idle": true,
  "presence": "away",
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**4. Labelled event with low confidence** (the model was unsure, so the label fell back to `Other` and is marked uncertain):

```json
{
  "schema_version": "1",
  "id": "e81b3d56-2c9f-4d07-a4b8-6f0e7a5c2d19",
  "start": "2026-10-27T05:31:00Z",
  "end": "2026-10-27T05:33:00Z",
  "duration_seconds": 120,
  "source": "agent",
  "app": "notepad.exe",
  "domain": null,
  "url": null,
  "title": "Untitled",
  "idle": false,
  "presence": "active",
  "category": "Other",
  "confidence": 0.41,
  "uncertain": true,
  "label_source": "model"
}
```

**5. Passive event** (a lecture video playing in the foreground for 30 minutes, no keyboard or mouse input, so presence is `passive` and the time still counts as active):

```json
{
  "schema_version": "1",
  "id": "5b9d2e84-7a1c-4f36-8d05-c3e1a7b49f62",
  "start": "2026-10-27T05:33:00Z",
  "end": "2026-10-27T06:03:00Z",
  "duration_seconds": 1800,
  "source": "agent",
  "app": "vlc.exe",
  "domain": null,
  "url": null,
  "title": "Lecture 7 - Thermodynamics.mp4",
  "idle": false,
  "presence": "passive",
  "category": "Study",
  "confidence": 0.93,
  "uncertain": false,
  "label_source": "model"
}
```

**6. Uncertain-presence event** (a PDF reader in the foreground for 8 minutes, no input, no media, so the user may be reading: presence is `uncertain`):

```json
{
  "schema_version": "1",
  "id": "a4c7f1e0-3d82-4b59-9e16-0f5b8d2c6a73",
  "start": "2026-10-27T06:03:00Z",
  "end": "2026-10-27T06:11:00Z",
  "duration_seconds": 480,
  "source": "agent",
  "app": "acrobat.exe",
  "domain": null,
  "url": null,
  "title": "Chapter 4 - Thermodynamics.pdf",
  "idle": false,
  "presence": "uncertain",
  "category": "Study",
  "confidence": 0.86,
  "uncertain": false,
  "label_source": "model"
}
```

Presence and the label are separate. In example 5 the category is `Study` and presence is `passive`. In example 6 the presence is `uncertain` (is the user there?) while the label field `uncertain` is `false` (the model was sure about `Study`).

## Rules

### Format
- All times are **ISO 8601 in UTC with a trailing `Z`**, in whole seconds, for example `2026-10-27T05:15:00Z`. Local time is used only for display.
- `duration_seconds` equals `end` minus `start`.
- `domain` is the domain only (for example `youtube.com`), lowercase.
- `title` is stored only when permitted and may be `null` or masked. Sensitive titles are masked.
- Page text, message bodies, screenshots, keystrokes and typed text are never stored.

### URL
- `url` is a **sanitized URL for extension events only**: scheme, domain and path. For all other events it is `null`.
- The **query string and the fragment are always removed.**
- **Long random-looking path segments** and **known sensitive patterns** (reset or login tokens, session ids, email or account ids) are **replaced with `*`**. For example `https://www.youtube.com/shorts/Ab3dE9xQ2zK` becomes `https://www.youtube.com/shorts/*`.
- The cleaning happens **inside the extension**, before anything is sent on.
- The URL is stored **only on the PC and is never synced**. `domain` is kept as well, and **reports use `domain`**.
- The host in `url` belongs to `domain` (`www.youtube.com` belongs to `youtube.com`). The extension guarantees this.
- The JSON schema adds a last-line safety net: it rejects a query string, a fragment, a port, user details, a path segment of 32 or more id-like characters, and an email address in the path. It cannot do the masking itself.

### Rules from the PRD (fixed)
1. Extension and agent events share one schema (`contracts/event-schema.md`) and merge in time order, with overlap rules (F5).
2. Events are stored only on the PC in SQLite, auto-deleted after 30 days, and survive restarts (F4).
3. Idle time is never counted as active (F3).
4. Every label has a category (Study, Work, Entertainment, Social Media, Other) and a confidence. Low confidence falls back to uncertain `Other`. No screenshots or OCR (F6).
5. No screenshots, keystrokes or typed text. Page text is read only on the device, never uploaded and not stored (only the label is kept).
6. Hidden sites and apps are skipped before anything is read (F7).
7. Only encrypted summaries leave the PC, never raw events or page text (F8).

### Team decisions (agreed 26 Oct 2026, not in the PRD)
1. **URL:** the extension stores a sanitized URL: scheme, domain and path only. Query string and fragment are always removed. Long random-looking path segments and known sensitive patterns (reset or login tokens, session ids, email or account ids) are masked with `*`. Cleaning happens inside the extension before anything is sent. The URL stays on the PC and is never synced. `domain` is kept too, and reports use `domain`.
2. **Idle and presence:** decided only by the Windows agent from system-wide inactivity plus passive-activity signals (foreground media or audio, calls) and context (reading apps), with states active, passive, uncertain and away. The extension reports only browser-specific signals (active tab, tab changes, tab audible, video playing) and never decides presence.
3. **Overlap:** an extension event counts only while the browser (`chrome.exe` or `msedge.exe`) is the foreground app. When the extension and the agent both cover the same moment, count it once and keep the extension event. If the extension has no event, keep the agent event with `domain` null. Two agent events never overlap.
4. **Other:** `Other` with `uncertain: true` is a last resort, used only when the activity cannot reasonably be identified from the available context.
5. **Categorization:** use all available context (app, domain, URL, permitted title, on-device page or media text, time of day) instead of basic guesses. If the model is off, rules fill the category with `label_source: "rules"`.
6. **Hidden apps and sites:** no event is recorded at all.
7. **Private/incognito windows:** not recorded.
8. **Privacy:** never read message bodies, inboxes, DMs, input fields or typed text. Mask sensitive titles.

### Presence (new, needs Fatima's approval)
- `presence` is `"active"`, `"passive"`, `"uncertain"`, `"away"` or `null`.
- **Only the agent decides presence.** The extension writes `null`. The Windows agent fills `presence` when it merges events into the timeline.
- **`idle` is `true` exactly when `presence` is `"away"`.** No other presence value can have `idle: true`.
- How presence counts:
  - **active** and **passive** time count as active.
  - **uncertain** time counts as active with **lower weight** and is **never productive**.
  - **away** time is not active.
- Presence is separate from the AI category. Watching entertainment is still active (presence), and its category is `Entertainment`.
- The signals, the waits and their starting values are in the Decisions section below. The numbers are not tested yet.

### How these rules show up in an event
- **Idle event:** `idle: true`, `presence: "away"`, `app: null`, `domain: null`, `url: null`. It is never counted as active, and no active event is counted during idle time. Only the agent creates idle events; an extension event always has `idle: false`.
- **Timeline merge:** events are ordered by `start`. Overlaps follow team decision 3: an extension event counts only while the browser is in the foreground; when the extension and the agent cover the same moment it is counted once and the extension event is kept; if the extension has no event, the agent event is kept with `domain: null`; two agent events never overlap.
- **Labels:** `category`, `confidence` and `label_source` are added later by the on-device AI, so they are `null` until then. If `confidence` is below the threshold, `category` is `Other` and `uncertain` is `true`. If the model is off, rules fill `category` and `label_source` is `"rules"`.
- **Hidden apps and sites, private windows:** they produce no event at all, so there is nothing in the data to filter out later.
- **Retention:** events older than 30 days are deleted automatically.

## Decisions

### Idle threshold and presence (decided by the team, numbers need testing)
- Do not rely only on a keyboard/mouse timer. The Windows agent decides presence from several signals: keyboard/mouse inactivity, computer lock/sleep/display off/sign-out, sound or media playing in the FOREGROUND app or active tab, and calls where detectable (microphone or camera in use).
- A signal counts only if it matches what the user is looking at. An open app alone never counts as activity, and music playing in a background app does not count.
- Presence states:
  - **active**: recent input.
  - **passive**: no input, but foreground media or a call is running.
  - **uncertain**: no input and no media, but a reader, slide or document app is in the foreground, so reading is possible.
  - **away**: locked, asleep, display off, or no input and no signals after the waits below.
- Starting values, **NOT yet tested**: 3 minutes of input inactivity; 10 minutes extra wait for reading apps; about 90 minutes with no input at all as a cap for passive activity, after which the time becomes uncertain, then away.
- If the system cannot tell whether the user is present, the time is marked uncertain. It is never guessed as active and never counted as productive. It counts less in the score.
- Presence is separate from AI categories. Watching entertainment is still active (presence), and its category is Entertainment.
- No screenshots, keystrokes, typed text or private data are collected. Sound detection checks only whether and which app is making sound, never the audio itself. Input detection sees only timing, never which keys.
- Implementation notes (for later days): `GetLastInputInfo` for input timing, session lock and power events, Windows audio sessions and system media controls for playback, the Windows microphone/camera usage record for calls; in the extension, the tab "audible" flag and whether a video is playing.
- Status: **needs Fatima's approval**, because it adds a `presence` field to the event format (commit 2) and extends the PRD's simple "inactivity past a set threshold" rule.

### AI model shortlist (candidates for Day 7 testing, final model NOT selected)
- The AI categorizes activity as Study, Work, Entertainment, Social Media or Other, runs locally in the C# agent using ONNX Runtime, works offline, and uses rule-based classification if the model is unavailable. Low confidence gives Other with `uncertain: true`.
- Candidates:
  1. all-MiniLM-L6-v2 + trained classifier
  2. bge-small-en-v1.5 + trained classifier
  3. all-MiniLM-L6-v2 zero-shot (no training, compares text with a description of each category)
  - Baseline for comparison: rules only.
- Measures: accuracy on Fatima's labelled test set (overall and per category), how often the result is uncertain Other, speed per prediction, memory usage, model file size, and compatibility with ONNX Runtime in C# including the text tokenizer.
- Proposed limits (to confirm with Fatima; **not from the PRD**): model file 150 MB or less; agent memory 250 MB or less with the model loaded; 200 ms or less per prediction on a normal laptop; under 3% CPU on average; model off below 8 GB RAM and rules used instead. The model must beat the rules-only baseline; a numeric accuracy target is set after the baseline is measured, and results are reported honestly.
- Both models are English; Urdu or Roman-Urdu titles may be weaker, so Fatima should include a few in the test set.
- Selection: test on Day 7, choose the most accurate candidate within the limits (smaller and faster if close).
- Still to decide: the confidence threshold.

### Encryption key (PRD open decision; decided by Aleeza on 27 Oct 2026; Fatima's approval is still needed in the pull request)
- **Decision:** for the 15-day build, summaries are protected by TLS in transit and encryption at rest on the server. The server holds the key (a secret in the server's environment or secret store, never in the repository). The dashboard does NOT decrypt in the browser; the server reads summaries to compute reports, score, forecast and recovery plan.
- **Why:** the server must read summaries to compute the score, forecast and recovery plan (F11, F16, F18), and the PRD includes password reset (F9). A key derived from the user's password would make server-side analytics impossible and would make old data unreadable after a password reset.
- **What leaves the PC:** only the daily summary (category totals, top app and site names, switches per hour, interruptions, confidence information). Never raw events, URLs, window titles or page text.
- **Honest limit:** this is not end-to-end encryption. The server can read summaries. The consent screen must say so plainly.
- Sync can be turned off by the user (F8).
- **Secrets:** never committed (the repository is public). Each developer uses their own `.env` file; production secrets live only on the server.
- **Upgrade path** (not in the 15 days): end-to-end encryption of the most sensitive parts (app and site names) with a user-held key. The summary format carries a version number so this can be added later.
- **Test:** during sync, use a network proxy to confirm that the traffic is TLS and that the payload contains only the summary fields.
