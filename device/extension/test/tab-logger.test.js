// The extension's event code run against the real sanitizer, with every produced event checked
// against contracts/event.schema.json.
import { test } from "node:test";
import assert from "node:assert/strict";
import { TabLogger, makeTitle, toUtcSeconds, MAX_TITLE_CHARS } from "../tab-logger.js";
import { getDomain } from "../url-sanitizer.js";
import { SENSITIVE_TITLE_HOSTS } from "../sensitive-domains.js";
import { validateEvent } from "./schema-check.js";

const T0 = Date.UTC(2026, 9, 27, 5, 15, 0); // 2026-10-27T05:15:00Z
const RECORDING = { recording: true, noConsent: false };
const PAUSED = { recording: false, noConsent: false };
const NO_CONSENT = { recording: false, noConsent: true };

function makeLogger(initial = RECORDING, extra = {}) {
  const state = { status: initial, calls: 0 };
  let n = 0;
  const logger = new TabLogger({
    getStatus: async () => { state.calls++; return state.status; },
    newId: () => `id-${++n}`,
    ...extra,
  });
  return { logger, state };
}
const tab = (tabId, url, title = "A page", incognito = false) => ({ tabId, url, title, incognito });
const secs = (s) => T0 + s * 1000;

function assertAllValid(events) {
  for (const e of events) {
    assert.deepEqual(validateEvent(e), [], JSON.stringify(e));
    assert.ok(e.url.includes(e.domain), "the url host belongs to the domain");
    assert.equal(e.duration_seconds, (Date.parse(e.end) - Date.parse(e.start)) / 1000);
    assert.ok(Date.parse(e.end) >= Date.parse(e.start));
  }
}

// ---- consent ---------------------------------------------------------------------------------------------------------

test("nothing is recorded when the agent does not allow it", async () => {
  for (const status of [PAUSED, NO_CONSENT]) {
    const { logger } = makeLogger(status);
    await logger.update(tab(1, "https://example.com/a"), secs(0));
    await logger.update(tab(2, "https://example.org/b"), secs(10));
    await logger.update(null, secs(20));
    assert.equal(logger.events.length, 0);
    assert.equal(logger.open, null);
  }
});

test("a pause drops the unfinished period and records nothing until recording is on again", async () => {
  const { logger, state } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), secs(0));
  state.status = PAUSED;
  await logger.update(tab(2, "https://example.org/b"), secs(30));
  assert.equal(logger.events.length, 0);
  assert.equal(logger.open, null);

  state.status = RECORDING;
  await logger.update(tab(2, "https://example.org/b"), secs(100));
  await logger.update(null, secs(130));
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].start, toUtcSeconds(secs(100))); // starts when recording came back, not at secs(0) or secs(30)
});

test("a pause keeps the events already closed, but 'no consent on record' removes them", async () => {
  const { logger, state } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), secs(0));
  await logger.update(tab(2, "https://example.org/b"), secs(10));
  assert.equal(logger.events.length, 1);

  state.status = PAUSED;
  await logger.update(tab(2, "https://example.org/b"), secs(20));
  assert.equal(logger.events.length, 1);
  assert.deepEqual(await logger.summary(), { recording: false, eventCount: 0 }); // the popup does not show a count while paused

  state.status = NO_CONSENT;
  await logger.update(null, secs(30));
  assert.equal(logger.events.length, 0);
});

test("the popup summary counts events only while recording is allowed", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), secs(0));
  await logger.update(tab(2, "https://example.com/b"), secs(5));
  assert.deepEqual(await logger.summary(), { recording: true, eventCount: 1 });
});

// ---- periods ---------------------------------------------------------------------------------------------------------

test("a tab switch closes one event and opens the next", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://www.youtube.com/shorts/Ab3dE9xQ2zK?x=1#t", "A short"), secs(0));
  await logger.update(tab(2, "https://news.bbc.co.uk/news/world", "BBC News - World"), secs(90));
  await logger.update(null, secs(150)); // the browser lost focus

  assert.equal(logger.events.length, 2);
  const [first, second] = logger.events;
  assert.equal(first.domain, "youtube.com");
  assert.equal(first.url, "https://www.youtube.com/shorts/*");
  assert.equal(first.title, null); // the sanitizer masked part of the path, so the title is dropped
  assert.equal(first.start, "2026-10-27T05:15:00Z");
  assert.equal(first.end, "2026-10-27T05:16:30Z");
  assert.equal(first.duration_seconds, 90);
  assert.equal(second.domain, "bbc.co.uk");
  assert.equal(second.title, "BBC News - World");
  assert.equal(second.duration_seconds, 60);
  assert.equal(first.end, second.start); // no gap, no overlap
  assertAllValid(logger.events);
});

