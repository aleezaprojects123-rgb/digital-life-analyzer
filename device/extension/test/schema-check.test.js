import { test } from "node:test";
import assert from "node:assert/strict";
import { validateEvent } from "./schema-check.js";

// Proves the checker used by the other tests really catches bad events (so "valid" means something).

const good = () => ({
  schema_version: "1", id: "e1", start: "2026-10-27T05:15:00Z", end: "2026-10-27T05:16:00Z", duration_seconds: 60,
  source: "extension", app: null, domain: "youtube.com", url: "https://www.youtube.com/shorts/*", title: "Short",
  idle: false, presence: null, category: null, confidence: null, uncertain: false, label_source: null,
});

test("a good extension event is valid", () => assert.deepEqual(validateEvent(good()), []));

test("the checker rejects broken events", () => {
  const cases = {
    "missing field": (e) => { delete e.title; },
    "extra field": (e) => { e.page_text = "x"; },
    "milliseconds in time": (e) => { e.start = "2026-10-27T05:15:00.123Z"; },
    "no Z": (e) => { e.end = "2026-10-27T05:16:00"; },
    "wrong schema_version": (e) => { e.schema_version = "2"; },
    "negative duration": (e) => { e.duration_seconds = -1; },
    "fractional duration": (e) => { e.duration_seconds = 1.5; },
    "extension event with an app": (e) => { e.app = "chrome.exe"; },
    "extension event without a domain": (e) => { e.domain = null; },
    "extension event that is idle": (e) => { e.idle = true; },
    "url with a query string": (e) => { e.url = "https://www.youtube.com/watch?v=abc"; },
    "url with a long id": (e) => { e.url = "https://www.youtube.com/x/" + "a".repeat(40); },
    "url with an email": (e) => { e.url = "https://www.youtube.com/u/a@b.com"; },
    "domain with a port": (e) => { e.domain = "youtube.com:80"; },
    "uppercase domain": (e) => { e.domain = "YouTube.com"; },
    "confidence without a category": (e) => { e.confidence = 0.5; },
    "uncertain without a model label": (e) => { e.uncertain = true; },
    "agent event with a url": (e) => { e.source = "agent"; e.app = "x.exe"; e.domain = null; },
  };
  for (const [name, mutate] of Object.entries(cases)) {
    const event = good();
    mutate(event);
    assert.notDeepEqual(validateEvent(event), [], name);
  }
});
