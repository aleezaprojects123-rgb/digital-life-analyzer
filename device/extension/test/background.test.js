// Tests for background.js (the service worker) with a small fake `chrome` object. No browser is started.
//
// The fake follows what a Chromium browser does for an extension that is NOT allowed in private windows
// ("incognito": "not_allowed"): it never lists private windows or tabs, and asking for one throws. The tests also
// cover the worst case, where a private window or tab is reported anyway and the extension's own checks must refuse it.
//
// What this cannot prove: how a real Chrome or Edge behaves. That stays a manual check (DAY2-TEST-CHECKLIST.md).
import { test, afterEach } from "node:test";
import assert from "node:assert/strict";

const EXT_ID = "abcdefghijklmnopabcdefghijklmnop";
const POPUP_URL = `chrome-extension://${EXT_ID}/popup.html`;
const NONE = -1; // chrome.windows.WINDOW_ID_NONE
const flush = async () => { for (let i = 0; i < 30; i++) await new Promise((r) => setImmediate(r)); };

let loads = 0;
const originalFetch = globalThis.fetch;
const originalSetInterval = globalThis.setInterval;
const originalClearInterval = globalThis.clearInterval;
const originalDateNow = Date.now;
afterEach(() => {
  Date.now = originalDateNow;
  delete globalThis.chrome;
  globalThis.fetch = originalFetch;
  globalThis.setInterval = originalSetInterval;
  globalThis.clearInterval = originalClearInterval;
});

/**
 * Loads a fresh copy of background.js against a fake chrome.
 * `windows`: { id: { focused, incognito, hidden, tab: { id, url, title, incognito, active } } }
 *   hidden: true  = like a private window the extension may not see: windows.get throws.
 */
