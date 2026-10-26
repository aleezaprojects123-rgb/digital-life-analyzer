# Contract 1: Event schema (version 1)

Status: **draft for Fatima's review.** This is a shared file: any change needs approval from both Aleeza and Fatima.
The machine-readable version is [event.schema.json](event.schema.json). If the two ever disagree, fix them together.

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
| `domain` | string or `null` | required | Website domain only (for example `youtube.com`), for extension events. `null` otherwise. |
| `title` | string or `null` | required | Window or tab title, only when permitted. `null` or masked otherwise. |
| `idle` | boolean | required | `true` if the user was away. Set by the agent only. |
| `category` | `Study`, `Work`, `Entertainment`, `Social Media`, `Other`, or `null` | required | Label from the on-device AI (F6). `null` until labelled. |
| `confidence` | number from 0 to 1, or `null` | required | How sure the labelling was. `null` until labelled. |
| `uncertain` | boolean | required | `true` when low confidence made the label fall back to `Other`. `false` otherwise. |
| `label_source` | `"model"`, `"rules"` or `null` | required | Who produced the label. `null` until labelled. |

No other fields are allowed. In particular there is no field for a URL, page text or a screenshot.

## Examples

All examples use made-up test data.

**1. Extension event** (browser, Khan Academy tab, 2 min 30 s):

```json
{
  "schema_version": "1",
  "id": "3f6c1a9e-5b2d-4e7a-9c11-0a4d8e2b7f10",
  "start": "2026-10-27T05:15:00Z",
  "end": "2026-10-27T05:17:30Z",
  "duration_seconds": 150,
  "source": "extension",
  "app": null,
  "domain": "khanacademy.org",
  "title": "Thermodynamics: Lecture 5",
  "idle": false,
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**2. Agent event** (desktop app in the foreground, 7 min 30 s):

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
  "title": "main.cs - dla-agent",
  "idle": false,
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**3. Idle event** (user away for 6 minutes, recorded by the agent):

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
  "title": null,
  "idle": true,
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
  "title": "Untitled",
  "idle": false,
  "category": "Other",
  "confidence": 0.41,
  "uncertain": true,
  "label_source": "model"
}
```

## Rules

### Time
- All times are **ISO 8601 in UTC with a trailing `Z`**, in whole seconds, for example `2026-10-27T05:15:00Z`. Local time is used only for display.
- `duration_seconds` equals `end` minus `start`.

### What is and is not stored
- `domain` is the **domain only** (`youtube.com`), never the URL, path or query.
- `title` is stored **only when permitted** and may be `null` or masked.
- Page text, message bodies, screenshots, keystrokes and typed text are **never** stored.

### Idle (F3)
- Idle is decided **by the agent only**.
- An idle event has `idle: true` and `app: null`, and is **never counted as active**.
- No active event is counted during idle time.

### Timeline merge (F5)
These are **team rules, not stated in the PRD**:

1. Events are ordered by `start`.
2. An extension event counts only while the browser (`chrome.exe` or `msedge.exe`) is the foreground app, according to the agent.
3. When an extension event and the agent's browser event overlap, the same moment is counted **once**, and the **extension event is kept** because it is more specific.
4. If the agent sees the browser in the foreground but the extension has no event (for example the extension was asleep), the **agent event is kept**, with `domain: null`.
5. Two agent events never overlap.

### Hidden apps and sites, private windows (F7)
- Hidden apps and sites produce **no event at all**.
- Incognito / private windows are **not recorded**. (Team rule, not in the PRD.)

### Labels (F6)
- `category` and `confidence` are added **later by the on-device AI**. Until then they are `null`.
- If `confidence` is below the threshold, `category` is `Other` and `uncertain` is `true`.
- If the model is off, rules may fill `category`, with `label_source: "rules"`.

### Retention (F4)
- Events older than **30 days are deleted automatically**.

## Decisions

### Idle threshold
TBD, Task 3

### AI model shortlist
TBD, Task 3

### Encryption key
TBD, Task 3
