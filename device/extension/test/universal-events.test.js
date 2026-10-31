// The universal browser-event contract: platform identification, activity types, adapters, the normalizer,
// and the regression matrix for the YouTube behaviour that was tested by hand (YT-001 ... YT-011).
import { test } from "node:test";
import assert from "node:assert/strict";
import { TabLogger } from "../tab-logger.js";
import { ACTIVITY_TYPES, PLATFORMS, identifyPlatform, describeActivity } from "../platforms.js";
import { BROWSER_EVENT_FIELDS, normalizeBrowserEvent } from "../event-normalizer.js";
import { validateEvent } from "./schema-check.js";

const T0 = Date.UTC(2026, 9, 27, 5, 15, 0);
const secs = (s) => T0 + s * 1000;
const RECORDING = { recording: true, noConsent: false };
const tab = (tabId, url, title = "A page") => ({ tabId, url, title, incognito: false });

function makeLogger() {
  let n = 0;
  return new TabLogger({ getStatus: async () => RECORDING, newId: () => `id-${++n}` });
}

/** Feeds a sequence of [secondsFromStart, tab|null] and returns the closed events. */
async function run(steps) {
  const logger = makeLogger();
  for (const [s, snapshot] of steps) await logger.update(snapshot, secs(s));
  return logger.events;
}

function assertContiguousAndValid(events) {
  for (const e of events) {
    assert.deepEqual(validateEvent(e), [], JSON.stringify(e));
    assert.equal(e.duration_seconds, (Date.parse(e.end) - Date.parse(e.start)) / 1000);
  }
  for (let i = 1; i < events.length; i++) assert.ok(Date.parse(events[i].start) >= Date.parse(events[i - 1].end), "no overlap");
}

test("the vocabularies are the agreed ones", () => {
  assert.deepEqual([...ACTIVITY_TYPES], ["webpage", "search", "video", "short_video", "reel", "music", "post", "article", "document", "social_feed", "messaging", "meeting", "shopping", "game", "unknown"]);
  assert.deepEqual([...PLATFORMS], ["youtube", "instagram", "tiktok", "reddit", "facebook", "whatsapp", "spotify", "netflix", "github", "google", "unknown"]);
});

test("platforms are identified from the domain alone, no adapter needed", () => {
  for (const [domain, platform] of [
    ["youtube.com", "youtube"], ["youtu.be", "youtube"], ["instagram.com", "instagram"], ["tiktok.com", "tiktok"],
    ["reddit.com", "reddit"], ["facebook.com", "facebook"], ["whatsapp.com", "whatsapp"], ["spotify.com", "spotify"],
    ["netflix.com", "netflix"], ["github.com", "github"], ["google.com", "google"], ["example.com", "unknown"], ["toString", "unknown"],
  ]) assert.equal(identifyPlatform(domain), platform, domain);
});

test("YT-001 search results page is a search, and the title is the tab title (no scraping of result cards)", async () => {
  const events = await run([[0, tab(1, "https://www.youtube.com/results?search_query=math+lec", "MATH LEC - YouTube")], [30, null]]);
  const [e] = events;
  assert.equal(e.platform, "youtube");
  assert.equal(e.activity_type, "search");
  assert.equal(e.url, "https://www.youtube.com/results");
  assert.equal(e.title, "MATH LEC - YouTube");
  assertContiguousAndValid(events);
});