async function load(windows, { recording = true } = {}) {
  const calls = { query: [], get: [], getLastFocused: 0, ping: 0 };
  // The keep-alive interval is faked: tests run its ticks by hand and look at when it was started and stopped.
  const timers = [];
  // Only the keep-alive's own tick is captured; any other timer (the test runner's) is left alone.
  globalThis.setInterval = (fn, ms) => {
    if (fn.name !== "tick") return originalSetInterval(fn, ms);
    const t = { fn, ms, cleared: false, unref() {} };
    timers.push(t);
    return t;
  };
  globalThis.clearInterval = (t) => { if (t && "cleared" in t) t.cleared = true; else originalClearInterval(t); };
  // The worker reads the clock when it is created, so the test clock is installed before it loads.
  const clock = { offset: 0 };
  Date.now = () => originalDateNow() + clock.offset;
  const listeners = {};
  const event = (name) => ({ addListener: (fn) => { listeners[name] = fn; } });
  let lastFocused = Object.keys(windows).map(Number).find((id) => windows[id].focused) ?? Number(Object.keys(windows)[0]);

  globalThis.chrome = {
    runtime: { id: EXT_ID, getPlatformInfo: async () => { calls.ping++; return { os: "win" }; }, getURL: (p) => `chrome-extension://${EXT_ID}/${p}`, onStartup: event("startup"), onInstalled: event("installed"), onMessage: event("message"), onConnect: event("connect") },
    tabs: {
      onActivated: event("activated"),
      onUpdated: event("updated"),
      query: async (q) => {
        calls.query.push(q);
        const w = windows[q.windowId];
        return w && !w.hidden && w.tab ? [{ windowId: q.windowId, active: true, incognito: false, ...w.tab }] : [];
      },
    },
    windows: {
      WINDOW_ID_NONE: NONE,
      onFocusChanged: event("focus"),
      get: async (id) => {
        calls.get.push(id);
        const w = windows[id];
        if (!w || w.hidden) throw new Error(`No window with id: ${id}.`);
        return { id, focused: w.focused, incognito: w.incognito ?? false };
      },
      getLastFocused: async () => {
        calls.getLastFocused++;
        const w = windows[lastFocused];
        if (!w || w.hidden) throw new Error("No last focused window");
        return { id: lastFocused, focused: w.focused, incognito: w.incognito ?? false };
      },
    },
  };
  const posted = [];
  globalThis.fetch = async (url, init) => {
    if (init?.method === "POST") {
      posted.push({ url, headers: init.headers, body: JSON.parse(init.body) });
      return { ok: true, status: 200, json: async () => ({ ok: true }) };
    }
    return {
      ok: true,
      json: async () => (recording ? { consented: true, trackingEnabled: true, consentVersion: 1 } : { consented: false, trackingEnabled: false, consentVersion: 1 }),
    };
  };

  await import(`../background.js?case=${++loads}`);

  const api = {
    calls,
    timers,
    setRecording(value) { recording = value; },
    /** Moves the worker's clock forward (the agent's answer is cached for 10 s). */
    advance(ms) { clock.offset += ms; },
    listeners,
    /** Every POST /events the worker made. */
    posted,
    /** What the popup would show. */
    async summary(sender = { id: EXT_ID, url: POPUP_URL }, message = { type: "dla-summary" }) {
      let answered;
      const keptOpen = listeners.message(message, sender, (reply) => { answered = reply; });
      if (keptOpen === true) await flush();
      return { keptOpen, answered };
    },
    /** Moves to a window / tab, the way the browser would announce it. */
    async focus(windowId, tab) {
      for (const w of Object.values(windows)) w.focused = false;
      if (windowId !== NONE) {
        windows[windowId].focused = true;
        lastFocused = windowId;
        if (tab) windows[windowId].tab = tab;
      }
      listeners.focus(windowId);
      await flush();
    },
    async navigate(windowId, tab, changeInfo = { url: tab.url }) {
      windows[windowId].tab = tab;
      listeners.updated(tab.id, changeInfo, { ...tab, active: tab.active ?? true, windowId });
      await flush();
    },
    async count() {
      const { answered } = await api.summary();
      return answered;
    },
    /**
     * Opens the extension popup the way a browser does: the popup page connects a port. Returns { close }.
     * `sender` and `name` can be changed to model a connection that is not our popup.
     */
    async openPopup({ name = "dla-popup", sender = { id: EXT_ID, url: POPUP_URL } } = {}) {
      let onDisconnect;
      const port = { name, sender, onDisconnect: { addListener: (fn) => { onDisconnect = fn; } } };
      listeners.connect(port);
      await flush();
      return { async close() { onDisconnect?.(); await flush(); } };
    },
  };
  listeners.startup(); // the browser starts the worker: the tab in front becomes the first open period
  await flush();
  return api;
}

const tab = (id, url, extra = {}) => ({ id, url, title: "Title", active: true, incognito: false, ...extra });

// ---- wiring --------------------------------------------------------------------------------------------------------

test("it listens for tab activation, page updates, window focus, start-up and popup messages", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  for (const name of ["activated", "updated", "focus", "startup", "installed", "message", "connect"]) assert.equal(typeof bg.listeners[name], "function", name);
});

// ---- active tab ----------------------------------------------------------------------------------------------------

test("it asks only for the active tab of the window it was told about", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") }, 2: { focused: false, tab: tab(2, "https://b.example.com/") } });
  bg.calls.query.length = 0;
  bg.listeners.activated({ tabId: 1, windowId: 1 });
  await flush();
  assert.deepEqual(bg.calls.query, [{ active: true, windowId: 1 }]);
});

test("the window that has focus decides: a tab change in an unfocused window ends the period instead of recording that tab", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") }, 2: { focused: false, tab: tab(2, "https://b.example.com/") } });
  bg.listeners.activated({ tabId: 2, windowId: 2 }); // window 2 does not have focus
  await flush();
  assert.equal((await bg.count()).eventCount, 1); // a.example.com ended; b was not started
  await bg.focus(1);
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 2); // only a's two periods; b never became an event
});

