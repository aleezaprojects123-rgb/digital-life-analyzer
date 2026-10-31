# Local dashboard (React + Vite)

Three screens, **Today**, **Weekly** and **Monthly**, with a light/dark theme toggle and the note "Your data stays on this PC". Every number on them is worked out by the **Digital Life Analyzer app on this PC** (the agent) and read from its own report routes. **There is no sample data in the app.** The look follows [design/dashboard-concept.html.html](design/dashboard-concept.html.html), whose numbers were only a picture.

Not in these screens, on purpose: categories, score, forecast, recovery plan, goals, login, app names, per-site lists.

Node.js 20 or newer. Commands are for PowerShell, run in this folder (`cloud/frontend/`).

## Install (once)

```
npm install
```

This creates `node_modules/` (ignored by git). See the OneDrive note in [../README.md](../README.md).

## Run it with the real agent

1. **Start the app on this PC** (`Dla.Agent`, the tray program) and make sure it is recording:
   - you have accepted the consent window (it is saved, so this is a one-time step),
   - the tray icon shows tracking **on**. If you chose "pause" in the tray menu, resume it. (A paused agent refuses to give reports.)
2. In this folder:

   ```
   npm run dev
   ```

3. Open **http://localhost:5173** (use `localhost`; the page is not offered on other addresses).

The agent's port is fixed at **49721** (`AgentLinkConfig.DefaultPort` in `device/agent/Dla.Agent/Link/AgentLinkConfig.cs`), so there is nothing to look up. Nothing listens until consent is given.

### What you should see

| Situation | What the page shows |
|---|---|
| Just opened | "Loading your data…" for a moment |
| Agent running, something tracked today | **Today**: the date as the heading (for example "Saturday, 31 October"), "Total tracked time today", five cards (Instagram, YouTube with shorts and videos counted separately, TikTok, Facebook, Other sites), a highlight line such as "N items in Xh YYm" (it is not shown when no content was counted), and a donut with each platform's share of the time |
| Agent running, **nothing tracked yet** (a new day, or a new install) | A card **"Nothing tracked yet"** with "Start using Digital Life Analyzer to see your time summaries here." On Weekly and Monthly the date range and the This/Previous switch stay, so you can still look at the other period |
| Weekly / Monthly | This/Previous switch, a badge (**In progress**, **Completed** or **Upcoming**), the date range, the total, a bar chart (days; week blocks for a month; bar heights are relative to the largest bar), the platform cards and a comparison such as "+2h 00m vs last week" (hidden when the other period has no data) |
| Agent not running, or its port is closed | **"Can't reach the Digital Life Analyzer app on this PC"**, "Please check the tray icon, then try again.", and a **Try again** button |
| Agent running but it answers **403** | The same heading, with: "The app answered, but it is not sharing reports right now. Check that tracking is switched on, then try again." The agent gives one answer (403) for all of: consent not given, tracking paused, and a missing or out-of-date access token, so the page can not say which |
| The agent has a problem, or sends something that is not a report | The same heading with a matching sentence |

Times are written like `2h 17m` (minutes rounded, always two digits). Switching theme is remembered on this browser only.

## How the page reaches the agent (development bridge)

> **DEVELOPMENT ONLY.** How the final UI talks to the agent is **Aleeza's decision**. This bridge is only a way to see real data while the screens are built.

The agent only answers a caller that sends its secret in the `X-DLA-UI-Token` header **and** sends **no `Origin` header**, so a web page can never read reports by itself. The secret is a random value the agent writes, while it runs, to `%LOCALAPPDATA%\DigitalLifeAnalyzer\ui-access.token`, and replaces each time it starts. A web page can not read that file and must not hold the secret.

So in development the Vite dev server does it ([dev/agentBridge.js](dev/agentBridge.js), used by [vite.config.js](vite.config.js)):

```
browser page ──GET /agent/dashboard/today──▶ Vite dev server ──GET /dashboard/today + X-DLA-UI-Token, no Origin──▶ agent (127.0.0.1:49721)
```

