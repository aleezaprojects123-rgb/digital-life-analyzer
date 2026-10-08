"""Text -> vector. OnnxEmbedder is the real thing; HashingEmbedder exists only to test the scripts without a model."""
from __future__ import annotations

import hashlib
import re
from pathlib import Path

import numpy as np

# Presets for the three embedding candidates in the plan. Pooling and prefix are fixed by how each model was trained.
CANDIDATES = {
    "minilm": {"hf": "sentence-transformers/all-MiniLM-L6-v2", "pooling": "mean", "prefix": "", "pad_id": 0, "pad_token": "[PAD]"},
    "bge-small": {"hf": "BAAI/bge-small-en-v1.5", "pooling": "cls", "prefix": "", "pad_id": 0, "pad_token": "[PAD]"},
    "e5-small": {"hf": "intfloat/multilingual-e5-small", "pooling": "mean", "prefix": "query: ", "pad_id": 1, "pad_token": "<pad>"},
}


def _l2(x: np.ndarray) -> np.ndarray:
    return x / np.clip(np.linalg.norm(x, axis=1, keepdims=True), 1e-12, None)


class HashingEmbedder:
    """SYNTHETIC-ONLY stand-in: hashed bag of words. It is not a candidate and must never be used on real data."""

    synthetic_only = True
    name = "hashing(synthetic)"

    def __init__(self, dim: int = 128):
        self.dim = dim

    def encode(self, texts: list[str]) -> np.ndarray:
        out = np.zeros((len(texts), self.dim), dtype=np.float32)
        for i, t in enumerate(texts):
            for w in re.findall(r"[a-z0-9]+", t.lower()):
                h = int.from_bytes(hashlib.md5(w.encode()).digest()[:4], "big")
                out[i, h % self.dim] += 1.0 if (h >> 31) & 1 else -1.0
        return _l2(out)


class OnnxEmbedder:
    synthetic_only = False

    def __init__(self, model_path: str | Path, tokenizer_path: str | Path, pooling: str = "mean", prefix: str = "",
                 max_len: int = 64, threads: int = 1, pad_id: int = 0, pad_token: str = "[PAD]"):
        import onnxruntime as ort
        from tokenizers import Tokenizer

        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads   # one thread: the agent must stay under 3% CPU
        opts.inter_op_num_threads = 1
        self.session = ort.InferenceSession(str(model_path), opts, providers=["CPUExecutionProvider"])
        self.input_names = {i.name for i in self.session.get_inputs()}
        self.tokenizer = Tokenizer.from_file(str(tokenizer_path))
        self.tokenizer.enable_truncation(max_len)
        self.tokenizer.enable_padding(pad_id=pad_id, pad_token=pad_token)
        self.pooling, self.prefix = pooling, prefix
        self.name = f"onnx:{Path(model_path).name}"

    def encode(self, texts: list[str]) -> np.ndarray:
        enc = self.tokenizer.encode_batch([self.prefix + t for t in texts])
        ids = np.array([e.ids for e in enc], dtype=np.int64)
        mask = np.array([e.attention_mask for e in enc], dtype=np.int64)
        feed = {"input_ids": ids}
        if "attention_mask" in self.input_names:
            feed["attention_mask"] = mask
        if "token_type_ids" in self.input_names:
            feed["token_type_ids"] = np.zeros_like(ids)
        out = self.session.run(None, feed)[0]
        if out.ndim == 3:  # token vectors -> one vector per text
            if self.pooling == "cls":
                out = out[:, 0, :]
            else:
                m = mask[..., None].astype(out.dtype)
                out = (out * m).sum(axis=1) / np.clip(m.sum(axis=1), 1e-9, None)
        return _l2(out.astype(np.float32))


def quantize_int8(src: str | Path, dst: str | Path) -> int:
    """Dynamic int8 quantization of a float ONNX model (weights int8, no calibration data needed). Returns bytes."""
    from onnxruntime.quantization import QuantType, quantize_dynamic

    quantize_dynamic(str(src), str(dst), weight_type=QuantType.QInt8)
    return Path(dst).stat().st_size