test("a page update in a background tab is ignored: no window or tab lookup at all", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  bg.calls.query.length = 0; bg.calls.get.length = 0;
  bg.listeners.updated(9, { url: "https://x.example.com/" }, tab(9, "https://x.example.com/", { active: false, windowId: 1 }));
  await flush();
  assert.deepEqual([bg.calls.query.length, bg.calls.get.length], [0, 0]);
});

test("only a new address or title in the front tab triggers a look (loading status alone does not)", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  bg.calls.query.length = 0;
  bg.listeners.updated(1, { status: "loading" }, tab(1, "https://a.example.com/", { windowId: 1 }));
  bg.listeners.updated(1, { audible: true }, tab(1, "https://a.example.com/", { windowId: 1 }));
  await flush();
  assert.equal(bg.calls.query.length, 0);
  bg.listeners.updated(1, { title: "New title" }, tab(1, "https://a.example.com/", { windowId: 1 }));
  await flush();
  assert.equal(bg.calls.query.length, 1);
});

test("losing browser focus (WINDOW_ID_NONE) ends the period without asking the browser for anything", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  bg.calls.get.length = 0; bg.calls.query.length = 0;
  await bg.focus(NONE);
  assert.deepEqual([bg.calls.get.length, bg.calls.query.length], [0, 0]);
  assert.equal((await bg.count()).eventCount, 1);
});

test("a window that exists but does not have focus is not recorded", async () => {
  const bg = await load({ 1: { focused: false, tab: tab(1, "https://a.example.com/") } });
  bg.listeners.focus(1);
  await flush();
  assert.equal((await bg.count()).eventCount, 0);
});

// ---- private windows -------------------------------------------------------------------------------------------------

test("a private window the browser hides from the extension: nothing is recorded and the normal page just ends", async () => {
  const bg = await load({
    1: { focused: true, tab: tab(1, "https://a.example.com/") },
    7: { focused: false, hidden: true, tab: tab(7, "https://private-site.example.org/secret", { incognito: true }) },
  });
  await bg.focus(7);                       // focus moves into the private window
  assert.equal((await bg.count()).eventCount, 1); // +1: the normal page ended
  bg.listeners.activated({ tabId: 7, windowId: 7 });                                     // events from inside it
  bg.listeners.updated(7, { url: "https://x.example.org/" }, tab(7, "https://x.example.org/", { windowId: 7, incognito: true }));
  await flush();
  assert.equal((await bg.count()).eventCount, 1); // still 1: the private window created nothing
  await bg.focus(1, tab(2, "https://second.example.net/"));
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 2); // only the two normal pages
});

test("even if a private window is reported with focus, the extension refuses it", async () => {
  const bg = await load({
    1: { focused: true, tab: tab(1, "https://a.example.com/") },
    7: { focused: false, incognito: true, tab: tab(7, "https://private-site.example.org/", { incognito: true }) },
  });
  await bg.focus(7);
  bg.listeners.activated({ tabId: 7, windowId: 7 });
  await flush();
  assert.equal((await bg.count()).eventCount, 1);
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 1); // the private window was never opened as a period
});

test("a window flagged private is refused even if its tab is not flagged", async () => {
  const bg = await load({
    1: { focused: true, tab: tab(1, "https://a.example.com/") },
    7: { focused: false, incognito: true, tab: tab(7, "https://private-site.example.org/", { incognito: false }) },
  });
  await bg.focus(7);
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 1); // only the normal page; the flagged window never became a period
});

test("a private TAB in a window reported as normal is refused too", async () => {
  const bg = await load({ 1: { focused: true, incognito: false, tab: tab(1, "https://private-site.example.org/", { incognito: true }) } });
  bg.listeners.focus(1);
  await flush();
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 0);
});

test("the last-focused fallback (start-up) also refuses a private window", async () => {
  const bg = await load({ 7: { focused: true, incognito: true, tab: tab(7, "https://private-site.example.org/", { incognito: true }) } });
  bg.listeners.startup();
  await flush();
  assert.ok(bg.calls.getLastFocused > 0);
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 0);
});

// ---- internal pages ----------------------------------------------------------------------------------------------------

