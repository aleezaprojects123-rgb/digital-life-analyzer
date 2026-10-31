// The service worker. It connects the browser's tab events to the TabLogger.
//
// It records nothing unless the Windows agent says (GET /status) that the user consented AND tracking is on.
// No content scripts, no page content is read, no network except /status and /events, no storage, no alarms.
// (Two timers only: a 3 second one-shot inside TabLogger that releases a period that ended because the browser lost focus, and the
// 20 second keep-alive in keep-alive.js, which runs only while a period is open and stops by itself otherwise.)
// A finished period is sent to the agent at once. A period that is still OPEN lives in memory only: Chrome may stop an
// idle service worker (about 30 seconds without browser events), and then that open period is gone (see README).

import { createStatusClient, createEventSender } from "./agent-status.js";
import { TabLogger } from "./tab-logger.js";
import { createKeepAlive } from "./keep-alive.js";
import { createKeyDiagnostics } from "./key-diag.js";

const status = createStatusClient();
const sender = createEventSender({ getStatus: () => status.get() });
const logger = new TabLogger({
  getStatus: () => status.get(),
  onEventClosed: (event) => sender.send(event),
  diag: (line) => console.info(`[DLA-EVENT] ${line}`), // console only: platform, activity type and yes/no flags, never content
});

// TEMP-DIAG: prints the SHAPE (digits or mixed, length, 4-character fingerprint) of an Instagram Reel code when it changes. Console only.
const keyDiag = createKeyDiagnostics((line) => console.info(line));

// Chrome stops an idle service worker after ~30 s without extension events, which would lose the open period (a video that just plays
// produces no events). While a period is open and recording is allowed, a cheap extension API call every 20 s keeps the worker alive.
const keepAlive = createKeepAlive({
  shouldRun: async () => (await status.get()).recording && logger.open !== null,
  ping: () => chrome.runtime.getPlatformInfo(),
});

/** The active tab of the focused window, or null when the browser is not in front. */
async function frontTab(windowIdHint) {
  if (windowIdHint === chrome.windows.WINDOW_ID_NONE) return null; // the browser lost focus
  let win;
  try {
    win = windowIdHint === undefined ? await chrome.windows.getLastFocused() : await chrome.windows.get(windowIdHint);
  } catch {
    return null;
  }
  if (!win.focused || win.incognito) return null;
  const [tab] = await chrome.tabs.query({ active: true, windowId: win.id });
  if (!tab || tab.incognito) return null;
  return { tabId: tab.id, url: tab.url, title: tab.title, incognito: false };
}

// One change after another, never two at once, so periods cannot overlap.
let queue = Promise.resolve();
function enqueue(task) {
  const run = queue.then(task);
  queue = run.catch(() => { /* a failed task must never break the next one */ });
  return run;
}

/**
 * `fromFocusChange`: the trigger was a window-focus event. If it leaves no tab in front, the logger is told it was a
 * focus loss, so that the extension's own popup (which takes the focus while it is open) does not end the period.
 */
function refresh(windowIdHint, fromFocusChange = false) {
  const at = Date.now();
  enqueue(async () => {
    const front = await frontTab(windowIdHint);
    if (front) keyDiag.observe(front.tabId, front.url);
    await logger.update(front, at, { focusLoss: fromFocusChange && front === null });
    if (logger.open !== null) keepAlive.start(); else keepAlive.stop(); // an open period exists only while recording is allowed
    await sender.drain();
  }).catch(() => { /* ignored: the next change starts from the logger's own state */ });
}

// Listeners are registered at the top level so a restarted service worker is woken by them.
chrome.tabs.onActivated.addListener((info) => refresh(info.windowId));
chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
  // A page change (including single-page-app navigation) or a new title in the tab that is in front.
  if (tab.active && (change.url !== undefined || change.title !== undefined)) refresh(tab.windowId);
});
chrome.windows.onFocusChanged.addListener((windowId) => refresh(windowId, true));
chrome.runtime.onStartup.addListener(() => refresh());
chrome.runtime.onInstalled.addListener(() => refresh());

const fromOwnPopup = (sender) => sender?.id === chrome.runtime.id && sender?.url === chrome.runtime.getURL("popup.html");

// The popup connects when it opens and disconnects when it closes. While it is open it holds the keyboard focus, and the
// browser reports that as "no window in front". Knowing the popup is open lets the logger keep the current period going.
chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== "dla-popup" || !fromOwnPopup(port.sender)) return; // only our own popup page counts
  enqueue(() => logger.popupOpened()).catch(() => {});
  port.onDisconnect.addListener(() => { enqueue(() => logger.popupClosed()).catch(() => {}); });
});

// The popup asks how many events there are. Only the extension's own popup page is answered.
// It goes through the same queue, so the answer comes after the popup's own connection has been handled.
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!fromOwnPopup(sender) || message?.type !== "dla-summary") return false;
  enqueue(() => logger.summary()).then(sendResponse, () => sendResponse({ recording: false, eventCount: 0 }));
  return true;
});
