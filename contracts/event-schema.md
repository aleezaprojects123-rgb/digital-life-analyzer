# Contract 1: Event schema (version 1)

Status: **revised for the extension-only architecture; needs Fatima's approval** (this is a shared file: any change needs approval from both Aleeza and Fatima).
The machine-readable version is [event.schema.json](event.schema.json). If the two ever disagree, fix them together.

**What changed in this revision.** The Windows desktop tracker was removed: the Chrome/Edge extension is now the **only** activity collector. So this contract no longer has
desktop (`agent`) events, `app`, `idle` or `presence`, and it now lists the fields the browser event really has (`platform`, `activity_type`, `content_count`, `session_id`).
`switched_count` and `browser_idle` were tried and removed; they are not part of the event. The sections "Rules from the PRD" and "Team decisions" below follow
[CLAUDE.md](../CLAUDE.md) except where they are marked as removed.

## Purpose

The browser extension (F1) records events. Every event is one **browser activity period**: the time between the moment a page comes to the front and the moment the next page does
(or the browser loses focus). All events share this one schema.

Events are stored **only on the user's PC**, in SQLite, for 30 days (F4). They never leave the PC. Only encrypted summaries are synced to the cloud; that is Contract 2 and is not covered here.

The flow is: **Chrome extension -> browser activity events -> local processing and storage -> SQLite -> analytics / UI.**

## Fields

Every event contains **all** of the required fields. A value that does not apply is `null`, so nobody has to guess whether a field is missing.

| Name | Type | Required | Meaning |
|---|---|---|---|
| `schema_version` | string, always `"1"` | required | Version of this schema. |
| `id` | string | required | Unique id of the event (a UUID is recommended). |
| `start` | string, UTC time | required | When the period began. |
| `end` | string, UTC time | required | When the period ended. Not earlier than `start`. |
| `duration_seconds` | integer | required | Equals `end` minus `start`, in whole seconds. |
| `source` | string, always `"extension"` | required | Who recorded it. The extension is the only collector. |
| `platform` | `youtube`, `instagram`, `tiktok`, `reddit`, `facebook`, `whatsapp`, `spotify`, `netflix`, `github`, `google` or `unknown` | required | The service the page belongs to. `unknown` for every other website. |
| `domain` | string | required | Website domain only (for example `youtube.com`). Reports use `domain`. |
| `url` | string | required | **Sanitized** URL (scheme, domain and path only). Stored only on the PC, never synced. See "URL" below. |
| `title` | string or `null` | required | Tab title, only when permitted. `null` or masked otherwise. |
| `activity_type` | `webpage`, `search`, `video`, `short_video`, `reel`, `music`, `post`, `article`, `document`, `social_feed`, `messaging`, `meeting`, `shopping`, `game` or `unknown` | required | What kind of page the period was spent on. |
| `content_count` | integer, 0 or 1 | required | `1` when the period is where the user moved to a new content item that can be told apart reliably (a Reel, a video, a Short, a post opened on its own address), else `0`. Never invented for feeds, home pages or search pages. |
| `session_id` | string or `null` | required | The recording session. The extension writes `null`; the local program stamps the id of the current session when it accepts the event. |
| `category` | `Study`, `Work`, `Entertainment`, `Social Media`, `Other`, or `null` | required | Label from the on-device AI (F6). `null` until labelled. |
| `confidence` | number from 0 to 1, or `null` | required | How sure the labelling was. `null` until labelled. |
| `uncertain` | boolean | required | `true` when low confidence made the label fall back to `Other`. `false` otherwise. |
| `label_source` | `"model"`, `"rules"` or `null` | required | Who produced the label. `null` until labelled. |
| `media_title`, `artist`, `album`, `media_type`, `playback`, `media_app` | string or `null` | optional | Added by the local program when the Windows media controls match the page that was playing (for example the song title). Absent when nothing matched. |

No other fields are allowed. There is no field for page text, message bodies, a screenshot, keystrokes, typed text, the name of a desktop program, or whether the user was idle.

## Examples

All examples use made-up test data.

**1. A YouTube Short** (2 min 30 s; the video id in the path is masked with `*`; it is the one new item, so `content_count` is 1):

