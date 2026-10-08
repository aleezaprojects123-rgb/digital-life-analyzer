# Digital Life Analyzer (DLA) — Desktop Agent & Browser Extension

Windows 10/11 AI time coach. This repo holds **Aleeza's half**: the desktop agent, the Chrome/Edge extension, local SQLite, on-device intelligence, local privacy controls, nudges and the installer. Backend, database, analytics and dashboard are Fatima's.

The spec is in [docs/](docs/) and is the only source of truth.

## Layout

| Folder | Contents |
|---|---|
| `agent/` | Windows tray agent (C#, current .NET LTS), local SQLite, tests |
| `extension/` | Chrome/Edge Manifest V3 extension |
| `shared/` | Contracts shared with the backend (empty until Phase 1+) |
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

Developer overrides (never set in production): `DLA_DATA_DIR` (data folder) and `DLA_AUTOSTART_SUBKEY` (registry key for the Run entry).

## Hard rules (from the spec)

- Raw events (app, site, title, timestamps) stay on the PC. Only encrypted summaries are uploaded.
- Typing is never recorded. No screenshots stored. Private/incognito windows never recorded.
- Tracking, AI categorization and nudges work offline.
- Budget: under 3% CPU, 150 MB RAM (250 MB with the model loaded). Visible tray icon at all times.
