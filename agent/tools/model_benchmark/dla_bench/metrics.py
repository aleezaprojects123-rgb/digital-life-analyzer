"""Accuracy, confidence-calibration and agreement metrics (numpy only)."""
from __future__ import annotations

import math

import numpy as np

NEEDS_REVIEW_BELOW = 0.60  # FR-7: labels under 60% confidence go to the Needs Review list


def accuracy(y_true, y_pred) -> float:
    y_true, y_pred = np.asarray(y_true), np.asarray(y_pred)
    return float((y_true == y_pred).mean()) if len(y_true) else float("nan")


def wilson_ci(successes: int, n: int, z: float = 1.96) -> tuple[float, float]:
    """95% Wilson score interval for a proportion; behaves at 0%, 100% and small n."""
    if n == 0:
        return (float("nan"), float("nan"))
    p = successes / n
    denom = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / denom
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denom
    return (max(0.0, centre - half), min(1.0, centre + half))


def required_test_size(expected_acc: float = 0.90, half_width: float = 0.02, z: float = 1.96) -> int:
    """How many test items give a 95% interval of +-half_width around the expected accuracy."""
    return math.ceil(z * z * expected_acc * (1 - expected_acc) / half_width**2)


def confusion_matrix(y_true, y_pred, k: int) -> np.ndarray:
    m = np.zeros((k, k), dtype=int)
    for t, p in zip(y_true, y_pred):
        m[t, p] += 1
    return m


def per_class(cm: np.ndarray, names: list[str]) -> dict[str, dict[str, float]]:
    out = {}
    for i, name in enumerate(names):
        tp = cm[i, i]
        fp = cm[:, i].sum() - tp
        fn = cm[i, :].sum() - tp
        precision = tp / (tp + fp) if tp + fp else float("nan")
        recall = tp / (tp + fn) if tp + fn else float("nan")
        f1 = 2 * precision * recall / (precision + recall) if precision == precision and recall == recall and precision + recall else 0.0
        out[name] = {"precision": float(precision), "recall": float(recall), "f1": float(f1), "support": int(cm[i, :].sum())}
    return out


def macro_f1(per_class_stats: dict) -> float:
    return float(np.mean([v["f1"] for v in per_class_stats.values()]))


def reliability_bins(conf, correct, bins: int = 10) -> list[dict]:
    conf, correct = np.asarray(conf, float), np.asarray(correct, float)
    edges = np.linspace(0.0, 1.0, bins + 1)
    out = []
    for lo, hi in zip(edges[:-1], edges[1:]):
        mask = (conf >= lo) & ((conf < hi) if hi < 1.0 else (conf <= hi))
        n = int(mask.sum())
        out.append({"lo": float(lo), "hi": float(hi), "n": n,
                    "mean_confidence": float(conf[mask].mean()) if n else None,
                    "accuracy": float(correct[mask].mean()) if n else None})
    return out


def ece(conf, correct, bins: int = 10) -> float:
    """Expected calibration error: average gap between stated confidence and actual accuracy."""
    total = len(conf)
    if total == 0:
        return float("nan")
    return float(sum(b["n"] / total * abs(b["accuracy"] - b["mean_confidence"]) for b in reliability_bins(conf, correct, bins) if b["n"]))


def brier(probs: np.ndarray, y_true) -> float:
    onehot = np.eye(probs.shape[1])[np.asarray(y_true)]
    return float(((probs - onehot) ** 2).sum(axis=1).mean())


def nll(probs: np.ndarray, y_true) -> float:
    p = np.clip(probs[np.arange(len(y_true)), np.asarray(y_true)], 1e-12, 1.0)
    return float(-np.log(p).mean())


def needs_review_stats(conf, correct, threshold: float = NEEDS_REVIEW_BELOW) -> dict:
    """What the 60% rule does: how many events go to review, and are the review ones really the shaky ones?"""
    conf, correct = np.asarray(conf, float), np.asarray(correct, bool)
    low = conf < threshold
    n = len(conf)

    def acc(mask):
        return float(correct[mask].mean()) if mask.any() else None

    return {
        "threshold": threshold,
        "review_rate": float(low.mean()) if n else None,
        "accuracy_auto_accepted": acc(~low),
        "accuracy_in_review": acc(low),
        "errors_caught_by_review": float((low & ~correct).sum() / (~correct).sum()) if (~correct).any() else None,
    }


def cohen_kappa(a: list[str], b: list[str]) -> float:
    """Agreement between two human labelers beyond chance. The model cannot be more accurate than people agree."""
    if not a or len(a) != len(b):
        return float("nan")
    labels = sorted(set(a) | set(b))
    n = len(a)
    po = sum(x == y for x, y in zip(a, b)) / n
    pe = sum((a.count(l) / n) * (b.count(l) / n) for l in labels)
    return float((po - pe) / (1 - pe)) if pe != 1 else 1.0


def percentiles(values, qs=(50, 95, 99)) -> dict[str, float]:
    arr = np.asarray(values, float)
    return {f"p{q}": float(np.percentile(arr, q)) for q in qs} if len(arr) else {}
