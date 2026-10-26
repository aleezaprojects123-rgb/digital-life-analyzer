# Digital Life Analyzer: project rules

University team project (Windows only). Two people: Aleeza owns `device/`, Fatima owns `cloud/`. `contracts/` is shared and needs both approvals.

## What we are building
- A background Windows agent written in **C# (.NET 8)**, tray icon only, that logs the foreground app and idle time.
- A **Chrome/Edge extension (Manifest V3)** that logs the active website.
- Both write events using the schema in `contracts/event-schema.md` into a local SQLite database, kept 30 days, then merged into one timeline.
- An on-device AI model (ONNX Runtime) labels time as Study, Work, Entertainment, Social Media or Other, with a confidence score. Low confidence becomes "Other". A rule-based fallback works if the model is off.
- Only encrypted daily summaries (`contracts/summary-format.md`) go to the cloud. Never raw events, page text or window titles.
- Cloud side (Fatima): FastAPI + PostgreSQL, React dashboard, Stripe test mode.

## Hard rules
- No screenshots, no keystrokes, no typed text, private windows never recorded.
- Raw events stay on the PC. Everything must work offline.
- Hidden apps and sites are skipped before anything is read.
- Tracking must stay light: low CPU and memory.
- The agent must be packaged as a normal `.exe` that starts with Windows; users never run code.

## Working rules
- Work on your own branch (`aleeza/day-XX-name`). Never push straight to main. The other person reviews every pull request.
- Only edit your own folder. Contract changes go in `contracts/`.
- Do one task at a time, explain each file briefly, and say how to run and test it.
