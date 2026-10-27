# DLA browser extension (Chrome and Edge, Manifest V3)

## Current state: active-tab logging (F1), only after consent

The extension logs which website is in front, **but only while the Windows agent says the user has agreed and tracking is on**. Until then it records nothing.

What it does:

1. Asks the agent one question, `GET http://127.0.0.1:49721/status` (with the header `X-DLA-Extension-Id: <its own ID>`, because the browser sends no Origin for this request), and records **only if both `consented` and `trackingEnabled` are `true`**. If the agent is not running, does not answer, or answers anything unexpected, that counts as **no consent**. The answer is cached for 10 seconds (never more than 15) and re-checked when tabs change. No timers or alarms.
2. While allowed, it follows the active tab of the focused browser window (tab switches, page changes including single-page-app navigation, and the browser losing focus, which ends the event).
3. Each period becomes **one Contract 1 event** (`contracts/event-schema.md`): all 16 fields, `source: "extension"`, `app: null`, `domain` from `getDomain`, `url` from `sanitizeUrl`, `idle: false`, `presence: null`, no label yet, UTC times in whole seconds, `duration_seconds = end - start`, a random unique `id`, `schema_version: "1"`.
4. Events are kept **in memory only**. The popup shows "N events this session" while recording is allowed, and otherwise says "Nothing is being recorded."

What it never does: read page content (there are **no content scripts**), record private windows (`"incognito": "not_allowed"`), record internal pages (`chrome://`, `edge://`, `about:`, `file:`, extension pages, the new-tab page), record hidden sites (the `isHiddenSite` hook; no setting exists yet, so it always says "not hidden"), use storage, alarms or timers, or make any network request other than `/status`.

**Not done yet** (do not assume any of these work): sending events to the agent (Day 3), SQLite, app tracking, idle or presence (the agent decides those), hidden-site settings, store publishing.

### Titles

The title is kept (cut to 120 characters, control characters removed) **except**, where it becomes `null`:

- when the sanitizer masked or cut anything in the URL path (a masked path suggests a private page), and
- for webmail and messaging sites listed in `sensitive-domains.js`.

That list is a **first version for the team to review**, not a finished list.

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
