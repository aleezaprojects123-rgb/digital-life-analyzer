// Instagram adapter (INSTAGRAM-001 ... 010) and a check that the adapter interface carries TikTok, Reddit and WhatsApp.
// Deterministic fixtures only: tab titles and URLs as the browser would hand them to the logger. No live Instagram.
import { test } from "node:test";
import assert from "node:assert/strict";
import { TabLogger } from "../tab-logger.js";
import { describeActivity } from "../platforms.js";
import { instagramAdapter } from "../adapters/instagram.js";
import { contentKey, isPublicVideoPage } from "../content-key.js";
import { validateEvent } from "./schema-check.js";

const T0 = Date.UTC(2026, 9, 27, 5, 15, 0);
const secs = (s) => T0 + s * 1000;
const RECORDING = { recording: true, noConsent: false };
const tab = (tabId, url, title) => ({ tabId, url, title, incognito: false });

async function run(steps) {
  let n = 0;
  const logger = new TabLogger({ getStatus: async () => RECORDING, newId: () => `id-${++n}` });
  for (const [s, snapshot] of steps) await logger.update(snapshot, secs(s));
  return logger.events;
}

const REEL_A = "https://www.instagram.com/reel/C8aBcDeFgHi/";
const REEL_B = "https://www.instagram.com/reel/D1xYzWvUtSr/";
const TITLE_A = 'Alice on Instagram: "Study with me"';
const TITLE_B = 'Bob on Instagram: "Chemistry tips"';

