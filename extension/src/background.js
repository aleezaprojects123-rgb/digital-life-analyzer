// Service worker (Manifest V3). Runs whenever the browser runs: Chrome/Edge start it at browser start-up and
// wake it from the alarm below, so no page, popup or user action is needed.
//
// It reads no page content, no URLs, no tab titles and no keystrokes (the manifest has no host permissions,
// no content scripts and no "tabs" permission). Phase 0 only logs a heartbeat.

import { isConsentGranted } from './consent.js';
import { sendHeartbeat } from './agentLink.js';
import { runHeartbeat } from './heartbeat.js';

const ALARM_NAME = 'dla-heartbeat';
const PERIOD_MINUTES = 0.5; // 30 s, the minimum Chrome allows for alarms

async function ensureAlarm() {
  // Alarms are not guaranteed to survive a browser restart, so (re)create it whenever the worker starts.
  if (!(await chrome.alarms.get(ALARM_NAME))) {
    await chrome.alarms.create(ALARM_NAME, { periodInMinutes: PERIOD_MINUTES });
  }
}

function beat(reason) {
  return runHeartbeat({
    isConsentGranted,
    send: sendHeartbeat,
    log: console,
    now: () => new Date(),
    version: chrome.runtime.getManifest().version,
    reason,
  });
}

// Listeners must be registered synchronously at the top level so the browser can wake the worker for them.
chrome.runtime.onStartup.addListener(() => { ensureAlarm(); beat('browser-start'); });
chrome.runtime.onInstalled.addListener(() => { ensureAlarm(); beat('installed'); });
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM_NAME) beat('alarm');
});

// Covers the worker being restarted by the browser without a start-up/installed event.
ensureAlarm();
