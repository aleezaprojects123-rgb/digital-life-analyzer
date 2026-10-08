# Digital Life Analyzer — PRD

*Product Requirements Document (PRD) — v2.0 Market Launch Edition | October 2026*

# 10. Business Model

Free: tracking, timeline, reports, score, goal, Weekly Wrapped.

Pro (~$4–6/month or $39/year): distraction forecast + nudges, on-device AI coach insights, time recovery plans, calendar sync, unlimited history.

No ads, no data resale — privacy is the product, not the upsell.

# 11. Launch Plan

| **Phase** | **Duration** | **Goal** |
| --- | --- | --- |
| Pilot | Weeks 1–4 | 10–15 students use it daily; collect real data; tune forecast; capture demo moments |
| Private beta | Weeks 5–8 | 100 users via student communities; measure week-4 retention ≥ 35% |
| Public launch | Week 9 | Product Hunt + Reddit (r/getdisciplined, r/productivity) + Wrapped-share loop |

# 12. Risks and Fixes

| **Risk** | **Fix** |
| --- | --- |
| Forecast too weak with little data | Ship 'smart defaults' from aggregated patterns; show confidence; start forecast at day 14 |
| On-device model too heavy for old PCs | Quantized model + graceful fallback to rules; auto-disable below 8 GB RAM |
| Antivirus flags the agent | Signed installer; clear permission explanations |
| Nudges annoy users | Rate-limit to 2/day; auto-cooldown after 2 ignores |
| Wrapped not shared | Make export beautiful; prompt only after a genuinely good week |

# 13. Open Questions

Which quantized model for on-device categorization (test: Phi-3-mini, Qwen2.5-0.5B, or embedding + lightweight classifier)?

Forecast trained on-device or on encrypted server summaries?

Exact Pro price point from pilot willingness-to-pay?

Which to build second after Windows: macOS agent or Android collector?

Which OCR languages ship at launch (English first, which second)?
