# Plan: on-device categorization model benchmark

Status: **plan and tooling only. No model has been downloaded, benchmarked or integrated.** Nothing in the agent app uses a model.
Owner: Aleeza (agent). Spec: FR-6, FR-7, FR-22, "On-Device Inference", quality requirements (150 MB / 250 MB / 3% CPU), open question "Which quantized model for on-device categorization".
Tooling: [agent/tools/model_benchmark/](../../agent/tools/model_benchmark/README.md) (Python, outside the agent app).

## What must be true (the gates)

| Gate | Value | Where it comes from |
|---|---|---|
| Disk | model + tokenizer + classifier <= 500 MB | spec (FR-22) |
| Accuracy | >= 90% correct category on held-out titles | spec (success measures) |
| RAM | whole agent < 250 MB with the model loaded | spec. Derived budget for the model: 250 - 150 (base budget) = **100 MB** |
| CPU | agent average < 3% | spec. Translated into **p95 latency <= 100 ms per event, one thread** (assumption, not in spec) |
| Offline | no network at any point | spec |
| Confidence | every label has a score we can calibrate | spec (events < 60% go to Needs Review, FR-7) |
| Calibration | expected calibration error (ECE) <= 0.05 after calibration | assumption, not in spec |
| Balance | recall >= 80% in every one of the 5 categories | assumption, not in spec |

The accuracy gate is judged on the **lower end of the 95% interval** as well as the point estimate, so a lucky small test set cannot pass.

## The three candidates, checked against the gates

Facts below were read from the model repositories on 2026-10-09. Items I could not read are marked **unverified**.

| Candidate | License (as listed on its page) | Size on disk (ONNX) | Languages | Verdict before any benchmark |
|---|---|---|---|---|
| **Phi-3-mini-4k-instruct** (`microsoft/Phi-3-mini-4k-instruct-onnx`) | MIT | CPU int4: weights 2,722,861,056 bytes (**2.7 GB**) | English-centric | **Fails the 500 MB gate by about 5x.** Excluded. |
| **Qwen2.5-0.5B-Instruct** (`Qwen/Qwen2.5-0.5B-Instruct`; ONNX from `onnx-community/Qwen2.5-0.5B-Instruct`) | Apache-2.0 on the original model. The ONNX conversion repo lists **no license field**, so treat it as derived from Apache-2.0 and confirm before shipping | q4f16 483,003,582 bytes; int8 512,096,557; q4 786,156,820; fp16 997,354,499 | Multilingual | Smallest variant is 483 MB: barely under 500 MB, and the weights alone exceed the 100 MB model RAM budget. **Expected to fail RAM and latency** (an LLM must generate text per event). Confirm cheaply with Gate 0, below. |
| **Embedding model + lightweight classifier** | see next rows | see next rows | see next rows | Fits all gates on paper. Main candidate. |
| - `sentence-transformers/all-MiniLM-L6-v2` | Apache-2.0 | 22.7M parameters. ONNX: fp32 90,405,214 bytes; AVX2-quantized `model_quint8_avx2.onnx` 23,046,789; AVX512 and ARM64 variants about 23.0 MB | English | Smallest. Needs a CPU-matched quantized file or our own quantization. |
| - `BAAI/bge-small-en-v1.5` | MIT | 33.4M parameters. `onnx/model.onnx` about 133 MB (fp32, the only ONNX file listed); int8 size **unverified** until we quantize it ourselves | English | Stronger embeddings, larger. |
| - `intfloat/multilingual-e5-small` | MIT (listed in its tags) | **Unverified** in this pass: the ONNX file list and parameter count could not be read. The script reports real sizes when the files are downloaded | 93 language tags | The only candidate that helps non-English titles. Needs the `query: ` prefix. |

Disk and RAM claims about the LLMs are file-size arithmetic plus the fact that runtime memory is at least the weights; they are not measurements. Gate 0 below measures them.

### Licenses: what was and was not verified

