# Day 2 manual test checklist (Edge, then Chrome)

This is **yours to run**. Nothing here has been run by hand yet, and no result is filled in. Replace each `NOT YET RUN` with `PASS` or `FAIL` (and a note) as you go.

Automatic tests (`dotnet test` in `device/agent/`, `node --test` in `device/extension/`) do not need a browser. This list covers what only a real browser and a real tray can show.

**Extension ID to expect (both browsers):** `facfnlbfamjpolgenifllpjedenekdlb`

## Setup (do once per browser)

Edge first, then Chrome. The agent can stay running between the two browsers.

| # | Step | Result: Edge | Result: Chrome |
|---|---|---|---|
| S1 | Quit any running agent (tray icon, **Quit**). In Task Manager there is no `Dla.Agent.exe`. | NOT YET RUN | NOT YET RUN |
| S2 | Create the allowlist file (PowerShell): `New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\DigitalLifeAnalyzer" \| Out-Null` then `[IO.File]::WriteAllText("$env:LOCALAPPDATA\DigitalLifeAnalyzer\agent-link.json", '{ "allowedExtensionIds": [ "facfnlbfamjpolgenifllpjedenekdlb" ] }')` | NOT YET RUN | NOT YET RUN |
| S3 | To test "no consent", delete **only** `%LocalAppData%\DigitalLifeAnalyzer\consent.json`. **Never delete the whole folder** (it holds `agent-link.json` and `agent.log`). | NOT YET RUN | NOT YET RUN |
| S4 | Run the agent from the repo root: `dotnet run --project device/agent/Dla.Agent`. Do not accept the consent window yet. | NOT YET RUN | NOT YET RUN |
| S5 | Open `edge://extensions` (Chrome: `chrome://extensions`). Turn on **Developer mode**. Click **Load unpacked** and choose `device/extension/`. | NOT YET RUN | NOT YET RUN |
| S6 | On the extension's card, check the **ID** reads `facfnlbfamjpolgenifllpjedenekdlb` and there are no errors. | NOT YET RUN | NOT YET RUN |

If the ID is different, the `key` in `manifest.json` is not being used. Stop and fix that; do not add another ID to the allowlist.

## The 8 steps

Click the extension's icon (pin it from the puzzle-piece menu) to open the popup.

### 1. No consent

With `consent.json` deleted (S3) and the agent started (S4): you see the consent window and **no tray icon**. Open the popup. It says "Nothing is being recorded." Browse a few sites, then open the popup again. Still "Nothing is being recorded."

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 2. Decline

Click **Decline** in the consent window. The agent exits, no tray icon appears, and `consent.json` is **not** created. Browse, then open the popup: "Nothing is being recorded."

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 3. Accept

Start the agent again. Tick the box and click **Accept**. A tray icon appears (it may be under the `^` arrow). Visit `https://en.wikipedia.org`, then `https://www.youtube.com/shorts/<any short>`, then any normal page, and switch between the tabs a few times. Open the popup: "N events this session", and N goes up as you switch.

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 4. Pause

Right-click the tray icon and choose **Tracking: OFF**. Wait up to 10 seconds, then switch tabs. The count stops growing and the popup says "Nothing is being recorded." Choose **Tracking: ON** again: recording resumes (new events, count grows again).

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 5. Private window

First stay on one normal page (for example `https://en.wikipedia.org`) for a few seconds and note the popup count (call it N). Then open an InPrivate (Edge) or Incognito (Chrome) window, browse several pages in it, close it, and return to the normal page.

**Expected:** the count may go up by **1** (the normal page you left has now ended as an event). It must **not** go up again for each page you visit inside the private window, and moving between private pages must not change it. The extension is not allowed in private windows (in `edge://extensions` / `chrome://extensions`, "Allow in InPrivate/Incognito" must be off or unavailable).

**Limit of this check:** the count cannot prove that nothing private was recorded (see "What the popup count means" below). The automatic tests cover that part.

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 6. Internal pages

Start on a normal page and note the popup count (N). Then visit `edge://settings` (Chrome: `chrome://settings`), the new-tab page and `about:blank`, moving between them, and return to the normal page.

**Expected:** the count may go up by **1** when you first leave the normal page (that page's event ended). It must **not** go up again while you move between the internal pages. Going back to a normal page starts a new period, and leaving that one later adds 1 again.

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 7. Quit

Right-click the tray icon and choose **Quit**. The icon disappears and Task Manager shows no `Dla.Agent.exe`. Open the popup: "Nothing is being recorded." Switching tabs adds nothing.

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 8. Restart

Start the agent again (`dotnet run --project device/agent/Dla.Agent`). There is **no consent window**, the tray icon appears, and recording works again (the count starts again from 0 or a small number; see the note below).

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

## Extra: sanitizer spot check (optional)

The sanitizer itself is covered by `node --test`. To see it in a real browser you would have to read events from the extension's service worker console, which is only for developers: on the extension card click **service worker**. Events are not shown there by default, so treat this as optional and skip it if unsure.

| Check | Result: Edge | Result: Chrome |
|---|---|---|
| A YouTube Shorts page produces an event whose url ends in `/shorts/*` (no id, no query) | NOT YET RUN | NOT YET RUN |

## What the popup count means

The popup number is **the number of finished normal-page tracking periods** held in the extension's memory. It is not the number of tab switches or pages visited.

- A period ends when you leave that page: switch tab, navigate in the tab, lose browser focus, or move to a private window or an internal page. Only then does the count go up by one.
- The page you are on right now is **not** counted yet, so the count lags by one.
- Private windows and internal pages never create a period, but **leaving a normal page for them ends that page's period, so the count goes up by one**. This is correct, not a leak.
- The count shows no addresses, so it **cannot prove** that nothing private was recorded. The automatic tests (`node --test`) check that.
- It covers all normal windows of the browser profile, resets when the browser stops the idle extension, and is hidden while paused or without consent.

### Popup check (after the popup-count fix)

Stay on one normal page (for example `https://en.wikipedia.org`) and do nothing else. Note the count. Open and close the DLA popup 6 times in a row.

**Expected:** the count does not change at all. Then go to a different website: the count goes up by 1. Do this in Edge and in Chrome.

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

## Things to expect (not bugs)

- **The count can fall back to 0.** The browser stops an idle extension service worker after about 30 seconds without events, and the events live only in its memory. This is a known Day 2 limit, fixed on Day 3.
- **A pause drops the unfinished period.** Recording resumes at the next tab event after it is turned back on, within about 10 seconds (the extension caches the agent's answer for 10 seconds).
- **The agent cannot be reached = no recording.** Closing the agent or blocking port 49721 means "Nothing is being recorded."
- **Events are not sent anywhere.** The agent does not receive events yet.

## Ghost icons

A force-ended agent (Task Manager, stopped debugger) leaves a **dead tray icon** until you hover the mouse over it. Use **Quit** from the tray menu. If ghost icons stay, restart Windows Explorer (Task Manager, Windows Explorer, Restart) to clear them.

## When you are done

Fill in the results above and tell me which steps failed. Do not commit your own `consent.json`, `agent-link.json` or any key file; they live outside the repository.

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
