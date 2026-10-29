# Mock data

Made-up data for building the dashboard and the reports before the agent sends real summaries (Day 8). **Test data only: the repository is public, so never put real activity here.**

| File | What it is |
|---|---|
| `summary-2026-10-28.json` | One day of a made-up user, in the exact format of Contract 2 ([contracts/summary-format.md](../../contracts/summary-format.md)). It is the base sample [contracts/samples/summary-valid.json](../../contracts/samples/summary-valid.json) with only `date` and `generated_at` moved one day later. |

The tests in `backend/tests/test_mock_summary.py` check this file against the Contract 2 schema and the seven server checks, and check that it still matches the contract sample. If the contract changes, they fail until this file is updated.

Later, for the weekly report and the forecast, more days are needed (the final demo data is Day 15). Add them here as `summary-YYYY-MM-DD.json`, one file per day.