test("normal -> internal pages -> normal through the real listeners: only normal pages count", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  assert.equal((await bg.count()).eventCount, 0);
  await bg.navigate(1, tab(1, "edge://settings"));
  assert.equal((await bg.count()).eventCount, 1);        // +1: the normal page ended
  for (const url of ["edge://newtab/", "chrome://settings", "about:blank", "", "file:///C:/x.txt", "view-source:https://example.com"])
    await bg.navigate(1, tab(1, url, { title: "x" }), { url, title: "x" });
  assert.equal((await bg.count()).eventCount, 1);        // internal pages add nothing
  await bg.navigate(1, tab(1, "https://second.example.net/b"));
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 2);
});

// ---- what the popup count means ---------------------------------------------------------------------------------------

test("the popup count is the number of finished periods, not of tab switches or pages visited", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  assert.equal((await bg.count()).eventCount, 0);                  // the page you are on is not counted
  await bg.navigate(1, tab(1, "https://a.example.com/", { title: "New title" }), { title: "New title" });
  assert.equal((await bg.count()).eventCount, 0);                  // a title change is not a new period
  await bg.navigate(1, tab(1, "https://b.example.com/"));
  assert.equal((await bg.count()).eventCount, 1);
  await bg.navigate(1, tab(1, "https://c.example.com/"));
  assert.equal((await bg.count()).eventCount, 2);
});

test("without consent or while paused, nothing is recorded and the popup shows no count", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } }, { recording: false });
  await bg.navigate(1, tab(1, "https://b.example.com/"));
  await bg.focus(NONE);
  assert.deepEqual(await bg.count(), { recording: false, eventCount: 0 });
});

// ---- who may ask -----------------------------------------------------------------------------------------------------

test("only the extension's own popup page gets an answer", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  for (const [sender, message] of [
    [{ id: "someotherextensionidxxxxxxxxxxxx", url: POPUP_URL }, { type: "dla-summary" }],
    [{ id: EXT_ID, url: `chrome-extension://${EXT_ID}/other.html` }, { type: "dla-summary" }],
    [{ id: EXT_ID, url: "https://evil.example/" }, { type: "dla-summary" }],
    [{ id: EXT_ID, url: POPUP_URL }, { type: "something-else" }],
    [{ id: EXT_ID, url: POPUP_URL }, null],
  ]) {
    const result = await bg.summary(sender, message);
    assert.equal(result.keptOpen, false);
    assert.equal(result.answered, undefined);
  }
});

test("the summary holds only a flag and a number, never an address or a title", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/secret-path?token=1", { title: "Private-looking title" }) } });
  await bg.navigate(1, tab(1, "https://b.example.com/"));
  const { answered } = await bg.summary();
  assert.deepEqual(Object.keys(answered).sort(), ["eventCount", "recording"]);
  assert.ok(!JSON.stringify(answered).includes("example"));
});

// ---- the popup must only read: opening and closing it must not end a page's period ------------------------------------
//
// Cause being guarded: opening the extension popup takes the focus from the browser window. The browser reports that as
// windows.onFocusChanged(WINDOW_ID_NONE), and closing it reports the window again. Before the fix the worker read the
// first as "browser left", ended the period (+1 event), and the second started a new period, so every open/close added one.

/** What a browser does when the popup opens, then closes, on the page that is in front in window 1. */
async function popupCycle(bg, { windowId = 1 } = {}) {
  await bg.focus(NONE);                       // the popup takes the focus
  const popup = await bg.openPopup();         // the popup page loads and connects
  const shown = await bg.count();             // and asks for the count
  await bg.focus(windowId);                   // the popup closes, the browser window has the focus again
  await popup.close();
  return shown;
}

test("opening and closing the popup again and again on the same page does not change the count", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  for (let i = 0; i < 6; i++) {
    const shown = await popupCycle(bg);
    assert.equal(shown.eventCount, 0, `while the popup is open (cycle ${i + 1})`);
    assert.equal((await bg.count()).eventCount, 0, `after it closed (cycle ${i + 1})`);
  }
});