```json
{
  "schema_version": "1",
  "id": "3f6c1a9e-5b2d-4e7a-9c11-0a4d8e2b7f10",
  "start": "2026-10-27T05:15:00Z",
  "end": "2026-10-27T05:17:30Z",
  "duration_seconds": 150,
  "source": "extension",
  "platform": "youtube",
  "domain": "youtube.com",
  "url": "https://www.youtube.com/shorts/*",
  "title": "Funny cats compilation",
  "activity_type": "short_video",
  "content_count": 1,
  "session_id": null,
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**2. The Instagram home feed** (time only: nothing is counted, because individual feed posts cannot be told apart):

```json
{
  "schema_version": "1",
  "id": "9a2e4c71-0d3b-4f58-8e62-7b1c5d9a3e44",
  "start": "2026-10-27T05:17:30Z",
  "end": "2026-10-27T05:25:00Z",
  "duration_seconds": 450,
  "source": "extension",
  "platform": "instagram",
  "domain": "instagram.com",
  "url": "https://www.instagram.com",
  "title": "Instagram",
  "activity_type": "social_feed",
  "content_count": 0,
  "session_id": "b0c1d2e3f4a5968778695a4b3c2d1e0f",
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**3. Any other website** (generic time, no content count):

```json
{
  "schema_version": "1",
  "id": "c47d0b2f-81a6-4a3e-b5f9-2e6d9c0a1b73",
  "start": "2026-10-27T05:25:00Z",
  "end": "2026-10-27T05:31:00Z",
  "duration_seconds": 360,
  "source": "extension",
  "platform": "unknown",
  "domain": "wikipedia.org",
  "url": "https://www.wikipedia.org",
  "title": "Wikipedia",
  "activity_type": "webpage",
  "content_count": 0,
  "session_id": "b0c1d2e3f4a5968778695a4b3c2d1e0f",
  "category": null,
  "confidence": null,
  "uncertain": false,
  "label_source": null
}
```

**4. A labelled event with low confidence** (the model was unsure, so the label fell back to `Other` and is marked uncertain):

```json
{
  "schema_version": "1",
  "id": "e81b3d56-2c9f-4d07-a4b8-6f0e7a5c2d19",
  "start": "2026-10-27T05:31:00Z",
  "end": "2026-10-27T05:33:00Z",
  "duration_seconds": 120,
  "source": "extension",
  "platform": "unknown",
  "domain": "example.com",
  "url": "https://example.com/docs",
  "title": "Untitled",
  "activity_type": "webpage",
  "content_count": 0,
  "session_id": "b0c1d2e3f4a5968778695a4b3c2d1e0f",
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
- `url` is a **sanitized URL**: scheme, domain and path.
- The **query string and the fragment are always removed.**
- **Long random-looking path segments** and **known sensitive patterns** (reset or login tokens, session ids, email or account ids) are **replaced with `*`**. For example `https://www.youtube.com/shorts/Ab3dE9xQ2zK` becomes `https://www.youtube.com/shorts/*`.
- The cleaning happens **inside the extension**, before anything is sent on.
- The URL is stored **only on the PC and is never synced**. `domain` is kept as well, and **reports use `domain`**.
- The host in `url` belongs to `domain` (`www.youtube.com` belongs to `youtube.com`). The extension guarantees this.
- The JSON schema adds a last-line safety net: it rejects a query string, a fragment, a port, user details, a path segment of 32 or more id-like characters, and an email address in the path. It cannot do the masking itself.

### Periods and counts
- A period starts when a page comes to the front and ends when a different page does, or when the browser loses focus. A new period in another tab, or on another address, is a new event.
- Time is measured for every period. A count (`content_count`) is given only where the item can be told apart reliably: Instagram Reels and posts opened on their own address, YouTube videos and Shorts, TikTok videos opened on their own address, and what Facebook reliably shows. Feeds, home pages and search pages are time only.
- Periods can overlap only if two windows report at once. Time totals use the union of the periods, so an overlap is counted once.

### Rules from the PRD (fixed)
1. All events come from the browser extension and share this one schema (`contracts/event-schema.md`) (F1). *(Changed: the desktop agent was removed, so there is nothing to merge them with.)*
2. Events are stored only on the PC in SQLite, auto-deleted after 30 days, and survive restarts (F4).
3. *(Removed: idle time is no longer measured. A period counts as the time the page was in front.)*
4. Every label has a category (Study, Work, Entertainment, Social Media, Other) and a confidence. Low confidence falls back to uncertain `Other`. No screenshots or OCR (F6).
5. No screenshots, keystrokes or typed text. Page text is read only on the device, never uploaded and not stored (only the label is kept).
6. Hidden sites are skipped before anything is read (F7).
7. Only encrypted summaries leave the PC, never raw events or page text (F8).

### Team decisions (agreed 26 Oct 2026, not in the PRD)
1. **URL:** the extension stores a sanitized URL: scheme, domain and path only. Query string and fragment are always removed. Long random-looking path segments and known sensitive patterns (reset or login tokens, session ids, email or account ids) are masked with `*`. Cleaning happens inside the extension before anything is sent. The URL stays on the PC and is never synced. `domain` is kept too, and reports use `domain`.
2. *(Removed: idle and presence were decided by the Windows agent, which no longer exists.)*
3. *(Removed: overlap with desktop events. There are no desktop events.)*
4. **Other:** `Other` with `uncertain: true` is a last resort, used only when the activity cannot reasonably be identified from the available context.
5. **Categorization:** use the available context (domain, URL, permitted title, time of day) instead of basic guesses. If the model is off, rules fill the category with `label_source: "rules"`.
6. **Hidden sites:** no event is recorded at all.
7. **Private/incognito windows:** not recorded.
8. **Privacy:** never read message bodies, inboxes, DMs, input fields or typed text. Mask sensitive titles.

### How these rules show up in an event
- **Labels:** `category`, `confidence` and `label_source` are added later by the on-device AI, so they are `null` until then. If `confidence` is below the threshold, `category` is `Other` and `uncertain` is `true`. If the model is off, rules fill `category` and `label_source` is `"rules"`.
- **Hidden sites, private windows:** they produce no event at all, so there is nothing in the data to filter out later.
- **Retention:** events older than 30 days are deleted automatically.

## Decisions

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
