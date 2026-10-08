# Phase 0 close-out: Aleeza's half (desktop agent and browser extension)

Date: 2026-10-09. Scope: Windows agent, Chrome/Edge MV3 extension, local SQLite, local privacy controls. Backend, analytics and dashboard are Fatima's.

Every statement below was checked against the files in this repository or against test runs on 2026-10-09. Where a result comes from Aleeza's own manual testing and not from an automated test, it says so.

## Completed

| Item | What exists | Where |
|---|---|---|
| Language choice | C# on .NET 10 (current LTS), WinForms on `net10.0-windows`. Reason: lowest memory against the 150/250 MB budget, native Windows hooks and signing, less antivirus risk than a frozen Python bundle. Main risk: the model tokenizer is Python-first, handled by a Python export step plus a C# parity test in Phase 3. | `agent/src/Dla.Agent/Dla.Agent.csproj` |
| Repo skeleton | `agent/`, `extension/`, `shared/`, `docs/` (13 spec files), `.gitignore`, README | repo root |
| Agent shell | First-run consent screen (Accept/Decline, version and timestamp saved); per-user auto-start through `HKCU\...\Run`; tray menu with Pause/Resume, Open dashboard (placeholder) and Consent and privacy, no Quit; supervisor that restarts a crashed agent (2 s, 5 s, 30 s, 30 s, then every 5 minutes with its own tray icon); clean-shutdown, suspend and resume markers; crash detection at next start; single instance; extension-silence gap markers | `agent/src/Dla.Agent/` |
| Extension shell | MV3, permissions only `alarms` and `storage`, no host permissions or content scripts, `incognito: not_allowed`, 30-second heartbeat logged to its console, inactive until a consent flag (stub) is true, agent link is a placeholder | `extension/` |
| SQLite layer | Versioned migrations (8 tables plus `schema_migrations`), data-access classes, retention purge (default 30 days), WAL and secure-delete on, no table able to hold keystrokes or screenshots | `agent/src/Dla.Agent/Data/` |
| Model benchmark plan | Plan with gates, measurement method, labeled-set design, verified licenses and sizes of the three candidates; Python tools under `agent/tools/model_benchmark/` tested on synthetic data only | `docs/decisions/model-benchmark-plan.md` |
| Contracts A to D | JSON Schemas (draft 2020-12), 13 sample payloads, README, tests including the Interface A privacy test | `shared/contracts/` |

Manual results reported by Aleeza (not automated tests): consent screen first; Decline exits cleanly; Accept registers auto-start; Pause and Withdraw behave as designed; single instance; kill and restart records `crash_detected`; sleep records `suspend`/`resume`; logoff and restart record `clean_shutdown` and the agent restarts by itself with no `crash_detected`. The extension was tested in Chrome and Edge: heartbeat within 30 seconds after the flag is set, stops when cleared, still enabled after restarting the browser. These are also recorded for Step 3 in the root README. Claude additionally ran the extension in Edge in a throwaway profile.

## Decisions

| Decision | Outcome | Record |
|---|---|---|
| OCR trigger policy | Runs only when consent is given, not paused, OCR enabled for that app (off by default), window in foreground and user not idle, and the title unclear (under 60% confidence, empty, generic, or equal to the app name). Never for apps not opted in, hidden or excluded apps/sites, private windows, password-manager or banking-type windows, or background windows. Screenshot deleted within 5 seconds even on failure; every capture audit-logged. Only a category label and a confidence are kept (to revisit in Phase 4). Per-window capture interval N is an assumption not in the spec, to be tested in Phase 4. | `docs/decisions/ocr-assist.md` |
| OCR launch language | English (en-US) only, built-in Windows OCR. Other Windows-supported languages can be added later by installing their pack. Urdu needs another engine and is roadmap. Fallback for unreadable text: app name and window title only. | `docs/decisions/ocr-assist.md` |
| Website activation | Account and terms on the website; `dla://activate?code=...` one-time code exchanged for a revocable device token; local consent stays the source of truth for recording; no Quit. **Planned for Phase 5**, needs Fatima's backend. | `docs/decisions/website-activation.md` |
| Local database encryption | SQLCipher with a random key protected by Windows (current user), adopted only if a Phase 1 benchmark stays within the CPU and memory budget; otherwise fall back to relying on the OS and say so in the privacy text. | `docs/decisions/local-db-encryption.md` |
| OQ-A7 | Forecast features are category-level only (previous category, hour of day, weekday, switches, interruptions). App names stay on the device. App-level features only later, as an explicit opt-in. | `shared/contracts/README.md` |
| OQ-C3 | The dashboard may pause tracking remotely but may not resume it. Only the user at the PC can resume. | `shared/contracts/README.md` |
| OQ-C4 | OCR opt-in settings are not synced; local-only. | `shared/contracts/README.md` |

