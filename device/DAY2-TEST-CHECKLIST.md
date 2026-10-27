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

Open an InPrivate (Edge) or Incognito (Chrome) window and browse. The extension is not allowed there: the count does not grow. (In `edge://extensions` / `chrome://extensions`, "Allow in InPrivate/Incognito" must stay off.)

| Result: Edge | Result: Chrome |
|---|---|
| NOT YET RUN | NOT YET RUN |

### 6. Internal pages

Visit `edge://settings` (Chrome: `chrome://settings`), the new-tab page and `about:blank`. The count does not grow for them.

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

## Things to expect (not bugs)

- **The count can fall back to 0.** The browser stops an idle extension service worker after about 30 seconds without events, and the events live only in its memory. This is a known Day 2 limit, fixed on Day 3.
- **A pause drops the unfinished period.** Recording resumes at the next tab event after it is turned back on, within about 10 seconds (the extension caches the agent's answer for 10 seconds).
- **The agent cannot be reached = no recording.** Closing the agent or blocking port 49721 means "Nothing is being recorded."
- **Events are not sent anywhere.** The agent does not receive events yet.

## Ghost icons

A force-ended agent (Task Manager, stopped debugger) leaves a **dead tray icon** until you hover the mouse over it. Use **Quit** from the tray menu. If ghost icons stay, restart Windows Explorer (Task Manager, Windows Explorer, Restart) to clear them.

## When you are done

Fill in the results above and tell me which steps failed. Do not commit your own `consent.json`, `agent-link.json` or any key file; they live outside the repository.
