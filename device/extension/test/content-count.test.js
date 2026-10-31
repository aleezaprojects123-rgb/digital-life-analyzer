// Behaviour, not content: how many content items (Reels, videos, posts) were consumed, for how long, and when.
// content_count is 1 on the event where the user moved to a NEW item, 0 on every other event (tab switch, lost focus, re-render).
// A future screen computes SUM(content_count) and SUM(duration_seconds), grouped by platform / activity_type / day / session.
import { test } from "node:test";
import assert from "node:assert/strict";
import { TabLogger } from "../tab-logger.js";
import { facebookAdapter } from "../adapters/facebook.js";
import { contentKey } from "../content-key.js";
import { validateEvent } from "./schema-check.js";

const T0 = Date.UTC(2026, 9, 27, 5, 15, 0);
const secs = (s) => T0 + s * 1000;
const RECORDING = { recording: true, noConsent: false };
const tab = (tabId, url, title = "Instagram") => ({ tabId, url, title, incognito: false });

const REEL = (code) => `https://www.instagram.com/reel/${code}/`;
const A = "C8aBcDeFgHi";
const B = "D1xYzWvUtSr";
const C = "E5mNoPqRsTu";
const YT = (id) => `https://www.youtube.com/watch?v=${id}`;

async function run(steps, options = {}) {
  let n = 0;
  const logger = new TabLogger({ getStatus: async () => options.status ?? RECORDING, newId: () => `id-${++n}`, ...options.logger });
  for (const [s, snapshot, extra] of steps) await logger.update(snapshot, secs(s), extra);
  for (const e of logger.events) assert.deepEqual(validateEvent(e), [], JSON.stringify(e));
  return logger.events;
}
const total = (events, platform, type) => events.filter((e) => e.platform === platform && (!type || e.activity_type === type)).reduce((sum, e) => ({ items: sum.items + e.content_count, seconds: sum.seconds + e.duration_seconds }), { items: 0, seconds: 0 });

