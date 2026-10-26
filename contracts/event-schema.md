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
| `idle` | boolean | required | `true` if the user was away. Set by the agent only. |
| `category` | `Study`, `Work`, `Entertainment`, `Social Media`, `Other`, or `null` | required | Label from the on-device AI (F6). `null` until labelled. |
| `confidence` | number from 0 to 1, or `null` | required | How sure the labelling was. `null` until labelled. |
| `uncertain` | boolean | required | `true` when low confidence made the label fall back to `Other`. `false` otherwise. |
| `label_source` | `"model"`, `"rules"` or `null` | required | Who produced the label. `null` until labelled. |

No other fields are allowed. There is no field for page text, message bodies, a screenshot, keystrokes or typed text.

## Examples

All examples use made-up test data.

**1. Extension event** (browser, a YouTube Shorts page, 2 min 30 s; the video id in the path is masked with `*`):

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
  "url": null,
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
  "url": null,
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
  "url": null,
  "title": "Untitled",
  "idle": false,
  "category": "Other",
  "confidence": 0.41,
  "uncertain": true,
  "label_source": "model"
}
```

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
2. **Idle:** decided only by the Windows agent from system-wide inactivity. The extension reports only browser-specific activity (active tab, tab changes) and never decides idle.
3. **Overlap:** an extension event counts only while the browser (`chrome.exe` or `msedge.exe`) is the foreground app. When the extension and the agent both cover the same moment, count it once and keep the extension event. If the extension has no event, keep the agent event with `domain` null. Two agent events never overlap.
4. **Other:** `Other` with `uncertain: true` is a last resort, used only when the activity cannot reasonably be identified from the available context.
5. **Categorization:** use all available context (app, domain, URL, permitted title, on-device page or media text, time of day) instead of basic guesses. If the model is off, rules fill the category with `label_source: "rules"`.
6. **Hidden apps and sites:** no event is recorded at all.
7. **Private/incognito windows:** not recorded.
8. **Privacy:** never read message bodies, inboxes, DMs, input fields or typed text. Mask sensitive titles.

### How these rules show up in an event
- **Idle event:** `idle: true`, `app: null`, `domain: null`, `url: null`. It is never counted as active, and no active event is counted during idle time. Only the agent creates idle events; an extension event always has `idle: false`.
- **Timeline merge:** events are ordered by `start`. Overlaps follow team decision 3: an extension event counts only while the browser is in the foreground; when the extension and the agent cover the same moment it is counted once and the extension event is kept; if the extension has no event, the agent event is kept with `domain: null`; two agent events never overlap.
- **Labels:** `category`, `confidence` and `label_source` are added later by the on-device AI, so they are `null` until then. If `confidence` is below the threshold, `category` is `Other` and `uncertain` is `true`. If the model is off, rules fill `category` and `label_source` is `"rules"`.
- **Hidden apps and sites, private windows:** they produce no event at all, so there is nothing in the data to filter out later.
- **Retention:** events older than 30 days are deleted automatically.

## Decisions

### Idle threshold
TBD, Task 3

### AI model shortlist
TBD, Task 3

### Encryption key
TBD, Task 3
