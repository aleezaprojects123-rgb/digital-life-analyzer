// Keeps the MV3 service worker alive while a period is being recorded.
//
// Chrome stops an idle extension service worker after about 30 seconds without extension events or extension API calls.
// A video that just plays produces no browser events, so the worker would be stopped in the middle of watching it, and the
// open period (kept in memory only) and the memory of which item was counted would be lost.
//
// While running, this makes ONE cheap extension API call every 20 seconds (chrome.runtime.getPlatformInfo: it returns the operating
// system name, reads nothing from any page and sends nothing anywhere). Any extension API call restarts Chrome's idle clock.
// It runs only while recording is allowed AND a period is open; every tick re-checks that and stops itself otherwise.
// No storage, no content, no ids.

export const KEEP_ALIVE_MS = 20_000;

/**
 * @param {object} deps
 * @param {() => Promise<boolean>} deps.shouldRun   true while recording is allowed and there is an open period to protect
 * @param {() => Promise<unknown>} deps.ping        the cheap extension API call
 * @param {number} [deps.intervalMs]
 */
export function createKeepAlive({
  shouldRun,
  ping,
  intervalMs = KEEP_ALIVE_MS,
  setIntervalFn = (...a) => setInterval(...a),
  clearIntervalFn = (...a) => clearInterval(...a),
}) {
  let timer = null;

  function stop() {
    if (timer === null) return;
    clearIntervalFn(timer);
    timer = null;
  }

  async function tick() {
    let run = false;
    try { run = await shouldRun(); } catch { /* cannot tell: stop */ }
    if (!run) { stop(); return; }
    try { await ping(); } catch { /* a failed ping must never break recording */ }
  }

  function start() {
    if (timer !== null) return;
    timer = setIntervalFn(tick, intervalMs);
    timer?.unref?.(); // Node only (tests)
  }

  return { start, stop, tick, get running() { return timer !== null; } };
}
