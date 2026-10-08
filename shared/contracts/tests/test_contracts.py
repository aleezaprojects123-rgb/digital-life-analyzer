"""
Contract tests for Interfaces A to D.   Run from shared/contracts:   python -m unittest discover -s tests -v

1. every schema is a valid JSON Schema draft 2020-12 with a versioned name;
2. every valid sample passes and every invalid sample fails;
3. PRIVACY: Interface A can never carry raw titles, URLs, app/site names, screenshots or typed text;
4. every OPEN QUESTION marked in a schema is listed in the README.
"""
import copy
import json
import re
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]
DRAFT_2020_12 = "https://json-schema.org/draft/2020-12/schema"
SPEC_CATEGORIES = ["Study", "Work", "Entertainment", "Social Media", "Other"]


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


SCHEMAS = {p.name: load(p) for p in sorted((ROOT / "schemas").glob("*.schema.json"))}
SAMPLES = {p.name: load(p) for p in sorted((ROOT / "samples").glob("*.json"))}
SAMPLE_NAME = re.compile(r"^(?P<base>.+\.v\d+)\.(?P<kind>valid|invalid)(\.[a-z-]+)?\.json$")


def validator(schema) -> Draft202012Validator:
    return Draft202012Validator(schema, format_checker=FormatChecker())


# ---- the privacy checker for Interface A (also tested against deliberately bad schemas) -----------------------------

FORBIDDEN_NAME = re.compile(
    r"title|url|uri|href|link|screenshot|screen|image|pixel|bitmap|keystroke|keylog|typed|typing|text|clipboard|"
    r"domain|site|host|(^|_)app(_|$)|application|process|window|exe|path|filename|caption|ocr|content|body|payload",
    re.IGNORECASE)
LOOSE_PATTERN = re.compile(r"\.\*|\.\+|\[\^|\\S|\\W")
FORBIDDEN_KEYWORDS = ("patternProperties", "unevaluatedProperties", "contentEncoding", "contentMediaType", "dependentSchemas")


def privacy_violations(schema) -> list[str]:
    """Returns every way this schema could let free text, URLs, names or binary data through."""
    problems: list[str] = []

    def walk(node, where, conditional=False):
        # `conditional` = the node is an if/then/else fragment. Properties named there cannot widen the parent object
        # (additionalProperties only looks at the parent's own `properties`), so the lock check is skipped for them,
        # but their names and strings are still checked.
        if isinstance(node, list):
            for i, n in enumerate(node):
                walk(n, f"{where}[{i}]", conditional)
            return
        if not isinstance(node, dict):
            return

        for kw in FORBIDDEN_KEYWORDS:
            if kw in node:
                problems.append(f"{where}: keyword {kw!r} not allowed")

        types = node.get("type")
        types = [types] if isinstance(types, str) else (types or [])

        if "properties" in node or "object" in types:
            if not conditional and node.get("additionalProperties") is not False:
                problems.append(f"{where}: object must set additionalProperties false")
            for name in node.get("properties", {}):
                if FORBIDDEN_NAME.search(name):
                    problems.append(f"{where}: property name {name!r} looks like raw content")
                sub = node["properties"][name]
                if not conditional and not any(k in sub for k in ("type", "const", "enum", "$ref", "oneOf", "anyOf")):
                    problems.append(f"{where}.{name}: unconstrained value")

        if "string" in types:
            pattern = node.get("pattern", "")
            bounded = (
                "enum" in node or "const" in node
                or (pattern.startswith("^") and pattern.endswith("$") and not LOOSE_PATTERN.search(pattern))
                or node.get("maxLength", 10**9) <= 64
            )
            if not bounded:
                problems.append(f"{where}: free-form string (needs enum, const, an anchored strict pattern, or maxLength <= 64)")

        if "array" in types and "maxItems" not in node:
            problems.append(f"{where}: array needs maxItems")

        for key, child in node.items():
            walk(child, f"{where}/{key}", conditional or key in ("if", "then", "else"))

    walk(schema, "#")
    return problems


# ---- tests ---------------------------------------------------------------------------------------------------------

class SchemaFileTests(unittest.TestCase):
    def test_there_is_a_schema_for_every_interface(self):
        for name in ("interface-a.v1", "interface-a-envelope.v1", "interface-b.v1", "interface-c.v1", "interface-d.v1"):
            self.assertIn(f"{name}.schema.json", SCHEMAS)

    def test_schemas_are_valid_2020_12_with_versioned_names_and_ids(self):
        for fname, schema in SCHEMAS.items():
            with self.subTest(fname):
                self.assertRegex(fname, r"^interface-[a-d](-[a-z]+)?\.v\d+\.schema\.json$")
                self.assertEqual(schema["$schema"], DRAFT_2020_12)
                self.assertEqual(schema["$id"], f"urn:dla:contracts:{fname}")
                Draft202012Validator.check_schema(schema)
                self.assertTrue(schema.get("title") and schema.get("description"))

    def test_schema_version_constant_matches_the_file_name(self):
        for fname, schema in SCHEMAS.items():
            expected = fname.removesuffix(".schema.json")
            versions = set(re.findall(r'"schema_version":\s*\{\s*"const":\s*"([^"]+)"', json.dumps(schema)))
            with self.subTest(fname):
                self.assertEqual(versions, {expected})

    def test_envelope_points_at_the_document_schema(self):
        env, doc = SCHEMAS["interface-a-envelope.v1.schema.json"], SCHEMAS["interface-a.v1.schema.json"]
        self.assertEqual(env["properties"]["document_schema"]["const"], doc["properties"]["schema_version"]["const"])


