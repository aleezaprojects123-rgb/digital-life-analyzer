# DLA browser extension (Chrome and Edge, Manifest V3)

## Current state: active-tab logging (F1), only after consent

The extension logs which website is in front, **but only while the Windows agent says the user has agreed and tracking is on**. Until then it records nothing.

What it does:

1. Asks the agent one question, `GET http://127.0.0.1:49721/status` (with the header `X-DLA-Extension-Id: <its own ID>`, because the browser sends no Origin for this request), and records **only if both `consented` and `trackingEnabled` are `true`**. If the agent is not running, does not answer, or answers anything unexpected, that counts as **no consent**. The answer is cached for 10 seconds (never more than 15) and re-checked when tabs change. No alarms. The only timers are a 3 second one-shot after the browser loses focus, and a 20 second keep-alive that runs only while a period is being recorded (see "Why the keep-alive" below).
2. While allowed, it follows the active tab of the focused browser window (tab switches, page changes including single-page-app navigation, and the browser losing focus, which ends the event).
3. Each period becomes **one Contract 1 event** (`contracts/event-schema.md`): all the fields of `contracts/event-schema.md`, `source: "extension"`, `domain` from `getDomain`, `url` from `sanitizeUrl`, `platform`, `activity_type`, `content_count`, no label yet, UTC times in whole seconds, `duration_seconds = end - start`, a random unique `id`, `schema_version: "1"`.
4. Events are kept **in memory only**. The popup shows "N events this session" while recording is allowed, and otherwise says "Nothing is being recorded."

What it never does: read page content (there are **no content scripts**), record private windows (`"incognito": "not_allowed"`), record internal pages (`chrome://`, `edge://`, `about:`, `file:`, extension pages, the new-tab page), record hidden sites (the `isHiddenSite` hook; no setting exists yet, so it always says "not hidden"), use storage or alarms, or make any network request other than `/status`. (The one recurring timer is the keep-alive, which makes a harmless `chrome.runtime.getPlatformInfo()` call and reads nothing.)

**Not done yet** (do not assume any of these work): hidden-site settings, store publishing.

### Titles

The title is kept (cut to 120 characters, control characters removed) **except**, where it becomes `null`:

- when the sanitizer masked or cut anything in the URL path (a masked path suggests a private page), and
- for webmail and messaging sites listed in `sensitive-domains.js`.

That list is a **first version for the team to review**, not a finished list.

### What the popup count means

"N events this session" is the number of **finished normal-page tracking periods** held in memory (`events.length`). It is not the number of tab switches or pages visited, and it does not include the page you are on now. A period ends when you leave the page (another tab, a new address in the tab, browser focus lost, a private window, an internal page). So leaving a normal page for a private window or `edge://settings` raises the count by one, although the private or internal page itself records nothing. The count shows no addresses, so it is **not** proof that nothing private was recorded; the automatic tests check that. It covers all normal windows of the browser profile and resets when the browser stops the idle service worker.

**Opening or closing the popup does not change the count.** Opening the popup takes the keyboard focus from the browser window, which the browser reports as "no window in front"; before the fix that ended the current page's period (and closing it started a new one), so every click added one. Now the popup connects to the background worker when it opens (it sends nothing over that connection). While it is open, a focus loss is ignored, and a period that was ended by the focus loss just before the popup connected is taken back with its original start time. Real changes (another page or tab, leaving the browser, a private window, an internal page) still end the period. One known gap: if you open the popup and then switch to another program without closing it first, the browser may not report a second focus loss, so that period ends at your next browser action instead of at the switch. This fix is tested with a fake browser only; it needs the manual check in `DAY2-TEST-CHECKLIST.md`.

### What happens to a period when recording stops

If the agent says "paused" or "no consent" at the next tab event, the **unfinished period is dropped** (we cannot know when the pause happened). Events already finished are kept, unless the agent says no consent is on record at all (`consentVersion` 0), which clears them. A tab switch of under a second still becomes a 0-second event.

### Known limitation: the service worker can be stopped

