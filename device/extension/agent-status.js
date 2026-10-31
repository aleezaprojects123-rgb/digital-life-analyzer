// Asks the Windows agent: "may I record right now?" (GET /status on 127.0.0.1)
// and sends finished browser activity events (POST /events on 127.0.0.1).
//
// These are the ONLY network requests the extension makes.
// Anything other than a clear "consented: true AND trackingEnabled: true" means NO recording.

export const STATUS_URL = "http://127.0.0.1:49721/status";
export const EVENTS_URL = "http://127.0.0.1:49721/events";
export const CACHE_MS = 10_000; // must stay at or under 15 seconds
const TIMEOUT_MS = 2_000;

/** @typedef {{ recording: boolean, noConsent: boolean }} RecordingStatus
 *  recording: true only if the user consented and tracking is not paused.
 *  noConsent: true only if the agent clearly said that no consent is on record (consentVersion 0). */

const STOPPED = Object.freeze({ recording: false, noConsent: false });

export function parseStatus(body) {
  if (body === null || typeof body !== "object" || Array.isArray(body)) return STOPPED;
  const { consented, trackingEnabled, consentVersion } = body;
  if (typeof consented !== "boolean" || typeof trackingEnabled !== "boolean") return STOPPED;
  if (consented && trackingEnabled) return { recording: true, noConsent: false };
  return { recording: false, noConsent: consentVersion === 0 };
}

// The browser sends no Origin header for this request (an extension with a host permission counts as same-origin),
// so the extension names itself in a header instead. A web page cannot send this header without a CORS preflight,
// and the agent refuses preflights. It is an ID, not a secret; it only tells the agent which extension is asking.
export const ID_HEADER = "X-DLA-Extension-Id";

export function createStatusClient({
  fetchFn = (...a) => fetch(...a),
  now = Date.now,
  url = STATUS_URL,
  cacheMs = CACHE_MS,
  extensionId = globalThis.chrome?.runtime?.id,
} = {}) {
  let cached = null; // { at, value }
  let pending = null;

  async function ask() {
    try {
      const response = await fetchFn(url, {
        method: "GET",
        cache: "no-store",
        credentials: "omit",
        headers: extensionId ? { [ID_HEADER]: extensionId } : {},
        redirect: "error",
        signal: AbortSignal.timeout(TIMEOUT_MS),
      });
      if (!response.ok) return STOPPED;
      return parseStatus(await response.json());
    } catch {
      return STOPPED; // agent not running, or anything else: no consent
    }
  }

  return {
    /** @returns {Promise<RecordingStatus>} */
    async get() {
      if (cached && now() - cached.at < Math.min(cacheMs, 15_000)) return cached.value;
      pending ??= ask().finally(() => { pending = null; });
      const value = await pending;
      cached = { at: now(), value };
      return value;
    },
  };
}

/**
 * Sends a single closed browser event to the Windows agent over the local link.
 */
export async function sendBrowserEvent(event, options = {}) {
  return (await postEvent(event, options)) === "sent";
}

/**
 * "sent"     the agent saved it (or already had it).
 * "rejected" the agent read it and said 400: this event can never be accepted, so retrying would block every later event.
 * "retry"    anything else (agent not running, 403 paused, 5xx, timeout): keep it and try again later.
 */
export async function postEvent(event, {
  fetchFn = (...a) => fetch(...a),
  url = EVENTS_URL,
  extensionId = globalThis.chrome?.runtime?.id,
} = {}) {
  try {
    const response = await fetchFn(url, {
      method: "POST",
      cache: "no-store",
      credentials: "omit",
      headers: {
        "Content-Type": "application/json",
        ...(extensionId ? { [ID_HEADER]: extensionId } : {}),
      },
      body: JSON.stringify(event),
      redirect: "error",
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    if (response.ok) return "sent";
    if (response.status === 400) {
      let code = "unknown";
      try { code = String((await response.json())?.error ?? code); } catch { /* no readable reason */ }
      console.warn(`[DLA] The agent rejected one browser event (${code}). It was dropped.`); // a reason code only, never page data
      return "rejected";
    }
    return "retry";
  } catch {
    return "retry";
  }
}

/**
 * Reliable event queue that holds events if the agent is temporarily offline or busy,
 * and drains them when tracking is permitted.
 */
export function createEventSender({
  fetchFn = (...a) => fetch(...a),
  url = EVENTS_URL,
  extensionId = globalThis.chrome?.runtime?.id,
  getStatus,
  maxQueue = 100,
} = {}) {
  const queue = [];
  let running = null;
  let rerun = false;

  async function drain() {
    if (running) {
      rerun = true;
      return running;
    }
    // `running` is cleared in .finally(), which runs after this assignment. (Clearing it inside the async function
    // is wrong: with an empty queue the function ends before the assignment, and `running` would stay set forever,
    // so nothing would ever be sent.)
    running = (async () => {
      try {
        do {
          rerun = false;
          while (queue.length > 0) {
            if (getStatus) {
              const st = await getStatus();
              if (st.noConsent) {
                queue.length = 0;
                break;
              }
              if (!st.recording) {
                break; // paused or unreachable: keep queued events until ready
              }
            }
            const item = queue[0];
            const outcome = await postEvent(item, { fetchFn, url, extensionId });
            if (outcome === "retry") {
              break; // agent busy or not there: try again at the next change
            }
            if (queue[0] === item) queue.shift(); // "sent" or "rejected" (never retried)
            else {
              const at = queue.indexOf(item);
              if (at >= 0) queue.splice(at, 1);
            }
          }
        } while (rerun);
      } catch {
        // never leak unhandled rejections
      }
    })().finally(() => { running = null; });
    return running;
  }

  return {
    send(event) {
      if (queue.length >= maxQueue) {
        queue.shift();
      }
      queue.push(event);
      drain().catch(() => {});
    },
    drain,
    get queueLength() {
      return queue.length;
    },
    clear() {
      queue.length = 0;
    },
  };
}