test("the same holds when the popup connects before the focus event arrives", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  for (let i = 0; i < 4; i++) {
    const popup = await bg.openPopup();
    await bg.focus(NONE);
    assert.equal((await bg.count()).eventCount, 0);
    await bg.focus(1);
    await popup.close();
  }
  assert.equal((await bg.count()).eventCount, 0);
});

test("the same holds if the browser names some window with no tab (such as the popup itself) instead of 'no window'", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") }, 5: { focused: false, tab: null } });
  for (let i = 0; i < 3; i++) {
    await bg.focus(5);
    const popup = await bg.openPopup();
    assert.equal((await bg.count()).eventCount, 0);
    await bg.focus(1);
    await popup.close();
  }
  assert.equal((await bg.count()).eventCount, 0);
});

test("after any number of popup clicks, genuine changes still finish periods one by one", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  await popupCycle(bg);
  await popupCycle(bg);
  assert.equal((await bg.count()).eventCount, 0);

  await bg.navigate(1, tab(1, "https://b.example.com/"));          // a new page: a.example.com ends
  assert.equal((await bg.count()).eventCount, 1);
  await popupCycle(bg);
  assert.equal((await bg.count()).eventCount, 1);                  // the popup adds nothing

  bg.listeners.activated({ tabId: 2, windowId: 1 });               // another tab in the same window
  await bg.navigate(1, tab(2, "https://c.example.com/"));          // b.example.com ends
  assert.equal((await bg.count()).eventCount, 2);
  await popupCycle(bg);
  assert.equal((await bg.count()).eventCount, 2);

  await bg.focus(NONE);                                            // the user really leaves the browser: c ends
  assert.equal((await bg.count()).eventCount, 3);
  await bg.focus(1);                                               // comes back: a new period starts
  await popupCycle(bg);
  assert.equal((await bg.count()).eventCount, 3);
});

test("leaving the browser without the popup still ends the period, every time", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 1);
  await bg.focus(1);
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 2);
});

test("leaving the browser after the popup was closed still ends the period", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  await popupCycle(bg);
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 1);
});

test("a page change while the popup is open still ends the period", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  const popup = await bg.openPopup();
  await bg.navigate(1, tab(1, "https://b.example.com/"));
  assert.equal((await bg.count()).eventCount, 1);
  await popup.close();
});

test("a connection that is not our popup page does not stop periods from ending", async () => {
  const cases = [
    { name: "something-else" },
    { sender: { id: "someotherextensionidxxxxxxxxxxxx", url: POPUP_URL } },
    { sender: { id: EXT_ID, url: `chrome-extension://${EXT_ID}/other.html` } },
    { sender: { id: EXT_ID, url: "https://evil.example/" } },
    { sender: null },
  ];
  for (const options of cases) {
    const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
    await bg.focus(NONE);
    await bg.openPopup(options);
    assert.equal((await bg.count()).eventCount, 1, JSON.stringify(options));   // the loss stands
    await bg.focus(1);
    await bg.focus(NONE);
    assert.equal((await bg.count()).eventCount, 2, JSON.stringify(options));   // and later losses are not ignored
  }
});

test("private windows and internal pages stay excluded around popup clicks", async () => {
  const bg = await load({
    1: { focused: true, tab: tab(1, "https://a.example.com/") },
    7: { focused: false, hidden: true, tab: tab(7, "https://private-site.example.org/", { incognito: true }) },
  });
  await popupCycle(bg);
  await bg.focus(7);                                     // into a private window the extension cannot see
  assert.equal((await bg.count()).eventCount, 1);        // a.example.com ended
  const popup = await bg.openPopup();                    // the popup is opened while a private window is in front
  bg.listeners.updated(7, { url: "https://x.example.org/" }, tab(7, "https://x.example.org/", { windowId: 7, incognito: true }));
  await flush();
  await popup.close();
  assert.equal((await bg.count()).eventCount, 1);        // nothing from the private window

  await bg.focus(1, tab(1, "https://b.example.com/"));
  await bg.navigate(1, tab(1, "edge://settings"));       // an internal page
  assert.equal((await bg.count()).eventCount, 2);
  await popupCycle(bg);                                  // popup on an internal page: nothing open, nothing to undo
  await bg.navigate(1, tab(1, "https://c.example.com/"));
  await bg.focus(NONE);
  assert.equal((await bg.count()).eventCount, 3);        // only a, b, c became events
});

