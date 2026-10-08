# Digital Life Analyzer — PRD

*Product Requirements Document (PRD) — v2.0 Market Launch Edition | October 2026*

## 6.4 The five market differentiators

| **ID** | **Requirement** | **Priority** | **Done when** |
| --- | --- | --- | --- |
| FR-20 | PREDICTIVE DISTRACTION FORECAST: per-user model (gradient boosting / logistic regression) trained on hour-of-day, day-of-week, current and previous app, session length, and history. Shows today's risk as an hourly heatmap. | P0 | Trains after 14 days; ≥60% precision on flagged hours |
| FR-21 | PREEMPTIVE NUDGE: desktop notification ~5 minutes before a predicted high-risk window with a suggested focus block. Snoozable, off by default after 2 ignores. | P0 | User can snooze/turn off |
| FR-22 | ON-DEVICE AI SHOWCASE: classification runs entirely in the agent (ONNX Runtime, quantized model ≤ 500 MB). Agent keeps working fully offline. | P0 | Unplug cable demo: categorization continues |
| FR-23 | TIME RECOVERY CALCULATOR: converts lost time (switching cost, entertainment over baseline) into hours and relatable units (study chapters, episodes). Weekly recovery plan: up to 3 concrete actions. | P0 | Each action has expected hours recovered |
| FR-24 | WEEKLY WRAPPED: auto-generated every Monday. Story-style cards (deepest session, top distraction, hours recovered vs. last week). One-click share as image. | P0 | Shareable PNG export |
| FR-25 | BEST-HOURS CALENDAR SYNC: OAuth to Google Calendar and Microsoft Graph; pushes detected focus hours as 'Focus Block' events in one click. | P0 | Event created with correct timezone |
