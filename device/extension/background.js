// The service worker. It connects the browser's tab events to the TabLogger.
//
// It records nothing unless the Windows agent says (GET /status) that the user consented AND tracking is on.
// No content scripts, no page content is read, no network except /status, no storage, no timers or alarms.
// Events live in memory only. Chrome may stop an idle service worker, and then they are gone (see README).

import { createStatusClient } from "./agent-status.js";
import { TabLogger } from "./tab-logger.js";

const status = createStatusClient();
const logger = new TabLogger({ getStatus: () => status.get() });

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
function refresh(windowIdHint) {
  const at = Date.now();
  queue = queue
    .then(async () => logger.update(await frontTab(windowIdHint), at))
    .catch(() => { /* a failed refresh must never break the next one */ });
}

// Listeners are registered at the top level so a restarted service worker is woken by them.
chrome.tabs.onActivated.addListener((info) => refresh(info.windowId));
chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
  // A page change (including single-page-app navigation) or a new title in the tab that is in front.
  if (tab.active && (change.url !== undefined || change.title !== undefined)) refresh(tab.windowId);
});
chrome.windows.onFocusChanged.addListener((windowId) => refresh(windowId));
chrome.runtime.onStartup.addListener(() => refresh());
chrome.runtime.onInstalled.addListener(() => refresh());

// The popup asks how many events there are. Only the extension's own popup page is answered.
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL("popup.html") || message?.type !== "dla-summary") return false;
  logger.summary().then(sendResponse, () => sendResponse({ recording: false, eventCount: 0 }));
  return true;
});
