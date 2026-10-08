"""Lightweight classifier on top of embeddings + temperature scaling, exportable as plain JSON for the C# agent."""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
from scipy.optimize import minimize_scalar
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import StratifiedKFold, cross_val_score

from .data import LABELS


def softmax(z: np.ndarray) -> np.ndarray:
    z = z - z.max(axis=1, keepdims=True)
    e = np.exp(z)
    return e / e.sum(axis=1, keepdims=True)


def fit_temperature(logits: np.ndarray, y: np.ndarray) -> float:
    """One scalar T that makes confidences honest. Fit on the calibration split only, never on test."""
    def nll(log_t: float) -> float:
        p = softmax(logits / np.exp(log_t))
        return float(-np.log(np.clip(p[np.arange(len(y)), y], 1e-12, 1.0)).mean())

    res = minimize_scalar(nll, bounds=(np.log(0.05), np.log(20.0)), method="bounded")
    return float(np.exp(res.x))


class Classifier:
    def __init__(self, model: LogisticRegression, temperature: float, c: float):
        self.model, self.temperature, self.c = model, temperature, c

    def logits(self, x: np.ndarray) -> np.ndarray:
        return self.model.decision_function(x)

    def proba(self, x: np.ndarray, calibrated: bool = True) -> np.ndarray:
        return softmax(self.logits(x) / (self.temperature if calibrated else 1.0))

    def export(self, path: str | Path, meta: dict) -> int:
        """Writes weights + temperature as JSON (a few KB). Returns the file size in bytes."""
        doc = {
            "labels": LABELS,
            "coef": self.model.coef_.tolist(),
            "intercept": self.model.intercept_.tolist(),
            "temperature": self.temperature,
            "needs_review_below": 0.60,
            **meta,
        }
        p = Path(path)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(doc), encoding="utf-8")
        return p.stat().st_size


def predict_from_export(doc: dict, x: np.ndarray) -> np.ndarray:
    """Reference implementation of exactly what the C# agent must do. Phase 3 parity tests compare against this."""
    logits = x @ np.asarray(doc["coef"]).T + np.asarray(doc["intercept"])
    return softmax(logits / doc["temperature"])


def train(x_train, y_train, x_cal, y_cal, c_grid=(0.1, 1.0, 10.0, 100.0), seed: int = 13) -> Classifier:
    x_train, y_train = np.asarray(x_train), np.asarray(y_train)
    smallest = int(np.bincount(y_train, minlength=len(LABELS)).min())
    if smallest >= 2:
        folds = StratifiedKFold(n_splits=min(5, smallest), shuffle=True, random_state=seed)
        scores = {c: cross_val_score(LogisticRegression(C=c, max_iter=3000), x_train, y_train, cv=folds).mean() for c in c_grid}
        best_c = max(scores, key=scores.get)
    else:
        best_c = 10.0
    model = LogisticRegression(C=best_c, max_iter=3000).fit(x_train, y_train)
    t = fit_temperature(model.decision_function(np.asarray(x_cal)), np.asarray(y_cal))
    return Classifier(model, t, best_c)