test("the popup connects before it asks for the count, using the agreed port name", async () => {
  const { readFileSync } = await import("node:fs");
  const source = readFileSync(new URL("../popup.js", import.meta.url), "utf8");
  const connectAt = source.indexOf('chrome.runtime.connect({ name: "dla-popup" })');
  const askAt = source.indexOf("chrome.runtime.sendMessage");
  assert.ok(connectAt > 0 && askAt > connectAt);
  assert.ok(!/storage|fetch\(|XMLHttpRequest|postMessage/.test(source));   // it only reads: no storage, no network, no extra messages
});

// ---- delivery to the agent (POST /events) -----------------------------------------------------------------------------
// These follow a closed period all the way to the request the browser would send. They exist because a bug once stopped
// every send (the first, empty drain at start-up blocked all later ones) while each part passed its own unit tests.

test("a finished page is POSTed to the agent: YouTube -> Google -> GitHub sends the first two at once", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://www.youtube.com/watch?v=abc", { title: "Video" }) } });
  await bg.navigate(1, tab(1, "https://www.google.com/search?q=secret#frag", { title: "Google" }));
  await bg.navigate(1, tab(1, "https://github.com/aleeza/repo", { title: "Repo" }));
  assert.equal(bg.posted.length, 2);
  assert.deepEqual(bg.posted.map((p) => p.body.domain), ["youtube.com", "google.com"]);
  for (const p of bg.posted) {
    assert.equal(p.url, "http://127.0.0.1:49721/events");
    assert.equal(p.headers["X-DLA-Extension-Id"], EXT_ID);
    assert.equal(p.body.source, "extension");
    assert.ok(!("app" in p.body) && !("presence" in p.body), "no desktop-application field");
    assert.ok(!("idle" in p.body) && !("browser_idle" in p.body));
    assert.ok(!/[?#]/.test(p.body.url), "no query or fragment is ever sent");
  }
});

test("switching to another tab sends the page that was left", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  bg.listeners.activated({ tabId: 2, windowId: 1 });
  bg.calls.query.length = 0;
  await bg.navigate(1, tab(2, "https://b.example.com/"));
  assert.deepEqual(bg.posted.map((p) => p.body.domain), ["example.com"]);
  assert.equal(bg.posted[0].body.url, "https://a.example.com");
});

test("nothing is POSTed while the agent says no consent", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } }, { recording: false });
  await bg.navigate(1, tab(1, "https://b.example.com/"));
  assert.equal(bg.posted.length, 0);
});

test("a page that was only open during an empty start-up check is still sent later (regression: stuck 'running' flag)", async () => {
  const bg = await load({ 1: { focused: true, tab: tab(1, "https://a.example.com/") } });
  // start-up already ran one drain with nothing to send; sending must still work afterwards, many times
  for (let i = 0; i < 4; i++) await bg.navigate(1, tab(1, `https://site${i}.example.com/`));
  assert.equal(bg.posted.length, 4);
});

// ---- content counting through the real service worker ------------------------------------------------------------------------

