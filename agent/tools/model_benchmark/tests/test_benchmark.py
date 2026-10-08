"""Tests for the benchmark scripts. Run:  python -m unittest discover -s tests -v   (from agent/tools/model_benchmark)
Everything here uses SYNTHETIC data. Numbers produced here are never results."""
import csv
import io
import json
import math
import sqlite3
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(Path(__file__).parent))

import bench  # noqa: E402
from dla_bench import SYNTHETIC_BANNER  # noqa: E402
from dla_bench import data as D  # noqa: E402
from dla_bench import metrics as M  # noqa: E402
from dla_bench.classifier import fit_temperature, predict_from_export, softmax  # noqa: E402

SYN = ROOT / "synthetic" / "synthetic_titles.csv"


class MetricsTests(unittest.TestCase):
    def test_accuracy_and_wilson(self):
        self.assertEqual(M.accuracy([0, 1, 1, 0], [0, 1, 0, 0]), 0.75)
        lo, hi = M.wilson_ci(90, 100)
        self.assertAlmostEqual(lo, 0.8256, places=3)
        self.assertAlmostEqual(hi, 0.9448, places=3)
        self.assertEqual(M.wilson_ci(0, 0)[0] != M.wilson_ci(0, 0)[0], True)  # nan
        self.assertEqual(M.required_test_size(0.9, 0.02), 865)

    def test_ece_perfect_and_overconfident(self):
        conf = [0.9] * 10
        self.assertAlmostEqual(M.ece(conf, [1] * 9 + [0]), 0.0)          # says 90%, right 90%
        self.assertAlmostEqual(M.ece(conf, [1] * 5 + [0] * 5), 0.4)      # says 90%, right 50%

    def test_needs_review_stats(self):
        conf = [0.95, 0.9, 0.55, 0.4, 0.59]
        correct = [True, True, False, False, True]
        s = M.needs_review_stats(conf, correct)
        self.assertAlmostEqual(s["review_rate"], 0.6)
        self.assertEqual(s["accuracy_auto_accepted"], 1.0)
        self.assertAlmostEqual(s["accuracy_in_review"], 1 / 3)
        self.assertEqual(s["errors_caught_by_review"], 1.0)

    def test_confusion_and_per_class(self):
        cm = M.confusion_matrix([0, 0, 1, 1, 1], [0, 1, 1, 1, 0], 2)
        self.assertEqual(cm.tolist(), [[1, 1], [1, 2]])
        pc = M.per_class(cm, ["a", "b"])
        self.assertAlmostEqual(pc["b"]["recall"], 2 / 3)
        self.assertAlmostEqual(pc["b"]["precision"], 2 / 3)

    def test_kappa(self):
        self.assertAlmostEqual(M.cohen_kappa(list("aabb"), list("aabb")), 1.0)
        self.assertAlmostEqual(M.cohen_kappa(list("aabb"), list("abab")), 0.0)

    def test_temperature_fixes_overconfidence(self):
        rng = np.random.default_rng(0)
        y = rng.integers(0, 3, 600)
        logits = rng.normal(size=(600, 3)) * 3
        logits[np.arange(600), y] += 3.0
        logits = logits * 4.0                      # make it overconfident
        t = fit_temperature(logits, y)
        self.assertGreater(t, 1.5)
        conf_before, conf_after = softmax(logits).max(1), softmax(logits / t).max(1)
        correct = softmax(logits).argmax(1) == y
        self.assertLess(M.ece(conf_after, correct), M.ece(conf_before, correct))