- Verified from each model page or its metadata on 2026-10-09: Phi-3 MIT, Qwen2.5-0.5B-Instruct Apache-2.0, all-MiniLM-L6-v2 Apache-2.0, bge-small-en-v1.5 MIT, multilingual-e5-small MIT.
- **Not verified:** the license of the `onnx-community` Qwen conversion; the licenses of the datasets these models were trained on; whether any attribution notice must ship inside the installer (MIT and Apache-2.0 both require keeping their license text with redistributed copies). **A licence check by someone qualified is advisable before the paid Pro tier ships a model.**
- The ONNX Runtime and tokenizer libraries are not covered by this note.

## Recommendation

Benchmark the **embedding + classifier** approach first, on MiniLM and bge-small, with multilingual-e5-small as the language fallback. Use Gate 0 once on Qwen2.5-0.5B q4f16 to put a measured number on "too big", then stop. Drop Phi-3 without benchmarking.

The classifier is a **multinomial logistic regression on the embedding** (a few KB: one matrix and 5 biases) plus a **temperature** that makes the confidence honest. The C# agent only needs matrix math for that, so the Python side exports `classifier.json` and a Phase 3 parity test compares C# output to it. Rules still run first (FR-6): this model sees only titles the rules did not settle.

## How each number is measured

All measured by `bench.py run`; defaults in parentheses.

| What | How |
|---|---|
| **Accuracy** | Held-out test split, one title at a time through the real model. Reports accuracy with a 95% Wilson interval, macro-F1, per-category precision and recall, and the confusion matrix. |
| **Per-event latency** | Each test title encoded alone (batch of 1, one thread), timed individually. Reports p50, p95, p99, plus CPU seconds per event. At an assumed 2,000 classifications a day, 50 ms each is under two minutes of CPU per day, far below 3%. |
| **File size** | Model file + tokenizer + `classifier.json`, including any ONNX external-data file next to the model. |
| **Peak RAM** | Memory measured after the Python libraries are imported (baseline), after the model loads, and the peak working set after all test titles are processed. The model's cost is the **delta from the baseline**. Python's own overhead is excluded. This is a lower bound: the final number is measured again in the C# agent in Phase 3. |
| **Confidence calibration** | Confidence = highest class probability. Temperature fitted on the **calibration split only**. Reported on the test split before and after: ECE (10 bins), Brier score, log-loss, and the reliability table. |
| **What the 60% rule does** | Share of events sent to Needs Review at confidence < 0.60; accuracy of auto-accepted events; accuracy inside Needs Review; share of all errors that Needs Review catches. A good calibration means review is where the mistakes are. |
| **Text format** | Compared: `full` (app + site + title) vs `title` only. |
| **Seen vs unseen sites** | `--split-mode group` (default, pessimistic: no site appears in two splits) vs `random` (optimistic). The gap shows how much the model leans on memorized sites; personal rules (FR-8) cover repeat sites in production. |

**Gate 0** (`bench.py gate --model x.onnx`) loads any ONNX file and reports its disk size and memory after loading, with no accuracy work. A model that fails here is out. It works for the LLMs because it does not need to run them.

### Honest limit of the Python numbers
The tool measures the model in a Python process. ONNX Runtime in C# uses the same engine, so accuracy, calibration and file size should carry over, **provided the C# tokenizer produces the same tokens as the Python one** (the risk named in the Step 1 language decision). Memory and latency are close but are re-measured in the agent in Phase 3, along with a parity test (same input gives the same vector and probabilities in Python and C#).

## Building the labeled evaluation set from real titles

**Where the titles come from.** The local database `events` table of the pilot users (Phase 1 onward, pilot weeks 1-4) and Aleeza's own. Until recording exists, titles can be added by hand from a sample of browsing.

**Privacy rules (the repo is public).**
- The labeled file holds real browsing data. It lives in `agent/tools/model_benchmark/data/`, which is **git-ignored**, and is never uploaded or shared outside the team.
- `bench.py export-titles` reads the database read-only, keeps distinct (app, site, title) rows only (no timestamps, no durations), and **scrubs** e-mail addresses, long digit runs and URL query strings.
- Pilot users must agree to this use of their titles; this is a consent question for the pilot, not a technical one.
- Only the **synthetic** sample is committed.

