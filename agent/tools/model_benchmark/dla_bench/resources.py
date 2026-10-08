"""Process memory and file-size measurement, plus the pass/fail gates taken from the spec."""
from __future__ import annotations

import os
from pathlib import Path

import psutil

MB = 1024 * 1024

# Gates. "spec" ones come straight from the spec; "assumed" ones are mine and are labelled as such in the plan.
GATES = {
    "disk_mb": 500,              # spec: <= 500 MB on disk (FR-22)
    "accuracy": 0.90,            # spec: >= 90% correct category
    "model_ram_mb": 100,         # derived: 250 MB cap with model minus the 150 MB base budget
    "latency_p95_ms": 100,       # assumed
    "ece": 0.05,                 # assumed
    "min_class_recall": 0.80,    # assumed
}


def rss_mb() -> float:
    return psutil.Process(os.getpid()).memory_info().rss / MB


def peak_mb() -> float:
    """Peak working set since process start (Windows); falls back to current RSS elsewhere."""
    info = psutil.Process(os.getpid()).memory_info()
    return getattr(info, "peak_wset", info.rss) / MB


def files_mb(*paths: str | Path | None) -> float:
    """Total on-disk size of the model files. Counts ONNX external-data files that sit next to a model."""
    total = 0
    for p in paths:
        if not p:
            continue
        p = Path(p)
        if p.is_file():
            total += p.stat().st_size
            total += sum(s.stat().st_size for s in p.parent.glob(p.name + ".data"))
    return total / MB