test("INSTAGRAM-001 the Instagram home page is a social feed (time measured, nothing counted)", async () => {
  const [e] = await run([[0, tab(1, "https://www.instagram.com/", "Instagram")], [10, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.title, e.content_count], ["instagram", "social_feed", "Instagram", 0]);
});

test("INSTAGRAM-002/003 an individual Reel is a reel on the instagram platform (all three path shapes)", async () => {
  for (const url of [REEL_A, "https://www.instagram.com/reels/C8aBcDeFgHi/", "https://www.instagram.com/alice/reel/C8aBcDeFgHi/"]) {
    const [e] = await run([[0, tab(1, url, TITLE_A)], [10, null]]);
    assert.deepEqual([e.platform, e.activity_type, e.domain], ["instagram", "reel", "instagram.com"], url);
    assert.deepEqual(validateEvent(e), [], url);
  }
});

test("INSTAGRAM-004 when the browser exposes a content title it is kept (the creator is part of it), and the Reel code is not stored", async () => {
  const [e] = await run([[0, tab(1, REEL_A, TITLE_A)], [20, null]]);
  assert.equal(e.title, TITLE_A);
  assert.ok(!JSON.stringify(e).includes("C8aBcDeFgHi"), "the shortcode is only an in-memory change key");
  assert.equal(e.duration_seconds, 20);
});

test("INSTAGRAM-005 no content title means null, never an invented one", async () => {
  for (const title of ["Instagram", "(3) Instagram", "Instagram • Reels", undefined, null, ""]) {
    const [e] = await run([[0, tab(1, REEL_A, title)], [10, null]]);
    assert.equal(e.activity_type, "reel");
    assert.equal(e.title, null, String(title));
  }
});

test("INSTAGRAM-006 other Instagram pages fall back safely", async () => {
  for (const [url, title, type] of [
    ["https://www.instagram.com/explore/", "Explore", "webpage"],
    ["https://www.instagram.com/alice/", "Alice (@alice) • Instagram photos and videos", "webpage"],
    ["https://www.instagram.com/reels/", "Instagram", "webpage"],                       // the Reels feed itself is not one Reel
    ["https://www.instagram.com/stories/alice/", "Instagram", "webpage"],
    ["https://www.instagram.com/p/C8aBcDeFgHi/", 'Alice on Instagram: "A photo"', "post"],
  ]) {
    const [e] = await run([[0, tab(1, url, title)], [5, null]]);
    assert.equal(e.platform, "instagram");
    assert.equal(e.activity_type, type, url);
  }
});

test("Instagram chats are messaging with no title and no message data", async () => {
  const [e] = await run([[0, tab(1, "https://www.instagram.com/direct/t/340282366841710300/", "Alice • Instagram Direct")], [30, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.title], ["instagram", "messaging", null]);
  assert.ok(!JSON.stringify(e).includes("Alice"));
});

test("INSTAGRAM-007 Reel A to Reel B (same tab, same masked url) closes A and starts B", async () => {
  const events = await run([
    [0, tab(1, REEL_A, TITLE_A)],
    [20, tab(1, REEL_B, TITLE_B)],
    [45, tab(2, "https://chatgpt.com/", "ChatGPT")],
    [60, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.title, e.start.slice(11), e.duration_seconds]), [
    ["reel", TITLE_A, "05:15:00Z", 20], ["reel", TITLE_B, "05:15:20Z", 25], ["webpage", "ChatGPT", "05:15:45Z", 15],
  ]);
  assert.equal(events[0].url, events[1].url, "the stored urls are identical (the code is masked): only the in-memory key tells them apart");
  assert.equal(new Set(events.map((e) => e.id)).size, 3);
});

test("INSTAGRAM-007b after in-app navigation the tab still shows the old Reel's title: it is not trusted for the new Reel", async () => {
  const events = await run([
    [0, tab(1, REEL_A, TITLE_A)],
    [20, tab(1, REEL_B, TITLE_A)],        // url changed, title not updated yet
    [22, tab(1, REEL_B, TITLE_B)],        // title catches up: same Reel, same event
    [40, null],
  ]);
  assert.deepEqual(events.map((e) => [e.title, e.duration_seconds]), [[TITLE_A, 20], [TITLE_B, 20]]);
});

test("INSTAGRAM-008 staying on the same Reel (title or poll updates) never creates a second event", async () => {
  const events = await run([
    [0, tab(1, REEL_A, "Instagram")],
    [3, tab(1, REEL_A, TITLE_A)],
    [9, tab(1, REEL_A, TITLE_A)],
    [15, tab(1, REEL_A, TITLE_A)],
    [30, null],
  ]);
  assert.equal(events.length, 1);
  assert.deepEqual([events[0].title, events[0].duration_seconds], [TITLE_A, 30]);
});

test("INSTAGRAM-009 an unknown website still works", async () => {
  const [e] = await run([[0, tab(1, "https://example.com/", "Example Domain")], [10, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.title], ["unknown", "webpage", "Example Domain"]);
});

test("change keys exist only in memory: Reel and post codes give keys, other Instagram pages do not", () => {
  assert.equal(contentKey(REEL_A), "reel:C8aBcDeFgHi");
  assert.notEqual(contentKey(REEL_A), contentKey(REEL_B));
  assert.equal(contentKey("https://www.instagram.com/p/C8aBcDeFgHi/"), "post:C8aBcDeFgHi");
  assert.equal(contentKey("https://www.instagram.com/alice/reel/C8aBcDeFgHi/"), "reel:C8aBcDeFgHi");
  for (const u of ["https://www.instagram.com/", "https://www.instagram.com/reels/", "https://www.instagram.com/direct/t/123456789/", "https://evil-instagram.com/reel/C8aBcDeFgHi/", "https://www.instagram.com.evil.test/reel/C8aBcDeFgHi/"])
    assert.equal(contentKey(u), null, u);
  assert.equal(isPublicVideoPage(REEL_A), true);
  assert.equal(isPublicVideoPage("https://www.instagram.com/direct/t/123456789/"), false);
});

test("the adapter answers the three adapter questions on its own", () => {
  assert.equal(instagramAdapter.identify("instagram.com"), true);
  assert.equal(instagramAdapter.identify("example.com"), false);
  assert.equal(instagramAdapter.getActivityType({ host: "www.instagram.com", path: "/reel/*/" }), "reel");
  assert.equal(instagramAdapter.getActivityType({ host: "www.instagram.com", path: "/" }), "social_feed");
  assert.equal(instagramAdapter.getActivityType({ host: "www.instagram.com", path: "/explore/" }), null);
  assert.equal(instagramAdapter.extractTitle(TITLE_A, { path: "/reel/*/" }), TITLE_A);
});

test("the adapter interface already fits TikTok, Reddit and WhatsApp Web (nothing in the logger or normalizer changes)", async () => {
  const tiktok = { platform: "tiktok", identify: (d) => d === "tiktok.com", getActivityType: ({ path }) => (/^\/@[^/]+\/video\/[^/]+/.test(path) ? "short_video" : null) };
  const reddit = {
    platform: "reddit", identify: (d) => d === "reddit.com",
    getActivityType: ({ path }) => (/^\/r\/[^/]+\/comments\//.test(path) ? "post" : null),
    extractTitle: (t) => (t ? t.replace(/ : [^:]+$/, "") : null),
  };
  const whatsapp = { platform: "whatsapp", identify: (d) => d === "whatsapp.com", getActivityType: () => "messaging", extractTitle: () => null };
  const adapters = [tiktok, reddit, whatsapp];
  const at = (url, domain, rawTitle) => describeActivity({ url, host: new URL(url).hostname, domain, rawTitle }, adapters);

  assert.deepEqual(pick(at("https://www.tiktok.com/@alice/video/*", "tiktok.com", "clip")), ["tiktok", "short_video", "clip"]);
  assert.deepEqual(pick(at("https://www.reddit.com/r/study/comments/*/tips/", "reddit.com", "Tips : study")), ["reddit", "post", "Tips"]);
  assert.deepEqual(pick(at("https://web.whatsapp.com/", "whatsapp.com", "(2) Alice")), ["whatsapp", "messaging", null]);
  assert.deepEqual(pick(at("https://example.com/", "example.com", "Example")), ["unknown", "webpage", "Example"]);
});

const pick = (r) => [r.platform, r.activity_type, r.rawTitle];
