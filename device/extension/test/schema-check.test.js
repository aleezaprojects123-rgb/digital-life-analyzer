import { test } from "node:test";
import assert from "node:assert/strict";
import { validateEvent } from "./schema-check.js";

// Proves the checker used by the other tests really catches bad events (so "valid" means something), against contracts/event.schema.json.

const good = () => ({
  schema_version: "1", id: "e1", start: "2026-10-27T05:15:00Z", end: "2026-10-27T05:16:00Z", duration_seconds: 60,
  source: "extension", platform: "youtube", domain: "youtube.com", url: "https://www.youtube.com/shorts/*", title: "Short",
  activity_type: "short_video", content_count: 1, session_id: null,
  category: null, confidence: null, uncertain: false, label_source: null,
});

test("a good extension event is valid", () => assert.deepEqual(validateEvent(good()), []));

test("an event with a session id and the agent's media fields is valid", () => {
  const e = { ...good(), session_id: "b0c1d2e3f4a5968778695a4b3c2d1e0f", media_title: "A song", artist: "A band", album: null, media_type: "music", playback: "playing", media_app: "chrome.exe" };
  assert.deepEqual(validateEvent(e), []);
});

test("the checker rejects broken events", () => {
  const cases = {
    "missing field": (e) => { delete e.title; },
    "missing platform": (e) => { delete e.platform; },
    "missing content_count": (e) => { delete e.content_count; },
    "extra field": (e) => { e.page_text = "x"; },
    "milliseconds in time": (e) => { e.start = "2026-10-27T05:15:00.123Z"; },
    "no Z": (e) => { e.end = "2026-10-27T05:16:00"; },
    "wrong schema_version": (e) => { e.schema_version = "2"; },
    "negative duration": (e) => { e.duration_seconds = -1; },
    "fractional duration": (e) => { e.duration_seconds = 1.5; },
    "a desktop event": (e) => { e.source = "agent"; },
    "a desktop application field": (e) => { e.app = "code.exe"; },
    "an idle field": (e) => { e.idle = false; },
    "a browser idle field": (e) => { e.browser_idle = false; },
    "a presence field": (e) => { e.presence = null; },
    "a switch count": (e) => { e.switched_count = 1; },
    "event without a domain": (e) => { e.domain = null; },
    "event without a url": (e) => { e.url = null; },
    "url with a query string": (e) => { e.url = "https://www.youtube.com/watch?v=abc"; },
    "url with a long id": (e) => { e.url = "https://www.youtube.com/x/" + "a".repeat(40); },
    "url with an email": (e) => { e.url = "https://www.youtube.com/u/a@b.com"; },
    "domain with a port": (e) => { e.domain = "youtube.com:80"; },
    "uppercase domain": (e) => { e.domain = "YouTube.com"; },
    "a platform that does not exist": (e) => { e.platform = "myspace"; },
    "an activity type that does not exist": (e) => { e.activity_type = "made_up"; },
    "content_count 2": (e) => { e.content_count = 2; },
    "negative content_count": (e) => { e.content_count = -1; },
    "fractional content_count": (e) => { e.content_count = 0.5; },
    "session id that is too long": (e) => { e.session_id = "x".repeat(65); },
    "confidence without a category": (e) => { e.confidence = 0.5; },
    "uncertain without a model label": (e) => { e.uncertain = true; },
  };
  for (const [name, mutate] of Object.entries(cases)) {
    const event = good();
    mutate(event);
    assert.notDeepEqual(validateEvent(event), [], name);
  }
});