class SampleTests(unittest.TestCase):
    def test_every_interface_has_a_valid_and_an_invalid_sample(self):
        kinds = {}
        for name in SAMPLES:
            m = SAMPLE_NAME.match(name)
            self.assertIsNotNone(m, f"bad sample file name {name}")
            kinds.setdefault(m["base"], set()).add(m["kind"])
        for schema_name in SCHEMAS:
            base = schema_name.removesuffix(".schema.json")
            self.assertEqual(kinds.get(base), {"valid", "invalid"}, base)

    def test_valid_samples_pass_and_invalid_samples_fail(self):
        for name, payload in SAMPLES.items():
            m = SAMPLE_NAME.match(name)
            schema = SCHEMAS[f"{m['base']}.schema.json"]
            errors = sorted(validator(schema).iter_errors(payload), key=lambda e: list(e.path))
            with self.subTest(name):
                if m["kind"] == "valid":
                    self.assertEqual([e.message for e in errors], [])
                else:
                    self.assertTrue(errors, f"{name} is supposed to be invalid but passed")


class InterfaceARules(unittest.TestCase):
    schema = SCHEMAS["interface-a.v1.schema.json"]
    valid = SAMPLES["interface-a.v1.valid.json"]

    def test_categories_are_exactly_the_spec_categories(self):
        cats = self.schema["$defs"]["hourSummary"]["properties"]["categories"]
        self.assertEqual(sorted(cats["required"]), sorted(SPEC_CATEGORIES))
        self.assertEqual(sorted(cats["properties"]), sorted(SPEC_CATEGORIES))

    def test_confidence_is_null_exactly_when_seconds_is_zero(self):
        doc = copy.deepcopy(self.valid)
        stats = doc["hours"][0]["categories"]["Social Media"]
        self.assertEqual(stats, {"seconds": 0, "confidence": None})
        stats["confidence"] = 0.5                                   # zero seconds but a confidence
        self.assertFalse(validator(self.schema).is_valid(doc))
        doc = copy.deepcopy(self.valid)
        doc["hours"][0]["categories"]["Study"]["confidence"] = None  # time spent but no confidence
        self.assertFalse(validator(self.schema).is_valid(doc))

    def test_hour_start_must_be_on_the_hour(self):
        doc = copy.deepcopy(self.valid)
        doc["hours"][0]["hour_start"] = "2026-10-09T08:30:00Z"
        self.assertFalse(validator(self.schema).is_valid(doc))

    def test_empty_batch_is_rejected(self):
        doc = copy.deepcopy(self.valid)
        doc["hours"] = []
        self.assertFalse(validator(self.schema).is_valid(doc))

    def test_category_names_match_the_agent_database(self):
        sql = (REPO / "agent/src/Dla.Agent/Data/Migrations.cs").read_text(encoding="utf-8")
        in_db = re.search(r"category\s+TEXT\s+CHECK \(category IS NULL OR category IN \(([^)]*)\)", sql).group(1)
        self.assertEqual(re.findall(r"'([^']+)'", in_db), SPEC_CATEGORIES)


