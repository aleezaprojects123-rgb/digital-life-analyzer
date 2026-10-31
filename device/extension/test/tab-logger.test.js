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
  assert.equal(first.title, "A short"); // a masked Shorts id is a public video id: the video title is kept
  assert.equal(first.start, "2026-10-27T05:15:00Z");
  assert.equal(first.end, "2026-10-27T05:16:30Z");
  assert.equal(first.duration_seconds, 90);
  assert.equal(second.domain, "bbc.co.uk");
  assert.equal(second.title, "BBC News - World");
  assert.equal(second.duration_seconds, 60);
  assert.equal(first.end, second.start); // no gap, no overlap
  assertAllValid(logger.events);
});

test("every event has exactly the canonical browser-event fields with the extension values", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://example.com/docs"), secs(0));
  await logger.update(null, secs(7));
  const [e] = logger.events;
  assert.deepEqual(Object.keys(e).sort(), [
    "activity_type", "category", "confidence", "content_count", "domain", "duration_seconds", "end", "id", "label_source",
    "platform", "schema_version", "session_id", "source", "start", "title", "uncertain", "url",
  ]);
  assert.deepEqual({ ...e, id: undefined }, {
    schema_version: "1", id: undefined, start: "2026-10-27T05:15:00Z", end: "2026-10-27T05:15:07Z", duration_seconds: 7,
    source: "extension", domain: "example.com", url: "https://example.com/docs", title: "A page",
    platform: "unknown", activity_type: "webpage",
    content_count: 0, session_id: null,
    category: null, confidence: null, uncertain: false, label_source: null,
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

// ---- mixed sequences: normal page -> excluded page -> normal page ------------------------------------------------------
// The popup count is events.length, i.e. FINISHED periods. Leaving a normal page for an excluded one finishes that
// normal page's period (count +1). The excluded page itself must never become an event.

const domainsOf = (logger) => logger.events.map((e) => e.domain);

test("normal -> private -> normal: only the two normal pages become events, the private page none", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://first.example.com/a"), secs(0));
  await logger.update(tab(2, "https://private-site.example.org/secret-area", "Private title", true), secs(10)); // a private snapshot
  assert.deepEqual(domainsOf(logger), ["example.com"]);   // leaving the first page ended it: +1
  assert.equal(logger.open, null);                        // nothing is open for the private page
  await logger.update(tab(3, "https://second.example.net/b"), secs(40));
  await logger.update(null, secs(50));

  assert.deepEqual(domainsOf(logger), ["example.com", "example.net"]);
  assert.ok(!JSON.stringify(logger.events).includes("private-site"));
  assert.ok(!JSON.stringify(logger.events).includes("Private title"));
  assertAllValid(logger.events);
});

test("normal -> several private pages -> normal: moving between private pages changes nothing", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://first.example.com/a"), secs(0));
  const afterLeaving = [];
  for (const [i, host] of ["p1.example.org", "p2.example.org", "p3.example.org", "p4.example.org"].entries()) {
    await logger.update(tab(10 + i, `https://${host}/x`, "t", true), secs(10 + i));
    afterLeaving.push(logger.events.length);
  }
  assert.deepEqual(afterLeaving, [1, 1, 1, 1]);           // +1 once, then no change
  assert.equal(logger.open, null);
});

test("normal -> internal pages -> normal: only the two normal pages become events", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://first.example.com/a"), secs(0));
  await logger.update(tab(1, "edge://settings"), secs(10));       // the same tab goes to an internal page
  assert.equal(logger.events.length, 1);                          // +1: the normal page ended
  for (const [i, url] of ["chrome://settings", "edge://newtab/", "about:blank", "", "file:///C:/x.txt", "chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html"].entries()) {
    await logger.update(tab(2 + i, url), secs(11 + i));
    assert.equal(logger.events.length, 1, url);                   // moving among excluded pages adds nothing
    assert.equal(logger.open, null, url);
  }
  await logger.update(tab(20, "https://second.example.net/b"), secs(30));
  await logger.update(null, secs(40));

  assert.deepEqual(domainsOf(logger), ["example.com", "example.net"]);
  assertAllValid(logger.events);
});

test("the count equals the number of normal pages that have ended, whatever excluded pages come between", async () => {
  const { logger } = makeLogger();
  const steps = [
    tab(1, "https://a.example.com/"), tab(2, "edge://settings"), tab(3, "https://b.example.com/", "t", true),
    tab(4, "https://c.example.com/"), tab(5, "about:blank"), tab(6, "https://d.example.com/"), null,
  ];
  let t = 0;
  const counts = [];
  for (const step of steps) { await logger.update(step, secs(t++)); counts.push(logger.events.length); }
  // a ends at step 2 (1), c ends at step 5 (2), d ends at the focus loss (3). The private b and internal pages add none.
  assert.deepEqual(counts, [0, 1, 1, 1, 2, 2, 3]);
  assert.deepEqual(domainsOf(logger), ["example.com", "example.com", "example.com"]);
  assert.deepEqual(logger.events.map((e) => e.url), ["https://a.example.com", "https://c.example.com", "https://d.example.com"]);
});