Chrome and Edge stop an idle MV3 service worker after about 30 seconds without events and restart it on the next tab event. **The in-memory events and the unfinished period are lost when that happens**, so "N events this session" can drop back to 0. This is expected for now and is fixed on Day 3, when finished events are handed to the agent straight away instead of being held here.

## Permissions (and why)

See [PERMISSIONS.md](PERMISSIONS.md). In short: `tabs` (to read the active tab's URL and title) and one host permission, `http://127.0.0.1:49721/*` (to ask the agent for `/status`). Nothing else.

## For developers only

End users never do any of this. They will install the extension from the official store (team decision 9). Loading it unpacked is developer-only.

### 1. The fixed development ID

`manifest.json` contains a `key` (a **public** key), so an unpacked copy always gets the same extension ID, in both Chrome and Edge, on every PC:

```
facfnlbfamjpolgenifllpjedenekdlb
```

An extension ID is computed from the key, not from the browser, so **Chrome and Edge show the same ID** for this key. (Without a `key`, an unpacked extension's ID depends on the folder path and would differ.)

- **This key is for development only.** The matching private key was generated on Aleeza's PC and is stored **outside the repository** (`C:\Users\Admin\.dla-keys\dla-extension-dev.pem`). `*.pem` and `*.key` are in `.gitignore`. Never commit it.
- **Review before packaging for a store.** The Chrome Web Store and Edge Add-ons assign their own IDs and key. Before a store build, remove this `key` (or replace it as the store instructs) and put the real store ID in the agent's allowlist instead. Do not publish a store build that carries this development key.

### 2. Load it unpacked

**Edge:** open `edge://extensions`, turn on **Developer mode** (left side), click **Load unpacked**, choose `device/extension/`.
**Chrome:** open `chrome://extensions`, turn on **Developer mode** (top right), click **Load unpacked**, choose `device/extension/`.

After editing a file, click the reload button on the extension's card.

### 3. Find the extension ID

On the extension's card in `edge://extensions` or `chrome://extensions`, the line **ID** shows 32 letters. It must read `facfnlbfamjpolgenifllpjedenekdlb`. If a browser shows a different ID, the `key` is missing or wrong; do not add that ID to the allowlist, fix the manifest instead.

### 4. Allow the extension in the agent

The agent only answers extensions on its allowlist, and its default list is **empty**. Create `%LocalAppData%\DigitalLifeAnalyzer\agent-link.json` (PowerShell):

```
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\DigitalLifeAnalyzer" | Out-Null
[IO.File]::WriteAllText("$env:LOCALAPPDATA\DigitalLifeAnalyzer\agent-link.json", '{ "allowedExtensionIds": [ "facfnlbfamjpolgenifllpjedenekdlb" ] }')
```

Both browsers use the same ID, so one entry covers Chrome and Edge. If you ever load a copy that shows another ID, add it as a second entry in the list. **Restart the agent** after changing the file. Do not commit this file (it lives outside the repo).

### 5. Run the automatic tests

Needs only Node.js (no npm, no packages). In `device/extension/`:

```
node --test
```

They run the real event code against the real sanitizer and check every event against `contracts/event.schema.json`. They do not need a browser or the agent.

### 6. Try it for real

See the manual test steps in the Day 2 report, or `device/DAY2-TEST-CHECKLIST.md` once it exists (Task 6).

## Files

| File | Purpose |
|---|---|
| `manifest.json` | Manifest V3: `tabs` permission, the one local host permission, the development `key`, module service worker, not allowed in private windows. |
| `background.js` | The service worker. Connects tab and window events to the logger. |
| `tab-logger.js` | The logic: one open period, closes into one Contract 1 event. No browser calls, so it is testable in Node. |
| `agent-status.js` | The only network call: `GET /status`, strict checking, 10 second cache. |
| `url-sanitizer.js` | `sanitizeUrl`, `getDomain`, `isHiddenSite` (hook, always false for now). |
| `sensitive-domains.js` | Sites whose page titles are never kept (first version, to review). |
| `popup.html`, `popup.js` | The popup: "N events this session" only while recording is allowed. |
| `PERMISSIONS.md` | Permission justifications for store review. |
| `test/*.test.js`, `test/schema-check.js` | Automatic tests and a small schema checker. |

## Browser events reach events.jsonl (manual Chrome check, version 0.0.3)

What was wrong: the extension's sender got stuck on its first (empty) run at browser start-up, so no event was ever POSTed. Fixed in 0.0.3.
Also: leaving for another app now sends the finished page after 3 seconds, and an event the agent permanently rejects (400) no longer blocks the ones behind it.

Steps (no automation is run for you; do these yourself):
1. Close the old agent (tray icon > Quit). Build and start the agent again: `dotnet run --project device/agent/Dla.Agent`.
2. Open `chrome://extensions`, press Reload on Digital Life Analyzer. It must say version 0.0.3 and ID facfnlbfamjpolgenifllpjedenekdlb.
3. Keep the service worker DevTools CLOSED (an open DevTools hides the idle-stop problem).
4. Visit YouTube, then Google, then GitHub, about 20 seconds each (not more than about 25 seconds without changing tab), then switch to VS Code for 10 seconds.
5. Look for extension lines:
   `findstr /C:"\"source\":\"extension\"" devicegent\data\events.jsonl`
   You should see one line per page, next to the `"source": "agent"` lines, in the same file.
6. If nothing appears, the agent log shows `Browser event refused: HTTP 400/500 <reason>` for a rejected event, and the first log lines show the exact events file path.

Known limit: a page that is still OPEN when Chrome stops the idle service worker (about 30 seconds with no tab changes) is lost. Finished pages are never lost.

## Media metadata in events.jsonl (manual Chrome check, version 0.0.4)

The agent reads what Windows says is playing (System Media Transport Controls: title, artist, album, play/pause, app) and
attaches it to the browser period it belongs to. It never writes a separate media event, so time is not counted twice.
If the media changes inside one page (next song, next Short), the period is split into consecutive events.
The new fields (`media_title`, `artist`, `album`, `media_type`, `playback`, `media_app`) are on branch `contract/media-fields`,
which still needs Fatima's approval. Events without media are written exactly as before.

1. Quit the agent from the tray, run the rebuilt agent, and reload the extension (0.0.4).
2. Test A: open "Heartless" by Kanye West on YouTube, play 30 s, switch tab. The line has `media_title`, `artist`, `playback: "playing"`.
3. Test B: a normal YouTube video for 30 s.
4. Test C: open YouTube Shorts and swipe 3 Shorts in the same tab. Expect one event per Short (urls stay `/shorts/*`, ids never stored).
5. Test D: Spotify desktop is not attached to events yet. Check that Windows sees it with a read-only query of the media sessions.
6. Check `device/agent/data/events.jsonl`. YouTube may give the channel as `artist` and the full video title as `media_title`.

### Checking the file is correct UTF-8 (do not trust `findstr` or the console)

`events.jsonl` is UTF-8 without a BOM. The Windows console (code page 437/850) and `findstr` show Japanese text and
`Señorita` as garbage such as `Se├▒orita` even though the file is fine. Check it with a UTF-8 reader instead:

    py -c "import json;[print(json.loads(l).get('media_title')) for l in open('device/agent/data/events.jsonl',encoding='utf-8') if 'media_title' in l]"

or open the file in VS Code (it shows "UTF-8" in the status bar). In PowerShell use `Get-Content -Encoding utf8 ... | Select-String media_title`.

### When a video gets no media fields

`agent.log` now says why, with a reason code only and never a title: `Media for browser event: browser_media_but_title_did_not_match`
means Windows had a playing browser session but its title was not the tab's title (for example a YouTube advert, which replaces the
session's title; or a session that still shows the previous video). `no_browser_media_in_period` means Windows published nothing for the browser then.

### Why the keep-alive

Chrome stops an idle extension service worker after about 30 seconds without extension events. A video that just plays creates no tab events, so
without a keep-alive the worker was stopped mid-video, the open period (memory only) was lost, and no event was written for the watched time. While
recording is allowed and a period is open, `keep-alive.js` makes one `chrome.runtime.getPlatformInfo()` call every 20 seconds (it returns the operating
system name; it reads no page and sends nothing) and stops by itself when recording stops or no period is open. No storage, no ids, no permission.
