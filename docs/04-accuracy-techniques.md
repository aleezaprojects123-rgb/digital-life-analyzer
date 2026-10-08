# Digital Life Analyzer — Project Document

*Project Document — v2.0 Market Launch Edition | Windows (v1)*

# 5. Techniques for Maximum Accuracy

| **Technique** | **How it helps** |
| --- | --- |
| Two-Source Cross-Check | Extension and agent verify each other; Chrome sleep is caught |
| Precise Timestamps | Exact start/end per event, not rounded minutes |
| Active Focus Only | Active tab/window counts; overlaps merged — no double counting |
| Smart Idle Detection | Away time excluded; video, calls, reading stay active |
| Rules-First AI | User corrections beat the model; confidence under 60% → human review |
| Efficient OCR Assist | Optional, per-app opt-in: reads on-screen text only when the window title is unclear. Active window only, on-device, screenshot deleted within 5 seconds; only a safe label is kept. Every capture appears in a user-visible audit log. |
| On-Device Inference | Categorization offline via quantized model; cloud only sees encrypted summaries |
| Semantic Goal Matching | Goal and activity compared by meaning, not keywords |
| Reliable Baselines | Weekday/weekend separated; waits for ≥14 days before forecasting |
| Forecast Validation | Every prediction logged and scored; precision reported honestly to the user |
| Raw Data & Quality Checks | Original events kept locally 30 days; gaps and crashes marked 'unknown' |
