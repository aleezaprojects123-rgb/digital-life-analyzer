import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { createStatusClient, parseStatus, STATUS_URL, CACHE_MS, ID_HEADER } from "../agent-status.js";

const json = (body, ok = true) => async () => ({ ok, json: async () => body });
const REC = { recording: true, noConsent: false };
const STOP = { recording: false, noConsent: false };

test("only consented AND trackingEnabled both true means recording", () => {
  assert.deepEqual(parseStatus({ consented: true, trackingEnabled: true, consentVersion: 1 }), REC);
  assert.deepEqual(parseStatus({ consented: true, trackingEnabled: false, consentVersion: 1 }), STOP);
  assert.deepEqual(parseStatus({ consented: false, trackingEnabled: false, consentVersion: 1 }), STOP); // paused
  assert.deepEqual(parseStatus({ consented: false, trackingEnabled: false, consentVersion: 0 }), { recording: false, noConsent: true });
  assert.deepEqual(parseStatus({ consented: false, trackingEnabled: true, consentVersion: 1 }), STOP);
});

test("any odd answer means no recording", () => {
  for (const body of [null, undefined, 5, "yes", [], {}, { consented: "true", trackingEnabled: true }, { consented: 1, trackingEnabled: 1 },
    { consented: true }, { trackingEnabled: true }, { consented: null, trackingEnabled: null }]) {
    assert.equal(parseStatus(body).recording, false, JSON.stringify(body));
  }
});

test("the agent not running, a bad status code or bad JSON means no recording", async () => {
  const failing = [
    async () => { throw new TypeError("Failed to fetch"); },
    json({ consented: true, trackingEnabled: true }, false),
    async () => ({ ok: true, json: async () => { throw new SyntaxError("bad json"); } }),
  ];
  for (const fetchFn of failing) assert.deepEqual(await createStatusClient({ fetchFn }).get(), STOP);
});

test("it asks the fixed local address with a plain GET and sends nothing about the user", async () => {
  let seen;
  const client = createStatusClient({ extensionId: "abcdefghijklmnopabcdefghijklmnop", fetchFn: async (url, init) => { seen = { url, init }; return json({ consented: true, trackingEnabled: true, consentVersion: 1 })(); } });
  assert.deepEqual(await client.get(), REC);
  assert.equal(seen.url, "http://127.0.0.1:49721/status");
  assert.equal(STATUS_URL, "http://127.0.0.1:49721/status");
  assert.equal(seen.init.method, "GET");
  assert.equal(seen.init.credentials, "omit");
  assert.equal(seen.init.body, undefined);
  assert.deepEqual(seen.init.headers, { "X-DLA-Extension-Id": "abcdefghijklmnopabcdefghijklmnop" }); // only the extension's own id
  assert.equal(ID_HEADER, "X-DLA-Extension-Id");
});

test("the answer is cached for at most 15 seconds", async () => {
  assert.ok(CACHE_MS <= 15_000);
  let now = 1_000_000;
  let calls = 0;
  const client = createStatusClient({ now: () => now, fetchFn: async () => { calls++; return json({ consented: true, trackingEnabled: true, consentVersion: 1 })(); } });
  await client.get(); await client.get(); now += 5_000; await client.get();
  assert.equal(calls, 1);
  now += 10_001;
  await client.get();
  assert.equal(calls, 2);
  const huge = createStatusClient({ now: () => now, cacheMs: 600_000, fetchFn: async () => { calls++; return json({ consented: true, trackingEnabled: true })(); } });
  const before = calls;
  await huge.get(); now += 15_001; await huge.get();
  assert.equal(calls - before, 2); // even a larger setting is held to 15 seconds
});

test("a failure is also cached briefly, and the next check after that sees a change", async () => {
  let now = 0;
  let consented = false;
  const client = createStatusClient({ now: () => now, fetchFn: async () => json({ consented, trackingEnabled: consented, consentVersion: 1 })() });
  assert.equal((await client.get()).recording, false);
  consented = true;
  assert.equal((await client.get()).recording, false); // still the cached answer
  now += CACHE_MS + 1;
  assert.equal((await client.get()).recording, true);
});

test("two questions at the same moment make one request", async () => {
  let calls = 0;
  const client = createStatusClient({ fetchFn: async () => { calls++; await new Promise((r) => setTimeout(r, 10)); return json({ consented: true, trackingEnabled: true })(); } });
  await Promise.all([client.get(), client.get(), client.get()]);
  assert.equal(calls, 1);
});

// ---- manifest ------------------------------------------------------------------------------------------------------

const manifest = JSON.parse(readFileSync(new URL("../manifest.json", import.meta.url), "utf8"));

test("manifest: only the tabs permission and only the agent's local address", () => {
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions, ["tabs"]);
  assert.deepEqual(manifest.host_permissions, ["http://127.0.0.1:49721/*"]);
  assert.equal(manifest.incognito, "not_allowed");
  assert.deepEqual(manifest.background, { service_worker: "background.js", type: "module" });
  for (const absent of ["content_scripts", "optional_permissions", "optional_host_permissions", "externally_connectable", "web_accessible_resources", "commands", "chrome_url_overrides"])
    assert.equal(absent in manifest, false, absent);
  assert.ok(!manifest.permissions.some((p) => ["storage", "alarms", "idle", "scripting", "activeTab", "history", "webRequest", "cookies", "unlimitedStorage"].includes(p)));
});

test("manifest: the fixed development key gives the extension ID documented in the README", () => {
  const der = Buffer.from(manifest.key, "base64");
  const hash = createHash("sha256").update(der).digest().subarray(0, 16);
  const id = [...hash].map((b) => "abcdefghijklmnop"[b >> 4] + "abcdefghijklmnop"[b & 15]).join("");
  assert.match(id, /^[a-p]{32}$/);
  assert.match(readFileSync(new URL("../README.md", import.meta.url), "utf8"), new RegExp(id));
  assert.ok(!/PRIVATE KEY/.test(manifest.key));
});

test("the extension code uses no content scripts, no page reading and no other network calls", () => {
  const dir = new URL("../", import.meta.url);
  const sources = readdirSync(dir).filter((f) => f.endsWith(".js")).map((f) => [f, readFileSync(new URL(f, dir), "utf8")]);
  for (const [file, code] of sources) {
    const lines = code.split("\n").filter((l) => !l.trim().startsWith("//") && !l.trim().startsWith("*"));
    const body = lines.join("\n");
    for (const banned of ["chrome.scripting", "executeScript", "chrome.storage", "localStorage", "indexedDB", "chrome.alarms", "setInterval", "XMLHttpRequest", "WebSocket", "sendBeacon", "chrome.history", "chrome.cookies", "document.cookie", "chrome.webRequest"])
      assert.ok(!body.includes(banned), `${file} uses ${banned}`);
    if (file !== "agent-status.js") assert.ok(!/\bfetch\s*\(/.test(body), `${file} makes a network call`);
  }
  const background = sources.find(([f]) => f === "background.js")[1];
  assert.ok(!background.includes("setTimeout"));
});