## Waiting on Fatima

1. **Her review of `shared/contracts/`** (five schemas, 13 samples, README).
2. **Answers to these blocking questions only** (full wording in the README's "Open questions for Fatima"):
   - **G1:** route names, status codes and error bodies for A, B and C.
   - **G2:** rate limits and maximum request sizes.
   - **G3:** how long old schema versions are accepted, and whether the server rejects (does not store) any payload with an unknown field.
   - **A1:** what "encrypted summaries" means: algorithm, who holds the key, how it is delivered, rotation.
   - **A3:** the per-hour `revision` rule (highest wins) and how corrections to past hours are re-uploaded.
   - **A5:** limits: hours per batch and maximum ciphertext size.
   - **B3:** forecast feed lifetime and what the agent does when it expires offline.
   - **D1:** activation code format, length and lifetime.
   - **D3:** device token lifetime, rotation, and how revocation reaches an offline agent.

The other open questions can wait until Phase 5. The README lists 30 questions in total; 3 are decided (OQ-A7, OQ-C3, OQ-C4) and 27 remain open, 9 of them blocking.

## Not done yet, by design

- **No real model has been benchmarked.** No model file is in the repo. The Python tools were run on synthetic data only, and their numbers are not results.
- **No OCR is built.** Only the `ocr_audit_log` table exists.
- **No activity collection.** The agent has no foreground-window, idle or title code, and the extension does not use tabs, navigation or history APIs. Nothing records app, site or title.
- **No backend link.** Neither the agent nor the extension contains any network or messaging code; the extension's agent link is a placeholder.
- **The database is not wired in.** The running agent still keeps consent, the paused flag and lifecycle markers in JSON files in `%LOCALAPPDATA%\DLA`. The SQLite layer is tested but unused by the running agent.
- Also not built: installer and signing, the `dla://` handler, SQLCipher, a real "Open dashboard", the on-device categorizer, nudges.

## Test status

Run on 2026-10-09 against the working tree, all passing, none skipped or failing:

| Suite | Command | Result |
|---|---|---|
| Agent (C#) | `cd agent` then `dotnet test` | 53 passed |
| Extension (Node) | `cd extension` then `npm test` | 7 passed |
| Benchmark tools (Python, synthetic data) | `cd agent/tools/model_benchmark` then `python -m unittest discover -s tests` | 15 passed |
| Contracts (Python) | `cd shared/contracts` then `python -m unittest discover -s tests` | 22 passed |
| **Total** | | **97 passed, 0 failed** |

Python suites need `pip install -r requirements.txt` in their folder.

## Next: Phase 1, activity collection

Phase 1 can start while Fatima reviews the contracts. It needs none of her answers except that its summary builder should follow Interface A once G1, A1, A3 and A5 are settled. Phase 1 starts with: the foreground-window and idle hooks, merging the extension's heartbeats and tab data into one timeline, writing events to SQLite (moving consent, the paused flag and the markers out of JSON), and the SQLCipher benchmark that decides the encryption condition above.
