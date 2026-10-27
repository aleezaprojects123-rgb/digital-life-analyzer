// Asks the Windows agent one question: "may I record right now?" (GET /status on 127.0.0.1).
//
// This is the ONLY network request the extension makes. It sends nothing about the user, and the answer is cached
// for a few seconds (never more than 15) so tab changes do not cause a request each time.
//
// Anything other than a clear "consented: true AND trackingEnabled: true" means NO recording:
// the agent not running, a wrong port, a slow answer, an HTTP error, bad JSON, a missing field, a field of the wrong type.

export const STATUS_URL = "http://127.0.0.1:49721/status";
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
