"""Builds a tiny SYNTHETIC ONNX 'embedding model' and tokenizer so the real OnnxEmbedder code path can be tested
without downloading anything. It is a random lookup table: it embeds nothing meaningful."""
from __future__ import annotations

import re
from pathlib import Path

import numpy as np


def build(out_dir: Path, texts: list[str], dim: int = 32, seed: int = 3) -> tuple[Path, Path]:
    import onnx
    from onnx import TensorProto, helper, numpy_helper
    from tokenizers import Tokenizer
    from tokenizers.models import WordLevel
    from tokenizers.pre_tokenizers import Whitespace

    words = sorted({w for t in texts for w in re.findall(r"\w+|[^\w\s]", t.lower())})
    vocab = {"[PAD]": 0, "[UNK]": 1, **{w: i + 2 for i, w in enumerate(words)}}

    tok = Tokenizer(WordLevel(vocab, unk_token="[UNK]"))
    tok.pre_tokenizer = Whitespace()
    from tokenizers import normalizers
    tok.normalizer = normalizers.Lowercase()
    tok_path = out_dir / "toy_tokenizer.json"
    tok.save(str(tok_path))

    table = np.random.default_rng(seed).normal(size=(len(vocab), dim)).astype(np.float32)
    ids = helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "seq"])
    mask = helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "seq"])
    out = helper.make_tensor_value_info("last_hidden_state", TensorProto.FLOAT, ["batch", "seq", dim])
    graph = helper.make_graph(
        [helper.make_node("Gather", ["table", "input_ids"], ["last_hidden_state"], axis=0),
         helper.make_node("Identity", ["attention_mask"], ["_unused_mask"])],
        "toy", [ids, mask], [out], initializer=[numpy_helper.from_array(table, "table")],
        value_info=[helper.make_tensor_value_info("_unused_mask", TensorProto.INT64, ["batch", "seq"])])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 9
    onnx.checker.check_model(model)
    model_path = out_dir / "toy_model.onnx"
    onnx.save(model, str(model_path))
    return model_path, tok_path