test("through the worker: Reel A, Reel B, a tab switch and back to Reel B send 1, 1, 0, 0 as content_count", async () => {
  const A = "https://www.instagram.com/reel/C8aBcDeFgHi/";
  const B = "https://www.instagram.com/reel/D1xYzWvUtSr/";
  const api = await load({ 1: { focused: true, tab: { id: 1, url: A, title: "Instagram" } } });
  await api.navigate(1, { id: 1, url: A, title: "Instagram" });
  api.advance(4000);                                                      // Reel A is a real stay, not a transition on arrival
  await api.navigate(1, { id: 1, url: B, title: "Instagram" });
  await api.navigate(1, { id: 1, url: "https://example.com/", title: "Example" });
  await api.navigate(1, { id: 1, url: B, title: "Instagram" });
  await api.navigate(1, { id: 1, url: "https://example.com/other", title: "Other" });
  assert.deepEqual(api.posted.map((p) => [p.body.activity_type, p.body.content_count]), [["reel", 1], ["reel", 1], ["webpage", 0], ["reel", 0]]);
  assert.ok(api.posted.every((p) => p.body.session_id === null && !("content_context" in p.body)));
});

test("through the worker: the page re-rendering (title flapping, a query added) does not send extra events", async () => {
  const A = "https://www.instagram.com/reel/C8aBcDeFgHi/";
  const api = await load({ 1: { focused: true, tab: { id: 1, url: A, title: "Instagram" } } });
  await api.navigate(1, { id: 1, url: A, title: "Instagram" });
  await api.navigate(1, { id: 1, url: A, title: "(1) Instagram" }, { title: "(1) Instagram" });
  await api.navigate(1, { id: 1, url: `${A}?igsh=abc`, title: "Instagram" });
  api.advance(4000);
  await api.navigate(1, { id: 1, url: "https://example.com/", title: "Example" });
  assert.deepEqual(api.posted.map((p) => [p.body.activity_type, p.body.content_count]), [["reel", 1]]);
});

// ---- the keep-alive in the real worker ---------------------------------------------------------------------------------------

const VIDEO_A = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
const VIDEO_B = "https://www.youtube.com/watch?v=9bZkp7q19f0";
const ytTab = (url, title = "Video - YouTube") => ({ id: 1, url, title });

test("keep-alive: nothing runs while no period is open; it starts when one opens, at a 20 second interval", async () => {
  const api = await load({ 1: { focused: false, tab: ytTab(VIDEO_A) } });   // the browser is not in front: no period yet
  assert.equal(api.timers.length, 0);
  await api.focus(1, ytTab(VIDEO_A));                                       // it comes to the front: a period opens
  assert.equal(api.timers.length, 1);
  assert.equal(api.timers[0].ms, 20_000);
  assert.equal(api.timers[0].cleared, false);
  await api.navigate(1, ytTab(VIDEO_A, "Video - YouTube (2)"));             // more events do not start a second timer
  assert.equal(api.timers.length, 1);
});

test("keep-alive: it does not start when recording is not allowed", async () => {
  const api = await load({ 1: { focused: true, tab: ytTab(VIDEO_A) } }, { recording: false });
  await api.navigate(1, ytTab(VIDEO_A));
  assert.equal(api.timers.length, 0);
});

test("keep-alive: each tick makes only the cheap extension call, and nothing else happens", async () => {
  const api = await load({ 1: { focused: true, tab: ytTab(VIDEO_A) } });
  await api.navigate(1, ytTab(VIDEO_A));
  const queriesBefore = api.calls.query.length;
  const sentBefore = api.posted.length;
  for (let i = 1; i <= 3; i++) {
    await api.timers[0].fn();
    await flush();
    assert.equal(api.calls.ping, i);
  }
  assert.equal(api.calls.query.length, queriesBefore, "a tick reads no tab");
  assert.equal(api.posted.length, sentBefore, "a tick sends no event");
});

test("keep-alive: it stops by itself when recording stops", async () => {
  const api = await load({ 1: { focused: true, tab: ytTab(VIDEO_A) } });
  await api.navigate(1, ytTab(VIDEO_A));
  api.setRecording(false);
  api.advance(11_000);                        // the agent's answer is cached for 10 s: let it expire
  await api.timers[0].fn();
  await flush();
  assert.equal(api.timers[0].cleared, true);
  assert.equal(api.calls.ping, 0, "no call is made once recording has stopped");
});

