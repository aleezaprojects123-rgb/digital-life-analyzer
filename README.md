# Digital Life Analyzer (DLA) — Desktop Agent & Browser Extension

Windows 10/11 AI time coach. This repo holds **Aleeza's half**: the desktop agent, the Chrome/Edge extension, local SQLite, on-device intelligence, local privacy controls, nudges and the installer. Backend, database, analytics and dashboard are Fatima's.

The spec is in [docs/](docs/) and is the only source of truth.

## Layout

| Folder | Contents |
|---|---|
| `agent/` | Windows tray agent (C#, current .NET LTS), local SQLite, tests |
| `extension/` | Chrome/Edge Manifest V3 extension |
| `shared/` | Contracts shared with the backend: JSON Schemas and samples for Interfaces A to D ([shared/contracts/README.md](shared/contracts/README.md)) |
| `docs/` | Project spec (13 `.md` files) |

## Prerequisites

- Windows 10/11
- **.NET 10 SDK** (current LTS). Check with `dotnet --list-sdks`. Install: `winget install Microsoft.DotNet.SDK.10`
- Git
- Chrome and/or Edge (for the extension)
- Python 3 is only needed later for model export scripts in `agent/tools/` (Phase 3)

## Agent (Step 3)

Build and test:

```bash
cd agent
dotnet test
dotnet build src/Dla.Agent
```

Run (first run shows the consent screen):

```bash
agent/src/Dla.Agent/bin/Debug/net10.0-windows/Dla.Agent.exe
```

How it works:

- `Dla.Agent.exe` with no arguments is the **supervisor (watchdog)**. It is what auto-start launches. It starts the agent as a child process (`--agent`) and restarts it after a crash: 2s, 5s, 30s, 30s, and after 5 crashes within 2 minutes it retries every 5 minutes, forever, with its own tray icon visible meanwhile.
- **Auto-start** uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Per-user, no admin rights, listed in Windows Settings > Apps > Startup.
- The agent has **no Quit**. It stops only on a Windows end-session (shutdown, restart, logoff), after writing a `clean_shutdown` marker. Anything else (crash, Task Manager kill) has no marker and is restarted.
- A second copy exits immediately (named mutexes for supervisor and agent).
- Local state lives in `%LOCALAPPDATA%\DLA` (override with `DLA_DATA_DIR`): `consent.json`, `settings.json` (paused flag), `lifecycle.jsonl` (markers), `agent.log`. Step 5 moves consent, settings and markers into SQLite.
- Windows 11 may hide a new tray icon in the overflow (^) area. Drag it out once to keep it always visible; an app cannot force this.

### Step 3 test results

Run by Aleeza on Windows 10 Pro, 2026-10-09. All passed.

| Check | Result |
|---|---|
| Unit tests | 23/23 at the time (27/27 after the lifecycle-log newline hardening below) |
| Consent screen appears first, nothing recorded before Accept | Pass |
| Decline exits cleanly, no auto-start | Pass |
| Accept registers auto-start and shows the tray icon | Pass |
| Tray menu: Pause, Open dashboard, Consent and privacy (no Quit) | Pass |
| Pause / Resume | Pass |
| Withdraw removes auto-start, process stays alive; re-Accept restores it | Pass |
| Single instance (second copy creates no second agent) | Pass |
| Kill the agent: restarts by itself; `crash_detected` marker written | Pass |
| Sleep and wake: `suspend` and `resume` markers | Pass |
| Logoff and restart: `clean_shutdown`, agent starts again by itself, no `crash_detected` | Pass |

Lifecycle log format: `lifecycle.jsonl` holds exactly one JSON object per line, each ended by a single `\n`. If a crash leaves a last line without a newline, the next record first adds one, so it never merges into the torn line (covered by unit tests).

## Extension (Step 4)

A Manifest V3 extension for Chrome and Edge. In Phase 0 it is a shell: it logs a heartbeat and nothing else.

What it does and does not do:

- **Runs with the browser.** The service worker starts at browser start-up (`onStartup`) and is woken every 30 seconds by an alarm. No page, popup or click is needed.
- **Logs a heartbeat** to its service-worker console: `[DLA] heartbeat <time> (...)`.
- **Inactive without consent.** It sends and logs no heartbeat until the consent flag is true. The flag is a stub for now (`chrome.storage.local`, key `dla.consentGranted`, default `false`); later the agent will set it.
- **Cannot read pages or typing.** The manifest has only the `alarms` and `storage` permissions, no host permissions, no content scripts, no `tabs`. A unit test fails if that ever changes.
- **Never runs in private windows** (`"incognito": "not_allowed"`).
- **Agent link is a placeholder** (`extension/src/agentLink.js`): nothing is sent anywhere yet.
- It is never force-installed or re-enabled. If the user disables it, the agent sees no heartbeat and records a gap (see above).

Test the logic:

```bash
cd extension
npm test
```

Load it unpacked in **Chrome**:

1. Open `chrome://extensions`, turn on **Developer mode** (top right).
2. Click **Load unpacked** and choose the `extension/` folder.
3. On the extension's card click **service worker** to open its console.

Load it unpacked in **Edge**:

1. Open `edge://extensions`, turn on **Developer mode** (left side).
2. Click **Load unpacked** and choose the `extension/` folder.
3. On the extension's card click **service worker** to open its console.

Check it:

1. With no consent you see `[DLA] inactive: no consent from the agent yet (...)` (shown under *Verbose* / *Debug* log levels). No heartbeat appears.
2. In that console run: `chrome.storage.local.set({'dla.consentGranted': true})`. Within 30 seconds you see `[DLA] heartbeat ...`.
3. Set it back to `false` and the heartbeat stops.
4. Close the browser completely and reopen it. The extension is still listed and enabled, and logs again after start-up (the service worker may show as inactive until its next alarm; click it to see the console).

Note: Chrome and Edge may show a "disable developer mode extensions" prompt at start-up for unpacked extensions. This only affects development; store-installed extensions do not show it.

How it reaches users at release:

- It is published on the **Chrome Web Store** and **Microsoft Edge Add-ons**. The user installs it by clicking **Add**. Neither browser allows a website or program to install an extension silently, and we do not use enterprise policy for consumers. Edge can also install from the Chrome Web Store.
- The DLA website (see [docs/decisions/website-activation.md](docs/decisions/website-activation.md)) links to the store pages. Once installed it starts with the browser and updates automatically through the store.
- The user can disable or remove it at any time. We never re-enable it; the agent marks that time as unknown.

## Local database (Step 5)

SQLite file `%LOCALAPPDATA%\DLA\dla.db` (WAL mode, `secure_delete` on so purged rows are overwritten). Code is in `agent/src/Dla.Agent/Data/`; the schema is the append-only list in `Migrations.cs`. Never edit a shipped migration; add a new one. Migrations run from an empty database, each in its own transaction, and an agent refuses a database from a newer version.

| Table | Holds |
|---|---|
| `events` | app, site, title, precise start/end (UTC ms), source (`agent` or `extension`), category, confidence, `is_unknown` marker + reason |
| `personal_rules` | user corrections (app / site / title contains -> category), unique per pattern, case-insensitive |
| `settings` | `retention_days` (30), `idle_threshold_seconds` (180), `paused` (0) |
| `exclusions` | apps and sites the user excluded |
| `consent_records` | append-only consent history (state, text version, time); newest row is current |
| `ocr_audit_log` | one row per OCR capture: counts and a label only |
| `upload_queue` | already-encrypted summaries; a payload hash prevents duplicates |
| `lifecycle_markers` | start, clean_shutdown, suspend, resume, crash_detected, extension_silent_start/end |

No table can store keystrokes or screenshots: a unit test fails if any table or column is named like one, and the only binary column allowed is `upload_queue.payload` (ciphertext).

**Retention purge** (`RetentionPurger`): deletes events that ended (or, if still open, started) before `now - retention_days`, plus old OCR audit rows, lifecycle markers and already-sent queue rows. Unsent summaries are never purged.

**Not wired yet:** the running agent still keeps consent, the paused flag and lifecycle markers in JSON files (Step 3). They move to these tables in Phase 1, when the recorder first needs the database, so there is only ever one live source of truth.

Run the tests:

```bash
cd agent
dotnet test
```

Developer overrides (never set in production): `DLA_DATA_DIR` (data folder) and `DLA_AUTOSTART_SUBKEY` (registry key for the Run entry).

## Hard rules (from the spec)

- Raw events (app, site, title, timestamps) stay on the PC. Only encrypted summaries are uploaded.
- Typing is never recorded. No screenshots stored. Private/incognito windows never recorded.
- Tracking, AI categorization and nudges work offline.
- Budget: under 3% CPU, 150 MB RAM (250 MB with the model loaded). Visible tray icon at all times.