test("the page you are on is not counted until you leave it", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/"), secs(0));
  assert.deepEqual(await logger.summary(), { recording: true, eventCount: 0 });
  await logger.update(tab(2, "https://b.example.com/"), secs(5));
  assert.deepEqual(await logger.summary(), { recording: true, eventCount: 1 });
});

test("a title change or a same-page update does not raise the count; a second tab on the same address does", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/x", "One"), secs(0));
  await logger.update(tab(1, "https://a.example.com/x?y=1", "Two"), secs(2));   // same page, new title
  assert.equal(logger.events.length, 0);
  await logger.update(tab(2, "https://a.example.com/x", "One"), secs(4));        // another tab, same address: a new period
  assert.equal(logger.events.length, 1);
});

// ---- the popup takes the focus: that must not end a page's period -------------------------------------------------------

const focusLoss = { focusLoss: true };

test("popup: a focus loss followed by 'the popup opened' is taken back, with the original start time", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/x"), secs(0));
  await logger.update(null, secs(10), focusLoss);               // the popup took the focus
  assert.equal(logger.events.length, 1);                        // for a moment it looks like the page ended
  logger.popupOpened();
  assert.equal(logger.events.length, 0);                        // taken back
  assert.equal(logger.open.startMs, secs(0));                   // still the same period, same start

  await logger.update(tab(1, "https://a.example.com/x"), secs(12));   // the popup closes and the window has focus again
  await logger.update(tab(2, "https://b.example.com/"), secs(30));
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].start, toUtcSeconds(secs(0)));
  assert.equal(logger.events[0].duration_seconds, 30);
  assertAllValid(logger.events);
});

test("popup: while it is open a focus loss is ignored", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/x"), secs(0));
  logger.popupOpened();
  await logger.update(null, secs(5), focusLoss);
  assert.equal(logger.events.length, 0);
  assert.notEqual(logger.open, null);
  logger.popupClosed();
  await logger.update(null, secs(8), focusLoss);                // after it closed, a real loss ends the period again
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].duration_seconds, 8);
});

test("popup: only a focus loss can be taken back, not leaving for an internal page or a private window", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/x"), secs(0));
  await logger.update(tab(1, "edge://settings"), secs(10));     // null, but not a focus loss
  logger.popupOpened();
  assert.equal(logger.events.length, 1);                        // stays ended
  assert.equal(logger.open, null);

  const second = makeLogger().logger;
  await second.update(tab(1, "https://a.example.com/x"), secs(0));
  await second.update(tab(2, "https://p.example.org/", "t", true), secs(10));   // a private snapshot
  second.popupOpened();
  assert.equal(second.events.length, 1);
});

test("popup: anything that happened after the focus loss makes it final", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/x"), secs(0));
  await logger.update(null, secs(10), focusLoss);
  await logger.update(tab(2, "https://b.example.com/"), secs(11));   // the user is on another page now
  logger.popupOpened();
  assert.equal(logger.events.length, 1);
  assert.equal(logger.open.url, "https://b.example.com");
});

test("popup: it is taken back only once, and does nothing when no period was ended", async () => {
  const { logger } = makeLogger();
  logger.popupOpened();                                         // nothing open, nothing to undo
  logger.popupClosed();
  await logger.update(tab(1, "https://a.example.com/x"), secs(0));
  await logger.update(null, secs(5), focusLoss);
  logger.popupOpened();
  logger.popupOpened();
  assert.equal(logger.events.length, 0);
  assert.notEqual(logger.open, null);
});

test("popup: a pause or missing consent still drops everything, popup or not", async () => {
  const { logger, state } = makeLogger();
  await logger.update(tab(1, "https://a.example.com/x"), secs(0));
  logger.popupOpened();
  state.status = PAUSED;
  await logger.update(tab(2, "https://b.example.com/"), secs(5));
  assert.equal(logger.open, null);
  state.status = RECORDING;
  logger.popupClosed();
  await logger.update(null, secs(8), focusLoss);
  assert.equal(logger.events.length, 0);
});

test("popup: internal pages and private tabs are still never recorded while the popup is open", async () => {
  const { logger } = makeLogger();
  logger.popupOpened();
  await logger.update(tab(1, "chrome://settings"), secs(0));
  await logger.update(tab(2, "https://p.example.org/", "t", true), secs(1));
  await logger.update(tab(3, "https://a.example.com/"), secs(2));
  await logger.update(null, secs(9));                           // not a focus loss: ends it
  assert.deepEqual(domainsOf(logger), ["example.com"]);
  assert.ok(!JSON.stringify(logger.events).includes("p.example.org"));
});

// ---- a period that ended because the browser lost focus is delivered even if nothing else ever happens ---------------------

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