test("every event has exactly the 16 contract fields with the extension values", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/docs"), secs(0));
  await logger.update(null, secs(7));
  const [e] = logger.events;
  assert.deepEqual(Object.keys(e).sort(), [
    "app", "category", "confidence", "domain", "duration_seconds", "end", "id", "idle", "label_source", "presence",
    "schema_version", "source", "start", "title", "uncertain", "url",
  ]);
  assert.deepEqual({ ...e, id: undefined }, {
    schema_version: "1", id: undefined, start: "2026-10-27T05:15:00Z", end: "2026-10-27T05:15:07Z", duration_seconds: 7,
    source: "extension", app: null, domain: "example.com", url: "https://example.com/docs", title: "A page",
    idle: false, presence: null, category: null, confidence: null, uncertain: false, label_source: null,
  });
});

test("ids are unique (real random ids)", async () => {
  const logger = new TabLogger({ getStatus: async () => RECORDING });
  for (let i = 0; i < 20; i++) await logger.update(tab(i, `https://example.com/p${i}`), secs(i));
  await logger.update(null, secs(21));
  assert.equal(new Set(logger.events.map((e) => e.id)).size, logger.events.length);
  assertAllValid(logger.events);
});

test("navigation in the same tab (including single-page apps) starts a new event", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/inbox"), secs(0));
  await logger.update(tab(1, "https://example.com/settings"), secs(20));
  await logger.update(null, secs(30));
  assert.deepEqual(logger.events.map((e) => e.url), ["https://example.com/inbox", "https://example.com/settings"]);
});

test("a repeated update for the same page, or just a new title, does not split the period", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a?x=1", "Loading"), secs(0));
  await logger.update(tab(1, "https://example.com/a?x=2#top", "The real title"), secs(5)); // only query and fragment differ
  await logger.update(tab(1, "https://example.com/a", "The real title"), secs(9));
  await logger.update(null, secs(10));
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].title, "The real title"); // the latest title wins
  assert.equal(logger.events[0].duration_seconds, 10);
});

test("losing browser focus ends the event, and coming back starts a new one", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), secs(0));
  await logger.update(null, secs(40));
  await logger.update(tab(1, "https://example.com/a"), secs(100));
  await logger.update(null, secs(110));
  assert.deepEqual(logger.events.map((e) => [e.start, e.duration_seconds]), [["2026-10-27T05:15:00Z", 40], ["2026-10-27T05:16:40Z", 10]]);
});

test("time with no event open is never counted (nothing between focus lost and focus back)", async () => {
  const { logger } = makeLogger();
  await logger.update(null, secs(0));
  await logger.update(null, secs(50));
  assert.equal(logger.events.length, 0);
});

test("a period that is still open is not an event yet", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), secs(0));
  assert.equal(logger.events.length, 0);
  assert.notEqual(logger.open, null);
});

test("sub-second periods are kept as zero-second events", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), T0 + 100);
  await logger.update(tab(2, "https://example.com/b"), T0 + 400);
  await logger.update(null, T0 + 900);
  assert.equal(logger.events.length, 2);
  assert.ok(logger.events.every((e) => e.duration_seconds === 0));
  assertAllValid(logger.events);
});

test("a clock that goes backwards never produces a negative duration", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a"), secs(50));
  await logger.update(null, secs(10));
  assert.equal(logger.events[0].duration_seconds, 0);
  assertAllValid(logger.events);
});

// ---- what is skipped ---------------------------------------------------------------------------------------------------

test("internal pages, files, the new tab page and extension pages are never recorded", async () => {
  const { logger } = makeLogger();
  const pages = ["chrome://extensions", "edge://newtab/", "about:blank", "file:///C:/secret.txt", "chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html",
    "view-source:https://example.com", "http://localhost:3000/", "https://", "", undefined];
  let t = 0;
  for (const url of pages) await logger.update(tab(1, url), secs(t++));
  await logger.update(null, secs(t));
  assert.equal(logger.events.length, 0);
});

test("a private window tab is never recorded", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/a", "t", true), secs(0));
  await logger.update(null, secs(10));
  assert.equal(logger.events.length, 0);
});

test("hidden sites produce no event at all, and the event before one ends normally", async () => {
  const { logger } = makeLogger(RECORDING, { isHiddenSite: (d) => d === "secret.example" });
  await logger.update(tab(1, "https://example.com/a"), secs(0));
  await logger.update(tab(2, "https://www.secret.example/x", "Hidden title"), secs(10));
  await logger.update(null, secs(50));
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].domain, "example.com");
  assert.ok(!JSON.stringify(logger.events).includes("secret.example"));
});

// ---- titles ----------------------------------------------------------------------------------------------------------