test("keep-alive: it stops when no period is open (the browser lost focus), and starts again for the next period", async () => {
  const api = await load({ 1: { focused: true, tab: ytTab(VIDEO_A) } });
  await api.navigate(1, ytTab(VIDEO_A));
  await api.focus(-1);                                        // the browser lost focus: the period is closed
  assert.equal(api.timers[0].cleared, true);
  await api.focus(1, ytTab(VIDEO_B));
  assert.equal(api.timers.length, 2, "a new period starts a new keep-alive");
  assert.equal(api.timers[1].cleared, false);
});

test("keep-alive: a long viewing with ticks keeps the memory, so video A and video B are each counted once and returning to B is not counted", async () => {
  const api = await load({ 1: { focused: true, tab: ytTab(VIDEO_A) } });
  await api.navigate(1, ytTab(VIDEO_A));
  for (let i = 0; i < 4; i++) { await api.timers[0].fn(); await flush(); }            // about 80 s on the same video, no events from the page
  await api.navigate(1, ytTab(VIDEO_A, "Video - YouTube (1)"));                         // a re-render of the same video
  await api.navigate(1, ytTab(VIDEO_B, "Little Mix - Touch - YouTube"));                 // a different video
  for (let i = 0; i < 3; i++) { await api.timers[0].fn(); await flush(); }
  await api.navigate(1, { id: 1, url: "https://example.com/", title: "Example" });       // leave
  await api.navigate(1, ytTab(VIDEO_B, "Little Mix - Touch - YouTube"));                 // come back to the same video
  await api.navigate(1, { id: 1, url: "https://example.com/other", title: "Other" });
  assert.deepEqual(api.posted.map((p) => [p.body.activity_type, p.body.content_count]), [["video", 1], ["video", 1], ["webpage", 0], ["video", 0]]);
  assert.ok(!JSON.stringify(api.posted).includes("dQw4w9WgXcQ") && !JSON.stringify(api.posted).includes("9bZkp7q19f0"), "no video id is sent");
});

test("key diagnostic in the real worker: one shape-only line per Reel code change, and the counting is exactly as before", async () => {
  const REEL_X = "https://www.instagram.com/reels/3712345678901234567/";
  const REEL_Y = "https://www.instagram.com/reels/DeT_HthytfP/";
  const lines = [];
  const realInfo = console.info;
  console.info = (...a) => lines.push(a.join(" "));
  try {
    const api = await load({ 1: { focused: true, tab: ytTab(REEL_X, "Instagram") } });
    await api.navigate(1, ytTab(REEL_X, "Instagram"));
    api.advance(4000);                                                      // Reel X is a real stay, not a transition on arrival
    await api.navigate(1, ytTab(REEL_Y, "Instagram"));
    await api.navigate(1, ytTab(REEL_Y, "(1) Instagram"));
    await api.navigate(1, { id: 1, url: "https://example.com/", title: "Example" });
    const keyLines = lines.filter((l) => l.includes("Instagram key change"));
    assert.equal(keyLines.length, 2);
    assert.match(keyLines[0], /^\[DLA-KEY \d\d:\d\d:\d\d\] Instagram key change: type=digits len=19 hash=[0-9a-z]{4}$/);
    assert.match(keyLines[1], /^\[DLA-KEY \d\d:\d\d:\d\d\] Instagram key change: type=mixed len=11 hash=[0-9a-z]{4}$/);
    const printed = lines.join("\n");
    for (const secret of ["3712345678901234567", "DeT_HthytfP", "instagram.com/reels"]) assert.ok(!printed.includes(secret), secret);
    // the diagnostic did not touch the events or the counts: two Reels, each counted once
    assert.deepEqual(api.posted.map((p) => [p.body.activity_type, p.body.content_count]), [["reel", 1], ["reel", 1]]);
    assert.ok(api.posted.every((p) => !JSON.stringify(p.body).includes("3712345678901234567") && !JSON.stringify(p.body).includes("DeT_HthytfP")), "no Reel code is stored in any event");
  } finally {
    console.info = realInfo;
  }
});
