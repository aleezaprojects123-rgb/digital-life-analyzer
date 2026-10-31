# Browser activity pipeline (developer guide)

**DLA measures behaviour, not content.** It records which platform was used, what kind of activity it was, when it started and
ended, how long it lasted, and how many content items were consumed. It does not read or store what the content was: no
captions, titles of watched items, creators, hashtags, audio, page text, messages, screenshots or OCR. The extension has no
content scripts and no website access (see `extension/PERMISSIONS.md`).

```
Browser tab (active, focused, not private)
  -> tab logger         extension/tab-logger.js        tab, sanitized URL, domain, start/end, transitions, content_count
  -> platform adapter   extension/platforms.js + adapters/   platform + activity_type (+ an in-memory item key) from the address only
  -> event normalizer   extension/event-normalizer.js  the ONE event shape
  -> local agent        agent/Dla.Agent/Link/          validates, stamps session_id, adds Windows media info
  -> events.jsonl       agent/data/events.jsonl
  -> (later) analytics, goals, UI, summaries, encrypted sync
```

## The event (extension)

| Field | Meaning |
|---|---|
| `id`, `schema_version` | unique id; `"1"` |
| `start`, `end`, `duration_seconds` | the timestamp of the activity is `start`; `duration_seconds = end - start` |
| `source` | `"extension"` |
| `platform` | `youtube instagram tiktok reddit facebook whatsapp spotify netflix github google unknown` (by domain) |
| `activity_type` | `webpage search video short_video reel music post article document social_feed messaging meeting shopping game unknown` |
| `content_count` | `1` if this event is where the user moved to a NEW content item (Reel, video, post), else `0` |
| `domain`, `url` | registrable domain; sanitized URL (no query, no fragment, ids masked with `*`) |
| `title` | tab title where it is allowed (null for Instagram's generic title, withheld on Facebook and webmail/chat); unchanged elsewhere |
| `session_id` | `null` from the extension; stamped by the local agent (one id per recording run) |
| `category`, `confidence`, `uncertain`, `label_source` | fixed (`null`/`false`): the classifier owns these |

Removed in this direction (they only existed for the abandoned metadata work): `content_context`, `creator`, `description`,
`hashtags`, `published_at`, `language`, `metadata_source`, and the extension-side `media_*` fields. The Windows agent may still add
`media_*` fields when the system media controls match a browser event (enrichment stage); when it splits an event at a media change
only the first piece keeps `content_count`.

## How content items are counted

An item is a Reel, video, Short or post that has its own address. The adapter reads an **item key** from the real address
(`reel:<code>`, `video:<id>`, `post:<id>`), in memory only; it is never stored or sent. The tab logger keeps, per tab, the key it
counted last.

- A new period whose key differs from the last counted key in that tab gets `content_count: 1`.
- Anything else gets `0`: coming back to the same item after a tab switch, lost focus or a gap in time; a page re-render; a title or
  query change; a feed, home or search page; an unknown site (no key).
- Swiping back to an earlier item counts again (it was watched again).
- If an address does not change when content changes (some feeds), nothing in the address shows it, so **the count under-reports
  rather than guessing**. Reliability is more important than aggressive counting.
- Pausing drops the unfinished period and gives its count back; withdrawing consent forgets the memory.
- Service worker lifetime: Chrome stops an idle extension service worker after ~30 s, and a video that just plays produces no events, so the open period and this memory would be lost. While a period is open and recording is allowed, `keep-alive.js` makes a cheap `chrome.runtime.getPlatformInfo()` call every 20 s and stops by itself otherwise. Nothing is stored. If Chrome stops the worker anyway (browser restart, crash), the memory is lost and an item returned to later may count once more.

The Instagram home feed (`instagram.com/`) is `activity_type: "social_feed"` with `content_count: 0`: its time is measured, but individual feed
Posts are NOT counted. The feed's address and title never change while scrolling, so nothing the browser reports tells one Post from the next, and
telling them apart would need page content or an id (not collected). A Post opened on its own address (`/p/<code>/`) is `post` and is counted.

Platforms with item addresses today: YouTube (`/watch?v=`, `/shorts/`, `/live/`, `/embed/`, youtu.be), Instagram (`/reel/`, `/reels/<code>`,
`/<user>/reel/`, `/p/`), TikTok (`/@user/video/<id>`), Reddit (`/comments/<id>`), Facebook (`/reel/<id>`, `/watch/?v=`, `/<page>/videos/<id>`,
`/<user>/posts/<id>`, `permalink.php?story_fbid=`). The Facebook and Instagram patterns have not been confirmed in a real browser.

## What a future screen computes from raw events

Raw events stay atomic; totals are never written by the collector.

| Question | Calculation |
|---|---|
| Content count | `SUM(content_count)` for `platform = 'instagram' AND activity_type = 'reel'` |
| Total time | `SUM(duration_seconds)` with the same filter |
| Daily usage | group by the date of `start` (convert to local time) |
| Platform / activity type usage | group by `platform`, `activity_type` |
| Session behaviour | group by `session_id` (ordered by `start`: app switches show as the gaps and other events between) |
| "200 Reels in 2h 17m" | `SUM(content_count)` and `SUM(duration_seconds)` together; count never replaces time |

Which activity types count towards a user's "mindless scrolling" goal is decided later by the user's own configuration, not by the collector.

## Adapters

```js
{ platform, identify(domain, host), getActivityType({host, path}) -> type | null,
  contentKey?(url, host) -> string | null,       // item identity from the REAL address; memory only
  extractTitle?(rawTitle, ctx), withholdTitles? } // title policy
```
A new platform is one adapter file plus an entry in `ADAPTERS`. Anything unrecognised becomes `platform: "unknown"` / `"webpage"` with
`content_count: 0`, and is still recorded.

## Privacy boundaries

No content scripts, no page reading, no screenshots/OCR, no form data, no private messages, no private windows. URLs are sanitized in the
extension and stay on the PC. Messaging and webmail titles are never kept.

## Contract status (needs Fatima's approval)

`contracts/event.schema.json` (`additionalProperties: false`) does not yet allow `platform`, `activity_type` or `content_count`
(and `session_id`, plus the agent's `media_*`). Tests validate against the contract plus exactly these additions
(`extension/test/schema-check.js`, `PROPOSED_FIELDS`). `content_count` is the only new field this direction needs: without it a count
cannot be told apart from an event that merely continues the same item after a tab switch. Proposal: integer, 0 or 1, required to be 0
for events that are not content items.

## Tests

`extension/test/content-count.test.js` (counting rules, Facebook, consent/pause), `universal-events.test.js` and `instagram.test.js`
(activity types, adapters), `tab-logger.test.js` (lifecycle, privacy, schema), `background.test.js` (the real worker). C#: `UniversalEventTests`
(count survives the agent and media splits), `SessionStampTests`.
