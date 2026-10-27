# DLA browser extension (Chrome and Edge, Manifest V3)

## Current state: development shell, it records nothing

This is only the **foundation** of the extension. Today it:

- loads in Chrome and Edge,
- asks for **no permissions** (no `tabs`, `storage`, `idle`, `alarms` and no website access),
- writes one line to its console: `Digital Life Analyzer extension loaded: no tracking started`,
- shows a small popup: "Digital Life Analyzer. Not set up yet. Nothing is being recorded."

It records, stores and sends **nothing**. It does not read tabs, URLs, titles or page content, makes no network requests, uses no storage or timers, and does **not talk to the Windows agent**. It is set not to run in private (incognito) windows.

**Not done yet** (do not assume any of these work): consent, tracking, any communication with the agent, and publishing in the Chrome or Edge store.

## For developers only: load it unpacked

End users will never do this. They will install the extension normally, from the store, and never run any code or commands. The steps below are only for developers testing the code.

**Chrome**
1. Open `chrome://extensions`.
2. Turn on **Developer mode** (switch at the top right).
3. Click **Load unpacked** and choose this folder (`device/extension/`).

**Edge**
1. Open `edge://extensions`.
2. Turn on **Developer mode** (switch on the left).
3. Click **Load unpacked** and choose this folder (`device/extension/`).

**What you should see**
- The extension "Digital Life Analyzer" in the list, version 0.0.1, with **no errors** and a permissions note saying it needs no special access.
- Click **service worker** on its card to open the console. You should see `Digital Life Analyzer extension loaded: no tracking started`.
- Click the extension's icon in the toolbar (use the puzzle-piece menu and pin it if needed). The popup says "Digital Life Analyzer. Not set up yet. Nothing is being recorded." It does not say "Tracking: ON".

After you change a file, click the reload button on the extension's card.

## What comes later

- **Day 2:** the `tabs` permission, sanitized URL and active-tab logging, following `contracts/event-schema.md`. This will work **only after the Windows agent says that the user has consented**. Until then the extension stays inactive.
- Talking to the agent (so it can learn that consent was given).
- Hidden sites are skipped before anything is read.
- Store publishing, so users install it normally.

## Files

| File | Purpose |
|---|---|
| `manifest.json` | Declares the extension (Manifest V3): name, version, description, the popup and the service worker. No permissions. Not allowed in private windows. |
| `background.js` | The service worker. It only writes one line to the console. |
| `popup.html` | The popup shown when you click the extension's icon. Plain HTML, no script. |
| `url-sanitizer.js` | `sanitizeUrl`, `getDomain`, `isHiddenSite` (hook, always false for now). Not used by the extension yet; the tab logging task will use it. |
| `test/url-sanitizer.test.js` | Automatic tests. |

## Run the tests (developers)

Needs only Node.js (no npm, no packages). In `device/extension/`:

```
node --test
```

The tests also check every cleaned URL against the `url` and `domain` rules read from `contracts/event.schema.json`.