test("titles are cut to 120 characters", () => {
  const long = "x".repeat(300);
  assert.equal(makeTitle(long, { masked: false, host: "example.com" }).length, MAX_TITLE_CHARS);
  assert.equal(makeTitle("é".repeat(200), { masked: false, host: "example.com" }), "é".repeat(120));
  assert.equal([...makeTitle("😀".repeat(200), { masked: false, host: "example.com" })].length, 120); // never cuts an emoji in half
});

test("titles are null when the path was masked or the site is webmail or messaging", async () => {
  assert.equal(makeTitle("Inbox", { masked: true, host: "example.com" }), null);
  assert.equal(makeTitle("Inbox (3)", { masked: false, host: "mail.google.com" }), null);
  assert.equal(makeTitle("Chat", { masked: false, host: "web.whatsapp.com" }), null);
  assert.equal(makeTitle("Chat", { masked: false, host: "eu.discord.com" }), null); // subdomains too
  assert.equal(makeTitle("Maps", { masked: false, host: "www.google.com" }), "Maps"); // other google.com sites are not affected
  assert.equal(makeTitle("Maps", { masked: false, host: "notmail.google.com.evil.example" }), "Maps");
});

test("titles: empty, missing, wrong type and control characters", () => {
  const ctx = { masked: false, host: "example.com" };
  assert.equal(makeTitle("", ctx), null);
  assert.equal(makeTitle("   ", ctx), null);
  assert.equal(makeTitle(undefined, ctx), null);
  assert.equal(makeTitle(42, ctx), null);
  assert.equal(makeTitle("a\u0000b\n\tc", ctx), "a b c");
});

test("webmail pages still produce an event, only without a title", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://mail.google.com/mail/u/0/", "Inbox - alice@example.com - Gmail"), secs(0));
  await logger.update(null, secs(10));
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].title, null);
  assert.equal(logger.events[0].domain, "google.com");
  assertAllValid(logger.events);
});

test("the sensitive-domain list is a plain list of lowercase hostnames", () => {
  for (const host of SENSITIVE_TITLE_HOSTS) assert.match(host, /^[a-z0-9.-]+\.[a-z]+$/, host);
  assert.ok(SENSITIVE_TITLE_HOSTS.length >= 10);
});

// ---- schema ----------------------------------------------------------------------------------------------------------

test("a realistic browsing session produces only schema-valid events", async () => {
  const { logger } = makeLogger();
  const visits = [
    "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10",
    "https://www.youtube.com/shorts/Ab3dE9xQ2zK?x=1#t",
    "https://en.wikipedia.org/wiki/Albert_Einstein",
    "https://user:secret@intranet.example.com:8443/reports/2024/q3?token=abc",
    "https://news.bbc.co.uk/news/world-europe-12345678",
    "https://www.daraz.com.pk/products/some-product-i123456789.html",
    "https://lums.edu.pk/",
    "http://192.168.1.10:8080/admin",
    "https://example.com/reset/Zm9vYmFyYmF6",
    "https://example.com/users/alice@example.com",
    "https://example.com/" + "section/".repeat(400),
    "https://example.com/x/" + "a".repeat(100),
    "https://mail.google.com/mail/u/0/#inbox/FMfcgzGxRZkWzNqkTcbHsXdBvMcQwHpS",
    "https://münchen.example/straße",
  ];
  let t = 0;
  for (const [i, url] of visits.entries()) await logger.update(tab(i + 1, url, "Title ".repeat(40)), secs((t += 17)));
  await logger.update(null, secs(t + 5));

  assert.equal(logger.events.length, visits.length);
  assertAllValid(logger.events);
  assert.deepEqual(logger.events.map((e) => e.domain), [
    "youtube.com", "youtube.com", "wikipedia.org", "intranet.example.com".split(".").slice(-2).join("."), "bbc.co.uk",
    "daraz.com.pk", "lums.edu.pk", "192.168.1.10", "example.com", "example.com", "example.com", "example.com", "google.com", "xn--mnchen-3ya.example",
  ]);
  for (const e of logger.events) {
    assert.ok(!/[?#]/.test(e.url), e.url);
    assert.ok(!e.url.includes("secret"), e.url);
    assert.equal(e.domain, getDomain(new URL(e.url).hostname));
    assert.ok(e.title === null || e.title.length <= 120);
  }
});

test("the event code never leaks the raw URL, query, fragment, userinfo or a masked id into any field", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://bob:hunter2@example.com:9000/files/Ab3dE9xQ2zK/view?session=SECRET123#frag", "Bob's files"), secs(0));
  await logger.update(null, secs(5));
  const text = JSON.stringify(logger.events);
  for (const leak of ["hunter2", "bob", "9000", "Ab3dE9xQ2zK", "SECRET123", "frag", "session="]) assert.ok(!text.includes(leak), leak);
  assertAllValid(logger.events);
});
