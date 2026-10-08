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

## Setup and run

Agent and extension instructions are added here as each step lands (Step 3: agent, Step 4: extension, Step 5: database and tests).

## Hard rules (from the spec)

- Raw events (app, site, title, timestamps) stay on the PC. Only encrypted summaries are uploaded.
- Typing is never recorded. No screenshots stored. Private/incognito windows never recorded.
- Tracking, AI categorization and nudges work offline.
- Budget: under 3% CPU, 150 MB RAM (250 MB with the model loaded). Visible tray icon at all times.