- The token file is read for **every request**, so restarting the agent needs no restart of the page. If the file is missing, no token is sent and the agent answers 403.
- **The token never reaches the browser code, the page's answers, the terminal or any log.** (Tests check this.)
- Only a `GET` of the five report routes is forwarded, with no query string, and only from this page itself. Everything else (`/status`, `/events`, other websites' requests) gets a 404 and never reaches the agent.
- Only the headers `Host`, `Accept` and `User-Agent` are forwarded; `Origin`, `Referer`, cookies and the rest are dropped.
- If the agent is not running the bridge answers 502, and the page shows "Can't reach...".
- It exists **only inside `npm run dev`**. In a production build (`npm run build`) nothing answers `/agent/...`, so the built page would show "Can't reach...". It is not a way to publish the dashboard.

The page itself only knows the prefix `/agent` and the five route names, and all reading is in one file, [src/dataSource.js](src/dataSource.js). If the final UI uses another way to talk to the agent, only that file changes.

## What the agent sends (field names)

Read from `device/agent/Dla.Agent/Reports/` and `Link/LocalStatusLink.cs`. JSON is camelCase, enums are words, dates are `yyyy-MM-dd`.

`GET /dashboard/today`: `date`, `totalSeconds`, `platforms` (a list of `platform`, `totalSeconds`, `content`), `otherPlatformsSeconds`. `content` is an object of counts: `instagram` → `reels`; `youtube` → `shorts`, `videos`; `tiktok` → `videos`; `facebook` → `reels`.

`GET /reports/weekly/current`, `weekly/previous`, `monthly/current`, `monthly/previous`: `kind` (`Weekly` or `Monthly`), `firstDay`, `lastDay`, `timeZoneId`, `totalSeconds`, `platforms` (a list of `platform`, `seconds`), `otherPlatformsSeconds`, `content` (a list of `platform`, `activityType`, `count`, `watchSeconds`), `days` (a list of `date`, `dayOfWeek`, `totalSeconds`), `status` (`InProgress`, `Completed` or `Upcoming`), `weeks` (a list of `number`, `firstDay`, `lastDay`, `totalSeconds`; empty in a weekly report).

## Tests and build

```
npm test
npm run build
```

- `npm test` runs the automatic tests. No browser and no agent are needed: the agent is faked. **Test data is only inside test files and is marked as made up.** A test also checks that none of the concept file's sample values and no written-out time or count appear in the shipped code, that only `src/dataSource.js` reads data, and that the token is never named in the browser code. One group of tests starts a real Vite dev server and a stand-in agent that refuses requests the way the real one does, to check the bridge.
- `npm run build` makes the production files in `dist/` (ignored by git).

## Files

| File | Purpose |
|---|---|
| `index.html`, `src/main.jsx` | The page and the entry point. |
| `src/App.jsx` | The frame: top bar (tabs, privacy note, theme toggle) and the three screens. |
| `src/TodayPage.jsx`, `src/ReportPage.jsx` | The Today screen, and the Weekly/Monthly screen (one component for both). |
| `src/components.jsx` | Cards, bar chart, donut, status badge, comparison, and the loading / error / empty states. |
| `src/dataSource.js` | **The one place that reads data.** Fetches the five routes and checks the shape of the answers. |
| `src/model.js`, `src/format.js` | Pure functions: cards, bar heights, donut shares, comparison, status badge; times and dates. |
| `src/useAgentData.js`, `src/theme.js` | Loading state with "Try again"; the theme toggle. |
| `src/styles.css` | Styles, light and dark. |
| `dev/agentBridge.js` | The development-only bridge described above. Never imported by the page. |
| `design/dashboard-concept.html.html` | The visual concept (a picture; its numbers are not used). |
| `src/*.test.js(x)`, `dev/agentBridge.test.js`, `src/test/setup.js` | Automatic tests (Vitest and Testing Library). |
| `vite.config.js` | Dev server on port 5173, the `/agent` bridge, the `/api` forward to the backend, and the test setup. |
