# Model benchmark tools

Offline scripts that measure candidate on-device categorization models. They are **not part of the agent app** and are never shipped. The plan and the reasoning are in [docs/decisions/model-benchmark-plan.md](../../../docs/decisions/model-benchmark-plan.md).

> **Everything under `synthetic/` is invented data.** It only proves the scripts work. Any number produced from it is
> labelled `SYNTHETIC` in the terminal and in the report file name and JSON, and must never be quoted as a result.
> No real model has been run yet.

## Setup (Python 3.11+; tested on 3.14, Windows)

```bash
cd agent/tools/model_benchmark
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

## Run the self-test (synthetic data only)

```bash
python -m unittest discover -s tests -v
python bench.py check-data --data synthetic/synthetic_titles.csv
python bench.py run --data synthetic/synthetic_titles.csv --embedder hashing
```

## Real use (later, with real labeled titles)

1. Export titles from your local database (read-only, scrubbed), then label the `label` column by hand:
   ```bash
   python bench.py export-titles --db "%LOCALAPPDATA%\DLA\dla.db" --out data/to_label.csv
   ```
   `data/` is git-ignored. Real titles must never be committed or uploaded.
2. Check the labeled file and the split:
   ```bash
   python bench.py check-data --data data/labeled.csv
   ```
3. Gate 0 on any ONNX model (size + memory after loading):
   ```bash
   python bench.py gate --model models/model.onnx
   ```
4. Full run for an embedding candidate (`--candidate` is minilm, bge-small or e5-small):
   ```bash
   python bench.py run --data data/labeled.csv --embedder onnx --model models/minilm/model.onnx --tokenizer models/minilm/tokenizer.json --candidate minilm --export-dir models/minilm
   ```
   Reports go to `reports/` (git-ignored). `--export-dir` writes `classifier.json`, the weights the C# agent will use.

The hashing embedder refuses to run on rows marked `origin=real`.