test("one Reel is one counted item with its own start, end and duration", async () => {
  const [e] = await run([[0, tab(1, REEL(A))], [12, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.content_count, e.duration_seconds, e.start, e.end], ["instagram", "reel", 1, 12, "2026-10-27T05:15:00Z", "2026-10-27T05:15:12Z"]);
});

test("the same Reel staying on screen is not counted again (repeated updates, title changes, re-renders)", async () => {
  const events = await run([
    [0, tab(1, REEL(A), "Instagram")], [2, tab(1, REEL(A), "(1) Instagram")], [5, tab(1, REEL(A), "Instagram")],
    [6, tab(1, `${REEL(A)}?igsh=xyz#t`, "Instagram")], [9, tab(1, REEL(A), "Instagram")], [20, null],
  ]);
  assert.equal(events.length, 1);
  assert.deepEqual([events[0].content_count, events[0].duration_seconds], [1, 20]);
});

test("Reel A, then B, then C: three events, three items, contiguous, durations and times add up", async () => {
  const events = await run([[0, tab(1, REEL(A))], [8, tab(1, REEL(B))], [20, tab(1, REEL(C))], [25, null]]);
  assert.deepEqual(events.map((e) => [e.content_count, e.duration_seconds, e.start.slice(11), e.end.slice(11)]), [
    [1, 8, "05:15:00Z", "05:15:08Z"], [1, 12, "05:15:08Z", "05:15:20Z"], [1, 5, "05:15:20Z", "05:15:25Z"],
  ]);
  assert.deepEqual(total(events, "instagram", "reel"), { items: 3, seconds: 25 });
  assert.ok(!JSON.stringify(events).includes(A) && !JSON.stringify(events).includes(B), "no Reel code is stored");
});

test("switching to another tab or app and coming back to the same Reel does not add a count", async () => {
  const events = await run([
    [0, tab(1, REEL(A))], [10, tab(2, "https://code.visualstudio.com/", "VS Code docs")], [40, tab(1, REEL(A))], [50, tab(1, REEL(B))], [60, null],
  ]);
  assert.deepEqual(events.map((e) => [e.platform, e.content_count]), [["instagram", 1], ["unknown", 0], ["instagram", 0], ["instagram", 1]]);
  assert.deepEqual(total(events, "instagram", "reel"), { items: 2, seconds: 10 + 10 + 10 });
});

test("a lost focus or a gap in time, then the same Reel again, is not a new item", async () => {
  const events = await run([[0, tab(1, REEL(A))], [10, null, { focusLoss: true }], [300, tab(1, REEL(A))], [320, null, { focusLoss: true }]]);
  assert.deepEqual(events.map((e) => [e.content_count, e.duration_seconds]), [[1, 10], [0, 20]]);
  assert.deepEqual(total(events, "instagram"), { items: 1, seconds: 30 });
});

test("watching the same Reel again after another one counts again (swiping back)", async () => {
  const events = await run([[0, tab(1, REEL(A))], [5, tab(1, REEL(B))], [9, tab(1, REEL(A))], [15, null]]);
  assert.deepEqual(events.map((e) => e.content_count), [1, 1, 1]);
});

test("each tab keeps its own memory of what it showed", async () => {
  const events = await run([[0, tab(1, REEL(A))], [5, tab(2, REEL(A))], [9, tab(1, REEL(A))], [12, null]]);
  assert.deepEqual(events.map((e) => e.content_count), [1, 1, 0]);
});

test("no item address means no count and no guess: the Instagram home and bare /reels/ are activity, not items", async () => {
  for (const url of ["https://www.instagram.com/", "https://www.instagram.com/reels/", "https://www.instagram.com/explore/"]) {
    const events = await run([[0, tab(1, url)], [6, tab(1, url, "Instagram (2)")], [20, null]]);
    assert.equal(events.length, 1, url);
    assert.deepEqual([events[0].content_count, events[0].duration_seconds], [0, 20], url);
  }
});

test("YouTube is unchanged: each video and each Short is one item, a return to the same video is not", async () => {
  const events = await run([
    [0, tab(1, YT("dQw4w9WgXcQ"), "Lecture - YouTube")], [30, tab(1, "https://www.youtube.com/shorts/mF2kqwe91Zc", "Short - YouTube")],
    [40, tab(1, YT("AAAAAAAAAAA"), "Video B - YouTube")], [70, tab(2, "https://example.com/", "Example")], [80, tab(1, YT("AAAAAAAAAAA"), "Video B - YouTube")], [90, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [
    ["video", 1, 30], ["short_video", 1, 10], ["video", 1, 30], ["webpage", 0, 10], ["video", 0, 10],
  ]);
  assert.equal(events[0].title, "Lecture - YouTube", "titles on YouTube are unchanged");
});

test("YouTube search pages and generic websites are recorded as before and never counted", async () => {
  const events = await run([
    [0, tab(1, "https://www.youtube.com/results?search_query=math", "math - YouTube")], [10, tab(1, "https://example.com/", "Example Domain")], [20, tab(1, "https://news.example.com/2026/10/story", "Story")], [30, null],
  ]);
  assert.deepEqual(events.map((e) => [e.platform, e.activity_type, e.content_count]), [["youtube", "search", 0], ["unknown", "webpage", 0], ["unknown", "article", 0]]);
});

test("events carry no caption, title, creator, hashtag or audio data of the content: only behaviour", async () => {
  const [e] = await run([[0, tab(1, REEL(A), 'Alice on Instagram: "private caption #secret"')], [9, null]]);
  for (const gone of ["content_context", "creator", "description", "hashtags", "published_at", "language", "metadata_source", "media_title", "artist", "album", "audio"]) assert.equal(gone in e, false, gone);
  assert.deepEqual(Object.keys(e).filter((k) => /context|caption|creator|hashtag|audio/i.test(k)), []);
});

// ---- Facebook: high-level activity from the address only, no titles --------------------------------------------------------

const FB = "https://www.facebook.com";

test("Facebook activity types come from the address only", async () => {
  for (const [url, type] of [
    [`${FB}/`, "social_feed"], [`${FB}/reel/1234567890123/`, "reel"], [`${FB}/reel/`, "reel"], [`${FB}/watch/?v=1234567890123`, "video"],
    [`${FB}/somepage/videos/1234567890123/`, "video"], [`${FB}/someuser/posts/pfbid0abcdefghijk`, "post"], [`${FB}/groups/123456/posts/987654321/`, "post"],
    [`${FB}/permalink.php?story_fbid=987654321&id=1`, "post"], [`${FB}/marketplace/`, "shopping"], [`${FB}/messages/t/123/`, "messaging"], [`${FB}/someuser`, "webpage"],
  ]) {
    const [e] = await run([[0, tab(1, url, "Facebook")], [5, null]]);
    assert.equal(e.platform, "facebook", url);
    assert.equal(e.activity_type, type, url);
    assert.equal(e.title, null, `${url}: the Facebook tab title is never kept`);
  }
});

test("Facebook: moving between Reels and videos counts items; the feed and a re-render do not", async () => {
  const events = await run([
    [0, tab(1, `${FB}/`, "Facebook")], [10, tab(1, `${FB}/reel/1111111111111/`)], [18, tab(1, `${FB}/reel/1111111111111/`, "(3) Facebook")],
    [25, tab(1, `${FB}/reel/2222222222222/`)], [33, tab(1, `${FB}/watch/?v=3333333333333`)], [40, tab(1, `${FB}/watch/?v=3333333333333&t=15`)], [50, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [
    ["social_feed", 0, 10], ["reel", 1, 15], ["reel", 1, 8], ["video", 1, 17],
  ]);
  assert.deepEqual(total(events, "facebook", "reel"), { items: 2, seconds: 23 });
  assert.ok(!JSON.stringify(events).includes("1111111111111"));
});

test("Facebook content keys: only addresses that name an item have one", () => {
  const key = (u) => contentKey(u);
  assert.equal(key(`${FB}/reel/1234567890123/`), "reel:1234567890123");
  assert.equal(key(`${FB}/watch/?v=1234567890123`), "video:1234567890123");
  assert.equal(key(`${FB}/page/videos/1234567890123/`), "video:1234567890123");
  assert.equal(key(`${FB}/user/posts/987654321`), "post:987654321");
  for (const u of [`${FB}/`, `${FB}/reel/`, `${FB}/watch/`, `${FB}/someuser`, "https://evil-facebook.com/reel/1234567890123/", "https://www.facebook.com.evil.test/reel/1234567890123/"]) assert.equal(key(u), null, u);
  assert.equal(facebookAdapter.getActivityType({ host: "www.facebook.com", path: "/reel/*" }), "reel");
});

// ---- consent, pause and sessions ---------------------------------------------------------------------------------------------

test("pausing drops the unfinished period and gives its count back, so the Reel is still counted when recording resumes", async () => {
  const status = { recording: true, noConsent: false };
  let n = 0;
  const logger = new TabLogger({ getStatus: async () => status, newId: () => `id-${++n}` });
  await logger.update(tab(1, REEL(A)), secs(0));
  status.recording = false;
  await logger.update(tab(1, REEL(A)), secs(5));      // paused: the open period is thrown away
  status.recording = true;
  await logger.update(tab(1, REEL(A)), secs(10));
  await logger.update(null, secs(20));
  assert.deepEqual(logger.events.map((e) => [e.content_count, e.duration_seconds]), [[1, 10]]);
});

test("no consent keeps nothing, and a withdrawn consent forgets what was counted", async () => {
  const status = { recording: true, noConsent: false };
  const logger = new TabLogger({ getStatus: async () => status, newId: () => "x" });
  await logger.update(tab(1, REEL(A)), secs(0));
  await logger.update(null, secs(5));
  status.recording = false; status.noConsent = true;
  await logger.update(null, secs(6));
  assert.equal(logger.events.length, 0);
  status.recording = true; status.noConsent = false;
  await logger.update(tab(1, REEL(A)), secs(10));
  await logger.update(null, secs(15));
  assert.equal(logger.events[0].content_count, 1);
});

test("the extension never invents a session id: it is null here and stamped by the local agent", async () => {
  const events = await run([[0, tab(1, REEL(A))], [8, tab(1, REEL(B))], [12, null]]);
  assert.deepEqual(events.map((e) => e.session_id), [null, null]);
});

test("diagnostics say whether a new item was counted, with no content", async () => {
  const lines = [];
  await run([[0, tab(1, REEL(A))], [8, tab(1, REEL(B))], [12, null]], { logger: { diag: (l) => lines.push(l) } });
  assert.equal(lines.length, 2);
  assert.match(lines[0], /^new period platform=instagram type=reel new-content-item=pending changed-because=first$/);
  assert.match(lines[1], /new-content-item=yes changed-because=content$/);
});

// ---- returning to an Instagram tab: the page first shows the Reel that was left, then moves on ---------------------------------

const OLD = "DeT_HthytfP";   // the Reel that was being watched before leaving the tab
const NEW = "C8aBcDeFgHi";   // the Reel the viewer actually moves to
const OTHER_TAB = (s) => [s, tab(2, "https://chatgpt.com/", "ChatGPT")];

test("arrival transient: the old Reel shown for 1 second after returning is not a new Reel; the Reel after it is", async () => {
  // (as after an extension reload or a fresh tab: nothing remembered, the page comes up on the old Reel)
  const events = await run([[0, tab(1, REEL(OLD))], [1, tab(1, REEL(NEW))], [27, null]]);
  assert.deepEqual(events.map((e) => [e.content_count, e.duration_seconds]), [[0, 1], [1, 26]]);
  assert.deepEqual(total(events, "instagram", "reel"), { items: 1, seconds: 27 });
});

test("arrival transient of 2 seconds (the 06:39 case) behaves the same", async () => {
  const events = await run([[0, tab(1, REEL(NEW))], [2, tab(1, REEL(OLD))], [33, null]]);
  assert.deepEqual(events.map((e) => [e.content_count, e.duration_seconds]), [[0, 2], [1, 31]]);
});

test("a Reel that is stayed on after arriving is counted, and the next swipe is counted too", async () => {
  const events = await run([[0, tab(1, REEL(OLD))], [10, tab(1, REEL(NEW))], [25, null]]);
  assert.deepEqual(events.map((e) => e.content_count), [1, 1]);
});

test("the settle time is exactly 3 seconds: 3 s counts, 2 s does not", async () => {
  assert.deepEqual((await run([[0, tab(1, REEL(OLD))], [3, null]])).map((e) => e.content_count), [1]);
  assert.deepEqual((await run([[0, tab(1, REEL(OLD))], [2, null]])).map((e) => e.content_count), [0]);
});

test("a genuine swipe inside the tab is never delayed: a quick flick through Reels still counts every one", async () => {
  const events = await run([[0, tab(1, REEL(OLD))], [10, tab(1, REEL(NEW))], [11, tab(1, REEL("D1xYzWvUtSr"))], [12, tab(1, REEL("E5mNoPqRsTu"))], [20, null]]);
  assert.deepEqual(events.map((e) => [e.content_count, e.duration_seconds]), [[1, 10], [1, 1], [1, 1], [1, 8]]);
});

test("coming back from another tab to a Reel already counted in this tab: the old Reel is 0, the swipe after it is 1", async () => {
  const events = await run([[0, tab(1, REEL(OLD))], [10, ...OTHER_TAB(0).slice(1)], [40, tab(1, REEL(OLD))], [41, tab(1, REEL(NEW))], [60, null]]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [["reel", 1, 10], ["webpage", 0, 30], ["reel", 0, 1], ["reel", 1, 19]]);
});

test("arriving on a Reel and leaving within 2 seconds counts nothing (under-count rather than guess)", async () => {
  const events = await run([[0, tab(1, REEL(OLD))], [1, tab(2, "https://example.com/", "Example")], [20, null]]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count]), [["reel", 0], ["webpage", 0]]);
});

test("the settle rule gives nothing back and takes nothing when recording is paused in the middle of a provisional period", async () => {
  const status = { recording: true, noConsent: false };
  let n = 0;
  const logger = new TabLogger({ getStatus: async () => status, newId: () => `id-${++n}` });
  await logger.update(tab(1, REEL(OLD)), secs(0));
  status.recording = false;
  await logger.update(tab(1, REEL(OLD)), secs(1));        // paused: the unfinished period is thrown away
  status.recording = true;
  await logger.update(tab(1, REEL(OLD)), secs(5));
  await logger.update(null, secs(20));
  assert.deepEqual(logger.events.map((e) => [e.content_count, e.duration_seconds]), [[1, 15]]);
});

test("only Instagram settles on arrival: YouTube, Facebook, TikTok, Reddit and generic sites count a new item at once", async () => {
  for (const url of [YT("dQw4w9WgXcQ"), "https://www.facebook.com/reel/1234567890123/", "https://www.tiktok.com/@alice/video/7301234567890123456", "https://www.reddit.com/r/study/comments/1abc2de/one/", "https://www.youtube.com/shorts/mF2kqwe91Zc"]) {
    const [e] = await run([[0, tab(1, url, "x")], [1, null]]);
    assert.equal(e.content_count, 1, url);
  }
  const [generic] = await run([[0, tab(1, "https://example.com/", "x")], [1, null]]);
  assert.equal(generic.content_count, 0);
});

// ---- privacy: an Instagram Reel/post code is ALWAYS masked in the stored address -----------------------------------------------

test("Instagram codes are always masked in the stored address, whatever they look like", async () => {
  for (const [raw, stored] of [
    ["https://www.instagram.com/reels/DeT_HthytfP/", "https://www.instagram.com/reels/*"],
    ["https://www.instagram.com/reels/DfH3-_0s1uj", "https://www.instagram.com/reels/*"],
    ["https://www.instagram.com/reel/abc-def_ghi/", "https://www.instagram.com/reel/*"],
    ["https://www.instagram.com/alice/reel/DeT_HthytfP/", "https://www.instagram.com/alice/reel/*"],
    ["https://www.instagram.com/p/Cx-yz_12abc/", "https://www.instagram.com/p/*"],
    ["https://www.instagram.com/tv/Cx-yz_12abc/", "https://www.instagram.com/tv/*"],
    ["https://www.instagram.com/reels/3712345678901234567/", "https://www.instagram.com/reels/*"],
    ["https://m.instagram.com/reels/DeT_HthytfP/?igsh=x#y", "https://m.instagram.com/reels/*"],
  ]) {
    const [e] = await run([[0, tab(1, raw, "Instagram")], [5, null]]);
    assert.equal(e.url, stored, raw);
    assert.ok(!JSON.stringify(e).includes("DeT_HthytfP") && !JSON.stringify(e).includes("DfH3-_0s1uj") && !JSON.stringify(e).includes("Cx-yz_12abc"), raw);
  }
});

test("masking the stored address does not stop the in-memory key from telling Reels apart", async () => {
  const events = await run([[0, tab(1, REEL("DeT_HthytfP"))], [8, tab(1, REEL("DfH3-_0s1uj"))], [20, null]]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count, e.url]), [["reel", 1, "https://www.instagram.com/reel/*"], ["reel", 1, "https://www.instagram.com/reel/*"]]);
});

test("the Instagram masking rule applies to Instagram only", async () => {
  const [other] = await run([[0, tab(1, "https://example.com/reels/DeT_HthytfP", "x")], [5, null]]);
  assert.equal(other.url, "https://example.com/reels/DeT_HthytfP");
  const [explore] = await run([[0, tab(1, "https://www.instagram.com/explore/tags/study/", "Instagram")], [5, null]]);
  assert.equal(explore.url, "https://www.instagram.com/explore/tags/study");
  const [profile] = await run([[0, tab(1, "https://www.instagram.com/imshaha.1/", "Instagram")], [5, null]]);
  assert.equal(profile.url, "https://www.instagram.com/imshaha.1");
});

// ---- Instagram home feed: classified and timed, never counted ------------------------------------------------------------------

import { readFileSync } from "node:fs";
import { BROWSER_EVENT_FIELDS } from "../event-normalizer.js";

test("the Instagram home feed is a social_feed with content_count 0, and its time is still measured", async () => {
  const [e] = await run([[0, tab(1, "https://www.instagram.com/", "Instagram")], [47, null]]);
  assert.deepEqual([e.platform, e.activity_type, e.content_count, e.duration_seconds, e.domain], ["instagram", "social_feed", 0, 47, "instagram.com"]);
  assert.equal(e.url, "https://www.instagram.com");
});

test("scrolling the feed (notification count in the title, repeated updates) stays one feed event and is never counted", async () => {
  const events = await run([
    [0, tab(1, "https://www.instagram.com/", "Instagram")], [10, tab(1, "https://www.instagram.com/", "(1) Instagram")], [30, tab(1, "https://www.instagram.com/", "Instagram")], [60, null],
  ]);
  assert.equal(events.length, 1);
  assert.deepEqual([events[0].activity_type, events[0].content_count, events[0].duration_seconds], ["social_feed", 0, 60]);
});

test("the feed event carries no Post id, caption, username or any other content, and nothing that identifies a Post", async () => {
  const [e] = await run([[0, tab(1, "https://www.instagram.com/", "Instagram")], [20, null]]);
  assert.deepEqual(Object.keys(e), [...BROWSER_EVENT_FIELDS], "exactly the universal event shape, no extra fields");
  const json = JSON.stringify(e);
  for (const forbidden of ["caption", "creator", "username", "hashtag", "audio", "post_count", "feed_post", "post_id", "image", "alt"]) assert.ok(!json.includes(forbidden), forbidden);
  assert.deepEqual(Object.keys(e).filter((k) => /post|caption|creator|user|tag|audio|image|media|context/i.test(k)), []);
});

test("coming back to the feed and leaving it counts nothing, however often", async () => {
  const events = await run([
    [0, tab(1, "https://www.instagram.com/", "Instagram")], [10, tab(2, "https://example.com/", "Example")], [20, tab(1, "https://www.instagram.com/", "Instagram")], [40, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count]), [["social_feed", 0], ["webpage", 0], ["social_feed", 0]]);
});

test("a Post opened on its own address is still a post and still counted: once, not again on return, and again for another Post", async () => {
  const P = (code) => `https://www.instagram.com/p/${code}/`;
  const events = await run([
    [0, tab(1, P("Cx-yz_12abc"))], [10, tab(2, "https://example.com/", "Example")], [20, tab(1, P("Cx-yz_12abc"))], [30, tab(1, P("Dq9mNpR4tZk"))], [45, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [["post", 1, 10], ["webpage", 0, 10], ["post", 0, 10], ["post", 1, 15]]);
  assert.ok(events.filter((e) => e.activity_type === "post").every((e) => e.url.endsWith("/p/*")), "the Post code is masked in the stored address");
  assert.ok(!JSON.stringify(events).includes("Cx-yz_12abc") && !JSON.stringify(events).includes("Dq9mNpR4tZk"));
});

test("feed to Post to feed: the feed is time only, the opened Post is counted", async () => {
  const events = await run([
    [0, tab(1, "https://www.instagram.com/", "Instagram")], [15, tab(1, "https://www.instagram.com/p/Cx-yz_12abc/")], [25, tab(1, "https://www.instagram.com/", "Instagram")], [40, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [["social_feed", 0, 15], ["post", 1, 10], ["social_feed", 0, 15]]);
});

test("Instagram Reels are unchanged by the feed classification (bare /reels/ is not the home feed, a Reel is still counted)", async () => {
  const bare = await run([[0, tab(1, "https://www.instagram.com/reels/", "Instagram")], [10, null]]);
  assert.deepEqual([bare[0].activity_type, bare[0].content_count], ["webpage", 0]);
  const reels = await run([[0, tab(1, REEL(A))], [10, tab(1, REEL(B))], [20, null]]);
  assert.deepEqual(reels.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [["reel", 1, 10], ["reel", 1, 10]]);
  const [explore] = await run([[0, tab(1, "https://www.instagram.com/explore/", "Instagram")], [5, null]]);
  assert.deepEqual([explore.activity_type, explore.content_count], ["webpage", 0]);
});

test("other platforms keep their classification: YouTube home, Facebook feed, TikTok and Reddit feeds, a generic home page", async () => {
  const types = async (url) => (await run([[0, tab(1, url, "x")], [5, null]]))[0];
  assert.deepEqual([(await types("https://www.youtube.com/")).activity_type, (await types("https://www.youtube.com/")).content_count], ["webpage", 0]);
  assert.equal((await types("https://www.facebook.com/")).activity_type, "social_feed");
  assert.equal((await types("https://www.tiktok.com/foryou")).activity_type, "social_feed");
  assert.equal((await types("https://www.reddit.com/")).activity_type, "social_feed");
  assert.equal((await types("https://example.com/")).activity_type, "webpage");
});

test("no content script: the manifest asks only for tabs and the local agent", () => {
  const manifest = JSON.parse(readFileSync(new URL("../manifest.json", import.meta.url), "utf8"));
  assert.deepEqual(manifest.permissions, ["tabs"]);
  assert.deepEqual(manifest.host_permissions, ["http://127.0.0.1:49721/*"]);
  for (const absent of ["content_scripts", "optional_permissions", "optional_host_permissions", "web_accessible_resources", "externally_connectable"]) assert.equal(absent in manifest, false, absent);
});