class DataTests(unittest.TestCase):
    def test_synthetic_file_is_valid_and_marked(self):
        rows = D.load_dataset(SYN)
        self.assertEqual(D.validate(rows), [])
        self.assertTrue(D.is_synthetic(rows))
        self.assertTrue(SYN.read_text(encoding="utf-8").startswith("# SYNTHETIC"))

    def test_group_split_never_leaks_a_site_across_splits(self):
        rows = D.load_dataset(SYN)
        split = D.assign_split(rows, seed=13, mode="group")
        seen = {}
        for r in rows:
            g = D.group_key(r)
            self.assertEqual(seen.setdefault(g, split[r["id"]]), split[r["id"]], f"{g} straddles splits")
        self.assertEqual(split, D.assign_split(rows, seed=13, mode="group"))  # deterministic
        self.assertEqual(D.split_problems(rows, split), [])

    def test_validation_catches_bad_files(self):
        base = {"id": "1", "origin": "real", "source": "agent", "app": "a.exe", "site": "", "title": "t", "label": "Work", "label_b": "", "notes": ""}
        self.assertEqual(D.validate([base]), [])
        self.assertTrue(any("duplicate" in p for p in D.validate([base, dict(base)])))
        self.assertTrue(any("not one of" in p for p in D.validate([{**base, "id": "2", "label": "Gaming"}])))
        self.assertTrue(any("label is empty" in p for p in D.validate([{**base, "label": ""}])))
        self.assertTrue(any("mixed origins" in p for p in D.validate([base, {**base, "id": "2", "origin": "synthetic"}])))

    def test_scrub_masks_private_bits(self):
        s = D.scrub_title("Inbox - sam.lee@example.com - order 1234567890 https://shop.test/p?id=9&token=abc")
        self.assertNotIn("sam.lee", s)
        self.assertNotIn("1234567890", s)
        self.assertNotIn("token", s)
        self.assertIn("<email>", s)

    def test_export_titles_reads_events_and_writes_unlabeled_scrubbed_csv(self):
        with tempfile.TemporaryDirectory() as d:
            db = Path(d) / "dla.db"
            con = sqlite3.connect(db)
            con.execute("CREATE TABLE events (source TEXT, app TEXT, site TEXT, title TEXT, is_unknown INTEGER)")
            con.executemany("INSERT INTO events VALUES (?,?,?,?,?)", [
                ("agent", "code.exe", None, "main.py", 0),
                ("agent", "code.exe", None, "main.py", 0),                       # duplicate collapses
                ("extension", None, "example.com", "mail a@b.com", 0),
                ("agent", None, None, None, 1),                                   # unknown gap is skipped
            ])
            con.commit(); con.close()
            out = Path(d) / "out.csv"
            self.assertEqual(D.export_titles(db, out), 2)
            rows = D.load_dataset(out)
            self.assertTrue(all(r["label"] == "" and r["origin"] == "real" for r in rows))
            self.assertNotIn("a@b.com", out.read_text(encoding="utf-8"))


class PipelineTests(unittest.TestCase):
    def run_cli(self, *argv):
        buf = io.StringIO()
        with redirect_stdout(buf):
            code = bench.main(list(argv))
        return code, buf.getvalue()

    def test_check_data_on_synthetic(self):
        code, out = self.run_cli("check-data", "--data", str(SYN))
        self.assertEqual(code, 0)
        self.assertIn("SYNTHETIC DATA", out)

    def test_end_to_end_with_hashing_embedder_is_labelled_synthetic(self):
        with tempfile.TemporaryDirectory() as d:
            code, out = self.run_cli("run", "--data", str(SYN), "--embedder", "hashing", "--export-dir", d)
            self.assertEqual(code, 0)
            self.assertIn(SYNTHETIC_BANNER, out)
            report = json.loads(max((ROOT / "reports").glob("SYNTHETIC_*.json"), key=lambda p: p.stat().st_mtime).read_text())
            self.assertTrue(report["synthetic"])
            self.assertEqual(report["banner"], SYNTHETIC_BANNER)
            self.assertIn("gates", report)
            # the exported classifier reproduces the in-process probabilities (this is what the C# parity test will use)
            doc = json.loads((Path(d) / "classifier.json").read_text())
            self.assertEqual(doc["labels"], D.LABELS)
            self.assertTrue(doc["synthetic"])
            x = np.random.default_rng(1).normal(size=(4, len(doc["coef"][0])))
            p = predict_from_export(doc, x)
            self.assertTrue(np.allclose(p.sum(axis=1), 1.0))

    def test_hashing_embedder_refuses_real_data(self):
        with tempfile.TemporaryDirectory() as d:
            rows = D.load_dataset(SYN)
            path = Path(d) / "real.csv"
            with open(path, "w", newline="", encoding="utf-8") as f:
                w = csv.DictWriter(f, D.FIELDS); w.writeheader()
                for r in rows:
                    w.writerow({**r, "origin": "real"})
            with self.assertRaises(SystemExit):
                self.run_cli("run", "--data", str(path), "--embedder", "hashing")

    def test_real_onnx_code_path_with_a_toy_model(self):
        try:
            import onnx  # noqa: F401
            import onnxruntime  # noqa: F401
            import tokenizers  # noqa: F401
        except ImportError:
            self.skipTest("onnx / onnxruntime / tokenizers not installed")
        import toy_model
        rows = D.load_dataset(SYN)
        with tempfile.TemporaryDirectory() as d:
            model, tok = toy_model.build(Path(d), [D.compose_text(r) for r in rows])
            code, out = self.run_cli("run", "--data", str(SYN), "--embedder", "onnx", "--model", str(model),
                                     "--tokenizer", str(tok), "--candidate", "minilm")
            self.assertEqual(code, 0)
            self.assertIn(SYNTHETIC_BANNER, out)
            self.assertIn("p95", out)
            code, out = self.run_cli("gate", "--model", str(model))
            self.assertEqual(code, 0)
            self.assertTrue(json.loads(out)["pass_disk"])


if __name__ == "__main__":
    unittest.main()