class InterfaceAPrivacy(unittest.TestCase):
    """The headline privacy guarantee: Interface A carries summaries only."""
    schema = SCHEMAS["interface-a.v1.schema.json"]
    valid = SAMPLES["interface-a.v1.valid.json"]

    def test_schema_has_no_way_to_carry_titles_urls_screenshots_or_free_text(self):
        self.assertEqual(privacy_violations(self.schema), [])

    def test_the_privacy_checker_really_catches_a_leaky_schema(self):
        cases = {
            "adds a title field": lambda s: s["$defs"]["hourSummary"]["properties"].update({"window_title": {"type": "string", "maxLength": 10}}),
            "adds a url field": lambda s: s["$defs"]["hourSummary"]["properties"].update({"page": {"type": "string"}}),
            "opens an object": lambda s: s["$defs"]["hourSummary"].update({"additionalProperties": True}),
            "removes the lock": lambda s: s["$defs"]["hourSummary"].pop("additionalProperties"),
            "allows binary data": lambda s: s["$defs"]["hourSummary"]["properties"].update({"blob": {"type": "string", "contentEncoding": "base64"}}),
            "allows any string": lambda s: s["$defs"]["uuid"].pop("pattern"),
            "loose pattern": lambda s: s["$defs"]["uuid"].update({"pattern": "^.*$"}),
            "pattern properties": lambda s: s["$defs"]["hourSummary"].update({"patternProperties": {"^x": {"type": "string"}}}),
            "unbounded array": lambda s: s["properties"]["hours"].pop("maxItems"),
            "app name field": lambda s: s["$defs"]["hourSummary"]["properties"].update({"app": {"enum": ["a"]}}),
        }
        for label, mutate in cases.items():
            with self.subTest(label):
                bad = copy.deepcopy(self.schema)
                mutate(bad)
                self.assertTrue(privacy_violations(bad), f"checker missed: {label}")

    def test_raw_content_fields_are_rejected_wherever_they_are_placed(self):
        leaky_fields = {
            "title": "Inbox - Gmail", "window_title": "Inbox - Gmail", "url": "https://example.com/a?b=1",
            "site": "example.com", "domain": "example.com", "app": "chrome.exe", "app_name": "chrome.exe",
            "process": "chrome.exe", "screenshot": "iVBORw0KGgo=", "image": "iVBORw0KGgo=", "text": "hello",
            "ocr_text": "hello", "keystrokes": "abc", "typed_text": "abc", "events": [], "raw_events": [{"app": "x"}],
        }
        v = validator(self.schema)
        for field, value in leaky_fields.items():
            def at_root(d): d[field] = value
            def at_hour(d): d["hours"][0][field] = value
            def at_categories(d): d["hours"][0]["categories"][field] = value
            def at_stats(d): d["hours"][0]["categories"]["Work"][field] = value
            for place, put in (("root", at_root), ("hour", at_hour), ("categories", at_categories), ("category stats", at_stats)):
                doc = copy.deepcopy(self.valid)
                put(doc)
                with self.subTest(f"{field} at {place}"):
                    self.assertFalse(v.is_valid(doc), f"{field!r} was accepted at {place}")

    def test_no_string_value_in_a_valid_document_can_be_free_text(self):
        # every string in the valid sample is an id, a timestamp, a schema marker or a category name
        allowed = [re.compile(p) for p in (
            r"^interface-a\.v1$", r"^[0-9a-f-]{36}$", r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")]

        def strings(node):
            if isinstance(node, str):
                yield node
            elif isinstance(node, dict):
                for v in node.values():
                    yield from strings(v)
            elif isinstance(node, list):
                for v in node:
                    yield from strings(v)

        for s in strings(self.valid):
            self.assertTrue(any(p.match(s) for p in allowed), f"unexpected string value {s!r}")

    def test_the_envelope_cannot_carry_plaintext_fields_either(self):
        env = SCHEMAS["interface-a-envelope.v1.schema.json"]
        self.assertFalse(env.get("additionalProperties"))
        self.assertEqual(sorted(env["properties"]),
                         sorted(["schema_version", "batch_id", "device_id", "created_at", "document_schema", "alg", "key_id", "nonce", "ciphertext"]))


class InterfaceDRules(unittest.TestCase):
    schema = SCHEMAS["interface-d.v1.schema.json"]

    def test_there_is_no_password_field_anywhere(self):
        self.assertNotRegex(json.dumps(self.schema["$defs"]).lower().replace("passwordless", ""), r'"[a-z_]*password[a-z_]*"\s*:\s*\{')

    def test_each_message_matches_exactly_one_variant(self):
        v = validator(self.schema)
        for name, payload in SAMPLES.items():
            if name.startswith("interface-d.v1.valid"):
                with self.subTest(name):
                    self.assertTrue(v.is_valid(payload))

    def test_the_activation_code_cannot_be_a_url_or_contain_spaces(self):
        v = validator(self.schema)
        base = copy.deepcopy(SAMPLES["interface-d.v1.valid.json"])
        for bad in ("dla://activate?code=Zk3p9QxT7vLm2WnR8aYd", "has space in it 123456789", "tooShort"):
            base["code"] = bad
            self.assertFalse(v.is_valid(base), bad)


class OpenQuestionTests(unittest.TestCase):
    readme = (ROOT / "README.md").read_text(encoding="utf-8")

    def test_every_open_question_marked_in_a_schema_is_listed_in_the_readme(self):
        ids = set(re.findall(r'"x-open-question":\s*"(OQ-[A-Z0-9-]+)"', "\n".join(json.dumps(s) for s in SCHEMAS.values())))
        self.assertTrue(ids)
        section = self.readme.split("## Open questions for Fatima", 1)[1]
        listed = set(re.findall(r"\*\*(OQ-[A-Z0-9-]+):\*\*", section))
        for oq in sorted(ids):
            self.assertIn(oq, listed, f"{oq} is marked in a schema but missing from the README list")

    def test_every_readme_open_question_has_a_unique_id(self):
        section = self.readme.split("## Open questions for Fatima", 1)[1]
        ids = re.findall(r"\*\*(OQ-[A-Z0-9-]+):\*\*", section)
        self.assertGreater(len(ids), 20)
        self.assertEqual(len(ids), len(set(ids)))


if __name__ == "__main__":
    unittest.main()
