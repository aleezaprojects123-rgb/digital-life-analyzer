// Idle tracking is gone. These tests prove two things: nothing about idle (or tab-switch counting) is produced any more, and ordinary
// timing is exactly what it was: a period runs from the moment a page comes to the front until another page does, whatever the user does.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { TabLogger } from "../tab-logger.js";
import { BROWSER_EVENT_FIELDS } from "../event-normalizer.js";
import { validateEvent } from "./schema-check.js";

const T0 = Date.UTC(2026, 9, 31, 10, 15, 20);
const secs = (s) => T0 + s * 1000;
const tab = (tabId, url, title = "Title") => ({ tabId, url, title, incognito: false });
const REEL = (code) => `https://www.instagram.com/reel/${code}/`;

function make() {
  let n = 0;
  return new TabLogger({ getStatus: async () => ({ recording: true, noConsent: false }), newId: () => `id-${++n}` });
}

// ---- nothing about idle exists any more ------------------------------------------------------------------------------------------

test("no extension source file uses chrome.idle or has any idle state, threshold or idle event", () => {
  const dir = new URL("../", import.meta.url);
  for (const file of readdirSync(dir).filter((f) => f.endsWith(".js"))) {
    const code = readFileSync(new URL(file, dir), "utf8").split("\n").filter((l) => !l.trim().startsWith("//") && !l.trim().startsWith("*")).join("\n");
    for (const banned of ["chrome.idle", "setIdleState", "IDLE_THRESHOLD", "idleAfterMs", "queryState", "onStateChanged", "browser_idle"])
      assert.ok(!code.includes(banned), `${file} still has ${banned}`);
  }
});

test("the manifest asks for the tabs permission only: the idle permission is gone", () => {
  const manifest = JSON.parse(readFileSync(new URL("../manifest.json", import.meta.url), "utf8"));
  assert.deepEqual(manifest.permissions, ["tabs"]);
});

test("the event shape has no idle field, no browser idle flag and no switch count", () => {
  for (const gone of ["idle", "browser_idle", "switched_count", "switch_count"]) assert.ok(!BROWSER_EVENT_FIELDS.includes(gone), gone);
});

test("an event that carries an idle field or a switch count is not a valid event", async () => {
  const logger = make();
  await logger.update(tab(1, "https://example.com/"), secs(0));
  await logger.update(null, secs(10));
  const [event] = logger.events;
  assert.deepEqual(validateEvent(event), []);
  for (const extra of ["idle", "browser_idle", "switched_count", "app", "presence"])
    assert.notDeepEqual(validateEvent({ ...event, [extra]: false }), [], extra);
});

// ---- ordinary timing is unchanged ------------------------------------------------------------------------------------------------

test("a page that stays in front for 60 seconds is one event with its start, end and duration", async () => {
  const logger = make();
  await logger.update(tab(1, "https://example.com/docs"), secs(0));
  await logger.update(null, secs(60));
  const [e] = logger.events;
  assert.deepEqual([e.start, e.end, e.duration_seconds], ["2026-10-31T10:15:20Z", "2026-10-31T10:16:20Z", 60]);
});

test("three hours on one page is one event of three hours: nothing cuts it because nothing happened", async () => {
  const logger = make();
  await logger.update(tab(1, "https://example.com/long-read"), secs(0));
  await logger.update(tab(1, "https://example.com/long-read", "Still reading"), secs(5400));     // only the title changed
  await logger.update(null, secs(10800));
  assert.equal(logger.events.length, 1);
  assert.equal(logger.events[0].duration_seconds, 10800);
});

test("changing the active tab closes the previous period and opens the next, back to back", async () => {
  const logger = make();
  await logger.update(tab(1, "https://a.example.com/"), secs(0));
  await logger.update(tab(2, "https://b.example.com/"), secs(60));
  await logger.update(tab(3, "https://c.example.com/"), secs(100));
  await logger.update(null, secs(130));
  assert.deepEqual(logger.events.map((e) => [e.domain, e.duration_seconds]), [["example.com", 60], ["example.com", 40], ["example.com", 30]]);
  assert.equal(logger.events[0].end, logger.events[1].start);
  assert.equal(logger.events[1].end, logger.events[2].start);
  assert.equal(logger.events.reduce((sum, e) => sum + e.duration_seconds, 0), 130);                  // the periods add up to the whole time
});

test("reloading or navigating inside the same tab keeps the tab's own timeline", async () => {
  const logger = make();
  await logger.update(tab(1, "https://a.example.com/one"), secs(0));
  await logger.update(tab(1, "https://a.example.com/one"), secs(20));
  await logger.update(tab(1, "https://a.example.com/two"), secs(30));
  await logger.update(null, secs(50));
  assert.deepEqual(logger.events.map((e) => e.duration_seconds), [30, 20]);
});

test("Reels are still counted once each and still timed", async () => {
  const logger = make();
  await logger.update(tab(1, REEL("C8aBcDeFgHi")), secs(0));
  await logger.update(tab(1, REEL("D1xYzWvUtSr")), secs(20));
  await logger.update(tab(1, REEL("E5mNoPqRsTu")), secs(45));
  await logger.update(null, secs(70));
  assert.deepEqual(logger.events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [["reel", 1, 20], ["reel", 1, 25], ["reel", 1, 25]]);
});

test("YouTube videos and Shorts are still counted, the YouTube home and search pages are time only", async () => {
  const logger = make();
  await logger.update(tab(1, "https://www.youtube.com/watch?v=dQw4w9WgXcQ"), secs(0));
  await logger.update(tab(1, "https://www.youtube.com/shorts/mF2kqwe91Zc"), secs(30));
  await logger.update(tab(1, "https://www.youtube.com/results?search_query=anything"), secs(50));
  await logger.update(tab(1, "https://www.youtube.com/"), secs(70));
  await logger.update(null, secs(90));
  assert.deepEqual(logger.events.map((e) => [e.activity_type, e.content_count, e.duration_seconds]), [["video", 1, 30], ["short_video", 1, 20], ["search", 0, 20], ["webpage", 0, 20]]);
});

test("a lost focus still ends the period (another program came to the front), and coming back starts a new one", async () => {
  const logger = make();
  await logger.update(tab(1, "https://example.com/"), secs(0));
  await logger.update(null, secs(30), { focusLoss: true });
  await logger.update(tab(1, "https://example.com/"), secs(100));
  await logger.update(null, secs(120));
  assert.deepEqual(logger.events.map((e) => [e.start.slice(11), e.duration_seconds]), [["10:15:20Z", 30], ["10:17:00Z", 20]]);
});