test("leaving for another app: the held period is delivered by itself after the hold time", async () => {
  const sent = [];
  const { logger } = makeLogger(RECORDING, { holdMs: 20, onEventClosed: (e) => sent.push(e) });
  await logger.update(tab(1, "https://github.com/x"), secs(0));
  await logger.update(null, secs(25), focusLoss);               // switched to VS Code
  assert.equal(sent.length, 0);                                 // held for a moment (the popup could take it back)
  await wait(60);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].domain, "github.com");
  assert.equal(sent[0].duration_seconds, 25);
  assert.equal(logger.events.length, 1);
  await wait(60);
  assert.equal(sent.length, 1);                                 // sent once, not twice
});

test("the popup opening inside the hold time cancels the delivery (the page was never left)", async () => {
  const sent = [];
  const { logger } = makeLogger(RECORDING, { holdMs: 20, onEventClosed: (e) => sent.push(e) });
  await logger.update(tab(1, "https://github.com/x"), secs(0));
  await logger.update(null, secs(10), focusLoss);
  logger.popupOpened();
  await wait(60);
  assert.equal(sent.length, 0);
  assert.equal(logger.events.length, 0);
  assert.equal(logger.open.startMs, secs(0));
});

test("a change after the focus loss sends the held period at once and only once", async () => {
  const sent = [];
  const { logger } = makeLogger(RECORDING, { holdMs: 20, onEventClosed: (e) => sent.push(e) });
  await logger.update(tab(1, "https://github.com/x"), secs(0));
  await logger.update(null, secs(10), focusLoss);
  await logger.update(tab(2, "https://b.example.com/"), secs(12));
  assert.equal(sent.length, 1);
  await wait(60);
  assert.equal(sent.length, 1);
});

// ---- the video changes inside the same tab (YouTube Shorts swipe, next /watch video) ------------------------------------------

const SHORT_A = "https://www.youtube.com/shorts/mF2kqwe91Zc";   // masks to /shorts/*
const SHORT_B = "https://www.youtube.com/shorts/Zk9qPw3LxT7";   // masks to /shorts/* as well

test("Short A -> Short B in the same tab: two events, each with its own title, the id is never stored", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, SHORT_A, "First short - YouTube"), secs(0));
  await logger.update(tab(1, SHORT_B, "First short - YouTube"), secs(20));   // url changed, tab title not yet updated
  await logger.update(tab(1, SHORT_B, "Second short - YouTube"), secs(21));  // the page updates its title
  await logger.update(null, secs(50), { focusLoss: true });
  logger.flush();

  assert.equal(logger.events.length, 2);
  const [a, b] = logger.events;
  assert.equal(a.url, "https://www.youtube.com/shorts/*");
  assert.equal(b.url, "https://www.youtube.com/shorts/*");
  assert.equal(a.title, "First short - YouTube");
  assert.equal(b.title, "Second short - YouTube");
  assert.equal(a.duration_seconds, 20);
  assert.equal(b.duration_seconds, 30);
  const everything = JSON.stringify(logger.events);
  assert.ok(!everything.includes("mF2kqwe91Zc") && !everything.includes("Zk9qPw3LxT7"), "the raw id must never be in an event");
  assert.ok(!Object.keys(a).includes("key"));
});

test("a title that did not change after the video changed is not trusted for the new video", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, SHORT_A, "Old title - YouTube"), secs(0));
  await logger.update(tab(1, SHORT_B, "Old title - YouTube"), secs(10));
  await logger.update(null, secs(40), { focusLoss: true });
  logger.flush();
  assert.equal(logger.events[0].title, "Old title - YouTube");
  assert.equal(logger.events[1].title, null);   // nothing invented
});

test("/watch?v=A -> /watch?v=B in the same tab is two events, although the stored URL is the same", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Song one - YouTube"), secs(0));
  await logger.update(tab(1, "https://www.youtube.com/watch?v=9bZkp7q19f0", "Song one - YouTube"), secs(200));
  await logger.update(tab(1, "https://www.youtube.com/watch?v=9bZkp7q19f0", "Song two - YouTube"), secs(201));
  await logger.update(null, secs(260), { focusLoss: true });
  logger.flush();
  assert.equal(logger.events.length, 2);
  assert.equal(logger.events[0].url, "https://www.youtube.com/watch");
  assert.equal(logger.events[0].title, "Song one - YouTube");
  assert.equal(logger.events[1].title, "Song two - YouTube");
  assert.ok(!JSON.stringify(logger.events).includes("dQw4w9WgXcQ"));
});

test("the same video with only a new title stays one event; a masked path on another site still has no title", async () => {
  const { logger } = makeLogger();
  await logger.update(tab(1, SHORT_A, "Loading - YouTube"), secs(0));
  await logger.update(tab(1, SHORT_A, "Real title - YouTube"), secs(2));
  await logger.update(tab(2, "https://example.com/reset/ab12", "Reset your password"), secs(30));
  await logger.update(null, secs(60), { focusLoss: true });
  logger.flush();
  assert.equal(logger.events.length, 2);
  assert.equal(logger.events[0].title, "Real title - YouTube");
  assert.equal(logger.events[1].title, null);
});