**File format.** One CSV, UTF-8 (Excel's BOM is accepted), one row per distinct title.

| Column | Meaning |
|---|---|
| `id` | unique id (`r00001`) |
| `origin` | `real` or `synthetic`. The tool refuses to mix them. |
| `source` | `agent` or `extension` |
| `app`, `site`, `title` | what the event had; `site` is the domain only |
| `label` | one of Study, Work, Entertainment, Social Media, Other |
| `label_b` | optional second labeler, used for agreement |
| `notes` | free text, ignored by the tools |

**Labeling guide** (write the final version with the first 100 items):
- Label by what the activity is *for*, using app, site and title together.
- Study: courses, textbooks, lecture videos, learning-oriented documentation. Work: IDEs, office tools, work e-mail and chat, project tools. Entertainment: streaming, games, music, memes. Social Media: feeds and social messaging. Other: system windows, file manager, maps, anything with no clear purpose.
- When it truly could be either (a YouTube video with a vague title), pick the most likely, and note it. Do not leave blanks; empty or generic titles are *kept*, because the model must learn to be unsure about them.

**Size.**

| Test size | 95% interval around 90% |
|---|---|
| 86 (the synthetic file) | about plus or minus 6% |
| 385 | plus or minus 3% |
| 865 | plus or minus 2% |

- Stage 1 (pilot): about **1,200 labeled titles**, which gives a test set near 300-400 (plus or minus 3%) and is enough to rule candidates out.
- Stage 2 (private beta): grow to about **3,000** so the final accuracy claim has a test set near 865 (plus or minus 2%).
- At least **100 per category** in total, and every category present in every split. The tool stops if a split is missing a class.
- Real activity is lopsided; Other and Entertainment may be rare. Where a class is short, add titles on purpose rather than accepting a weak class.

**Splits.** 60% train, 15% calibration, 25% test, fixed seed. A stratified **group split**: all titles from the same site (or app) land in one split, so the test measures unseen sites. The tool balances classes across splits automatically. The calibration split is used only to fit the temperature; the test split is touched once per candidate.

**Label quality.** Have a second person label 200 titles. The tool reports agreement and Cohen's kappa. The model cannot honestly be more accurate than people agree; if agreement is below 90%, the 90% target needs discussion with Fatima before any model is blamed.

## Can the same model support goal matching (FR-12)?

**Yes, if we choose an embedding model, and that is a reason to prefer one.**
- FR-12 compares the daily goal text with activity "by meaning". That is cosine similarity between two embeddings; the model already computes the activity embedding, so goal matching costs **no extra RAM and no extra file**.
- A generative LLM could do it, but at far higher cost and latency, and it would not be calibrated.
- Caveats: (1) the similarity threshold needs its own small labeled set of goal/activity pairs (Phase 3), because category accuracy does not prove the threshold is right; (2) e5 expects `query: ` prefixes, so goal and activity are encoded with the same prefix rule; (3) MiniLM and bge-small are English, so non-English users would need e5.

## Pass / fail summary (what "done" will mean in Phase 3/4)

1. Gate 0 run on Qwen2.5-0.5B q4f16, recorded as the reason LLMs are out (or surprising us).
2. For each embedding candidate: all gates in the table above pass on a real test set of at least 385 titles, `--split-mode group`, text format chosen by the better of `full` and `title`.
3. `classifier.json` plus the quantized model chosen, and the licence text for the chosen model added to the installer notes.
4. Parity test (Python vs C#) agreed before the model is integrated.

## Not decided here

- Which candidate wins (needs real data).
- Whether to ship one multilingual model or an English model plus a language-specific add-on (depends on the OCR and launch-language decision).
- Whether quantization is per-CPU (AVX2/AVX512/ARM64 files) or a single generic int8 file; the installer and the 500 MB limit make a single file simpler.
- Who labels, and the pilot consent wording for using titles.
