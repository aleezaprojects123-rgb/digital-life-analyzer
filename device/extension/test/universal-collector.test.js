// The universal collector behaviour, tested over activity CLASSES rather than one website's page.
// Real sanitizer + real logger; every event is checked against the contract plus the proposed fields.
import { test } from "node:test";
import assert from "node:assert/strict";
import { TabLogger } from "../tab-logger.js";
import { contentKey } from "../content-key.js";
import { validateEvent } from "./schema-check.js";

const T0 = Date.UTC(2026, 9, 27, 5, 15, 0);
const secs = (s) => T0 + s * 1000;
const RECORDING = { recording: true, noConsent: false };
const tab = (tabId, url, title) => ({ tabId, url, title, incognito: false });

async function run(steps) {
  let n = 0;
  const logger = new TabLogger({ getStatus: async () => RECORDING, newId: () => `id-${++n}` });
  for (const [s, snapshot] of steps) await logger.update(snapshot, secs(s));
  for (const e of logger.events) assert.deepEqual(validateEvent(e), [], JSON.stringify(e));
  return logger.events;
}
const only = async (url, title) => (await run([[0, tab(1, url, title)], [10, null]]))[0];

const CLASSES = [
  // [class, url, title, platform, activity_type, expected title]
  ["normal webpage", "https://example.com/about", "About us", "unknown", "webpage", "About us"],
  ["search", "https://www.google.com/search?q=study+tips&hl=en", "study tips - Google Search", "google", "search", "study tips - Google Search"],
  ["video", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Lecture - YouTube", "youtube", "video", "Lecture - YouTube"],
  ["short video", "https://www.tiktok.com/@alice/video/7301234567890123456", 'Alice on TikTok: "study"', "tiktok", "short_video", 'Alice on TikTok: "study"'],
  ["reel", "https://www.instagram.com/reel/C8aBcDeFgHi/", 'Alice on Instagram: "Hi"', "instagram", "reel", 'Alice on Instagram: "Hi"'],
  ["post", "https://www.reddit.com/r/study/comments/1abc2de/tips_for_exams/", "Tips for exams : study", "reddit", "post", "Tips for exams : study"],
  ["article", "https://news.example.com/2026/10/exam-results", "Exam results announced", "unknown", "article", "Exam results announced"],
  ["social feed", "https://www.reddit.com/r/study/", "study", "reddit", "social_feed", "study"],
  ["messaging", "https://web.whatsapp.com/", "(3) Alice - WhatsApp", "whatsapp", "messaging", null],
  ["document", "https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789/edit", "Essay draft - Google Docs", "google", "document", null],   // the document id is masked, so the title is withheld (documents are private)
  ["meeting", "https://meet.google.com/abc-defg-hij", "Meet - abc-defg-hij", "google", "meeting", "Meet - abc-defg-hij"],
  ["unknown site", "https://totally-new-site.example.org/", "Welcome", "unknown", "webpage", "Welcome"],
];

for (const [name, url, title, platform, activity, expectedTitle] of CLASSES) {
  test(`class: ${name}`, async () => {
    const e = await only(url, title);
    assert.deepEqual([e.platform, e.activity_type, e.title], [platform, activity, expectedTitle]);
    assert.equal(e.duration_seconds, 10);
    assert.ok(!e.url.includes("?") && !e.url.includes("#"), "query and fragment are always removed");
  });
}

test("missing title: null, never invented, for every kind of page", async () => {
  for (const [, url] of CLASSES) {
    for (const missing of [undefined, null, ""]) {
      const e = await only(url, missing);
      assert.equal(e.title, null, `${url} / ${String(missing)}`);
    }
  }
});

test("SPA content change inside one tab: TikTok video A to B, Reddit post A to B, YouTube video A to B each split once", async () => {
  for (const [a, b] of [
    ["https://www.tiktok.com/@alice/video/7301234567890123456", "https://www.tiktok.com/@bob/video/7309876543210987654"],
    ["https://www.reddit.com/r/study/comments/1abc2de/one/", "https://www.reddit.com/r/study/comments/1xyz9ab/two/"],
    ["https://www.youtube.com/watch?v=AAAAAAAAAAA", "https://www.youtube.com/watch?v=BBBBBBBBBBB"],
  ]) {
    const events = await run([[0, tab(1, a, "First")], [20, tab(1, b, "Second")], [35, null]]);
    assert.deepEqual(events.map((e) => [e.title, e.duration_seconds]), [["First", 20], ["Second", 15]], a);
    assert.equal(events[0].end, events[1].start, "contiguous: no gap and no overlap");
  }
});

test("TikTok: two videos with identical masked urls are told apart only by the in-memory key, which is never stored", async () => {
  const a = "https://www.tiktok.com/@alice/video/7301234567890123456";
  const b = "https://www.tiktok.com/@alice/video/7309876543210987654";
  const events = await run([[0, tab(1, a, "One")], [12, tab(1, b, "Two")], [30, null]]);
  assert.equal(events.length, 2);
  assert.equal(events[0].url, events[1].url);
  assert.ok(!JSON.stringify(events).includes("73012345") && !JSON.stringify(events).includes("73098765"));
});

test("same content continues: title updates and repeated polls never create another event", async () => {
  for (const url of ["https://www.tiktok.com/@alice/video/7301234567890123456", "https://www.reddit.com/r/study/comments/1abc2de/one/", "https://example.com/page"]) {
    const events = await run([[0, tab(1, url, "Loading")], [2, tab(1, url, "Real title")], [10, tab(1, url, "Real title")], [25, null]]);
    assert.equal(events.length, 1, url);
    assert.deepEqual([events[0].title, events[0].duration_seconds], ["Real title", 25]);
  }
});

test("no reliable content identity: an unknown site is never split on a guess (title change, same url)", async () => {
  const events = await run([[0, tab(1, "https://app.example.com/dashboard", "Inbox (1)")], [8, tab(1, "https://app.example.com/dashboard", "Inbox (2)")], [20, null]]);
  assert.equal(events.length, 1);
  assert.equal(contentKey("https://app.example.com/dashboard"), null);
});

test("feeds and home pages have no identity, so scrolling inside them does not split", async () => {
  for (const url of ["https://www.tiktok.com/foryou", "https://www.reddit.com/", "https://www.instagram.com/", "https://www.youtube.com/"]) {
    assert.equal(contentKey(url), null, url);
    const events = await run([[0, tab(1, url, "A")], [5, tab(1, url, "B")], [10, null]]);
    assert.equal(events.length, 1, url);
  }
});

test("tab switching and navigating away close the event exactly where the next one starts", async () => {
  const events = await run([
    [0, tab(1, "https://www.reddit.com/r/study/comments/1abc2de/one/", "Post")],
    [20, tab(2, "https://www.tiktok.com/@alice/video/7301234567890123456", "Video")],
    [40, tab(1, "https://example.com/", "Example")],
    [50, tab(1, "https://example.com/next", "Next")],
    [60, null],
  ]);
  assert.deepEqual(events.map((e) => [e.activity_type, e.duration_seconds]), [["post", 20], ["short_video", 20], ["webpage", 10], ["webpage", 10]]);
  for (let i = 1; i < events.length; i++) assert.equal(events[i].start, events[i - 1].end);
});

test("privacy: query strings, fragments and tokens never reach the event; private windows and chats keep nothing", async () => {
  const e = await only("https://www.reddit.com/r/study/comments/1abc2de/one/?utm_source=share&token=SECRET#frag", "Post");
  assert.ok(!JSON.stringify(e).includes("SECRET") && !JSON.stringify(e).includes("utm_source"));

  const chat = await only("https://web.whatsapp.com/send?phone=15551234567&text=hello", "Alice: hello");
  assert.deepEqual([chat.activity_type, chat.title], ["messaging", null]);
  assert.ok(!JSON.stringify(chat).includes("hello") && !JSON.stringify(chat).includes("5551234567"));

  const logger = new TabLogger({ getStatus: async () => RECORDING });
  await logger.update({ tabId: 1, url: "https://www.tiktok.com/@alice/video/7301234567890123456", title: "x", incognito: true }, secs(0));
  await logger.update(null, secs(10));
  assert.equal(logger.events.length, 0);
});

test("content keys only exist for the right host (look-alike domains get none)", () => {
  for (const u of [
    "https://evil-tiktok.com/@alice/video/7301234567890123456", "https://www.tiktok.com.evil.test/@alice/video/7301234567890123456",
    "https://notreddit.com/r/study/comments/1abc2de/one/", "https://example.com/r/study/comments/1abc2de/one/",
  ]) assert.equal(contentKey(u), null, u);
  assert.equal(contentKey("https://www.reddit.com/r/study/comments/1abc2de/one/"), "post:1abc2de");
  assert.equal(contentKey("https://www.tiktok.com/@alice/video/7301234567890123456"), "video:7301234567890123456");
});
