# Digital Life Analyzer — PRD

*Product Requirements Document (PRD) — v2.0 Market Launch Edition | October 2026*

# 6. Functional Requirements

## 6.1 Collecting activity (foundation)

| **ID** | **Requirement** | **Priority** | **Done when** |
| --- | --- | --- | --- |
| FR-1 | Records active website and duration. Private windows never recorded. | P0 | Only active tab counts |
| FR-2 | Records foreground app and duration. | P0 | Switches logged within 1 second |
| FR-3 | Idle detection; away time not counted. Videos/calls count as active. | P0 | Default 3-minute threshold |
| FR-4 | Raw events stored locally for error tracing. No screenshots. | P0 | 30-day local retention, user-configurable |

## 6.2 Cleaning, labeling, sync

| **ID** | **Requirement** | **Priority** | **Done when** |
| --- | --- | --- | --- |
| FR-5 | Merge browser + app records into one timeline. No overlaps, no double counting. | P0 | Same moment counted once |
| FR-6 | On-device categorization: rules first, then a local AI model (ONNX, quantized). Confidence on every label. | P0 | Works with internet unplugged |
| FR-7 | Events under 60% confidence go to a Needs Review list. | P0 | User correction one tap |
| FR-8 | Corrections saved as personal rules; 'apply to all similar'. | P0 | Rule applied to past + future |
| FR-9 | Sync encrypted summaries to server. Works offline; no duplicates. | P0 | Raw events stay local by default |

## 6.3 Core reports (foundation)

| **ID** | **Requirement** | **Priority** | **Done when** |
| --- | --- | --- | --- |
| FR-10 | Time by category, daily and weekly, with week-over-week comparison. | P0 | — |
| FR-11 | 0–100 productivity score with reasons. | P0 | — |
| FR-12 | Goal alignment: how much activity matched the daily goal (semantic match). | P0 | — |
| FR-13 | App/tab switches per hour; interruption count. | P0 | — |
| FR-15 | Best focus hours detected after 14 days of data. | P0 | Weekday/weekend separated |