test("YT-002 a normal video is a video with its tab title", async () => {
  const [e] = await run([[0, tab(1, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Lecture 1 - YouTube")], [60, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.title, e.duration_seconds], ["youtube", "video", "Lecture 1 - YouTube", 60]);
});

test("YT-003 Shorts are short_video, keep the masked url and never contain the short id", async () => {
  const [e] = await run([[0, tab(1, "https://www.youtube.com/shorts/mF2kqwe91Zc", "A short - YouTube")], [20, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.url, e.title], ["youtube", "short_video", "https://www.youtube.com/shorts/*", "A short - YouTube"]);
  assert.ok(!JSON.stringify(e).includes("mF2kqwe91Zc"));
});

test("YT-004 YouTube Music is music", async () => {
  const [e] = await run([[0, tab(1, "https://music.youtube.com/watch?v=abcdefghijk", "Attention - YouTube Music")], [20, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.domain], ["youtube", "music", "youtube.com"]);
});

test("YT-005..008 the extension event carries no media or metadata fields: only the agent may add media from Windows", async () => {
  const [e] = await run([[0, tab(1, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Song - YouTube")], [20, null]]);
  for (const f of ["media_title", "artist", "album", "media_type", "playback", "media_app", "creator", "description", "hashtags", "published_at", "language", "metadata_source", "content_context"]) assert.equal(f in e, false, f);
  assert.deepEqual([e.content_count, e.session_id], [1, null]);
});

test("YT-009/010 video A to video B, then a short, then another tab: contiguous, one event each, no overlap", async () => {
  const events = await run([
    [0, tab(1, "https://www.youtube.com/watch?v=AAAAAAAAAAA", "Video A - YouTube")],
    [30, tab(1, "https://www.youtube.com/watch?v=BBBBBBBBBBB", "Video B - YouTube")],
    [50, tab(1, "https://www.youtube.com/shorts/CCCCCCCCCCC", "Short C - YouTube")],
    [60, tab(2, "https://example.com/notes", "Notes")],
    [90, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.title, e.duration_seconds]), [
    ["video", "Video A - YouTube", 30], ["video", "Video B - YouTube", 20], ["short_video", "Short C - YouTube", 10], ["webpage", "Notes", 30],
  ]);
  assert.equal(new Set(events.map((e) => e.id)).size, 4);
  assertContiguousAndValid(events);
});

test("YT-011 / unknown website: still recorded, platform unknown, activity webpage, nothing guessed", async () => {
  const [e] = await run([[0, tab(1, "https://example.com/", "Example Domain")], [10, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.domain, e.uncertain, e.category], ["unknown", "webpage", "example.com", false, null]);
});

test("known platforms without an adapter are still identified, and fall back to webpage", async () => {
  const events = await run([
    [0, tab(1, "https://www.netflix.com/browse", "Netflix")], [10, tab(1, "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "Playlist")],
    [20, tab(1, "https://github.com/user/repo", "repo")], [30, null],
  ]);
  assert.deepEqual(events.map((e) => [e.platform, e.activity_type]), [["netflix", "webpage"], ["spotify", "webpage"], ["github", "webpage"]]);
});

test("messaging: WhatsApp Web is identified, the title is withheld, and no message data exists in the event", async () => {
  const [e] = await run([[0, tab(1, "https://web.whatsapp.com/", "(3) Alice - WhatsApp")], [15, null]]);
  assert.deepEqual([e.platform, e.title, e.duration_seconds], ["whatsapp", null, 15]);
  assert.ok(!JSON.stringify(e).includes("Alice"));
});

test("a YouTube page the adapter cannot tell apart falls back to webpage instead of guessing", () => {
  const home = describeActivity({ url: "https://www.youtube.com/", host: "www.youtube.com", domain: "youtube.com", rawTitle: "YouTube" });
  assert.deepEqual([home.platform, home.activity_type], ["youtube", "webpage"]);
});

test("a new platform needs only an adapter: no change to the logger or the normalizer", () => {
  const reddit = {
    platform: "reddit",
    identify: (domain) => domain === "reddit.com",
    getActivityType: ({ path }) => (path.startsWith("/r/") && path.includes("/comments/") ? "post" : null),
    extractTitle: (title) => (title ? title.replace(/ : .*$/, "") : title),
  };
  const page = { url: "https://www.reddit.com/r/study/comments/abc123/x", host: "www.reddit.com", domain: "reddit.com", rawTitle: "Tips : study" };
  const r = describeActivity(page, [reddit]);
  assert.deepEqual([r.platform, r.activity_type, r.rawTitle], ["reddit", "post", "Tips"]);
  assert.equal(describeActivity({ ...page, url: "https://www.reddit.com/" }, [reddit]).activity_type, "webpage");
});

test("an adapter that returns something outside the vocabulary is ignored", () => {
  const bad = { platform: "reddit", identify: () => true, getActivityType: () => "made_up" };
  assert.equal(describeActivity({ url: "https://www.reddit.com/", host: "www.reddit.com", domain: "reddit.com", rawTitle: "t" }, [bad]).activity_type, "webpage");
});

test("the normalizer produces exactly the canonical fields, in order, and no classification", () => {
  const e = normalizeBrowserEvent({ id: "x", start: "2026-10-27T05:15:00Z", end: "2026-10-27T05:15:05Z", duration_seconds: 5, domain: "example.com", url: "https://example.com/", title: null, platform: "unknown", activity_type: "webpage" });
  assert.deepEqual(Object.keys(e), [...BROWSER_EVENT_FIELDS]);
  assert.deepEqual([e.category, e.confidence, e.label_source, e.uncertain], [null, null, null, false]);
  for (const gone of ["app", "presence", "idle", "browser_idle", "switched_count"]) assert.ok(!(gone in e), `${gone} is not part of the event`);
  assert.deepEqual(validateEvent(e), []);
});
