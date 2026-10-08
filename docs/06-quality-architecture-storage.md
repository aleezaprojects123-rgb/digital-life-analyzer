# Digital Life Analyzer — PRD

*Product Requirements Document (PRD) — v2.0 Market Launch Edition | October 2026*

# 7. Quality Requirements

| **Area** | **Requirement** |
| --- | --- |
| Light and fast | < 3% CPU, 150 MB base RAM; 250 MB cap with model loaded |
| Reliable | Restarts after crash/reboot; gaps marked 'unknown' |
| Secure | TLS in transit, encryption at rest, signed installer |
| Private | Raw events and OCR never leave the device; typing never recorded; sensitive titles masked; easy delete |
| Offline-first | Tracking + AI categorization + nudges work with zero internet |
| Easy | Setup < 5 minutes; visible tray icon; auto-suggested goal from history |

# 8. Architecture

| **Part** | **Job** | **Tool** |
| --- | --- | --- |
| Browser Extension | Active tab recording; wake-safe heartbeats | Chrome / Edge MV3 |
| Desktop Agent | App/idle recording, local AI (ONNX Runtime, quantized model), optional OCR assist, nudges, rules engine, SQLite | Python or .NET |
| Backend + DB | Accounts, encrypted summaries, Wrapped assets, Stripe | FastAPI + PostgreSQL |
| Analysis Engine | Score, best hours, forecast model training (per-user, nightly) | Python (scikit-learn / XGBoost) |
| Dashboard | Reports, forecast heatmap, Wrapped, settings | React |

*Key design decision: the forecast model trains on aggregated summaries server-side (or fully locally in v1.1); categorization is strictly on-device.*

# 9. What We Store

| **Item** | **Where** | **Details** |
| --- | --- | --- |
| Raw events | Local device only | App, site, title, timestamps — never uploaded by default |
| Activity summaries | Cloud (encrypted) | Time by category, confidence, per hour |
| Rules | Cloud (encrypted) | User corrections |
| Goal + score + forecast | Cloud (encrypted) | Daily goal, match %, risk windows |
| Account + settings | Cloud | Login, preferences, subscription |
