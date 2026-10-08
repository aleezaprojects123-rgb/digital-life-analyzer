// Consent gate (STUB).
//
// The desktop agent owns the real consent record. Later the agent will tell the extension whether recording is
// allowed (over the agent link). Until then this flag, kept in chrome.storage.local, stands in for that message.
// Default is false: without consent the extension stays inactive and sends nothing.
//
// To switch the stub on while testing, run this in the extension's service-worker console:
//   chrome.storage.local.set({ 'dla.consentGranted': true })
// and to switch it off again:
//   chrome.storage.local.set({ 'dla.consentGranted': false })

export const CONSENT_KEY = 'dla.consentGranted';

/** @param {{get: (defaults: object) => Promise<object>}} storage */
export async function isConsentGranted(storage = chrome.storage.local) {
  const result = await storage.get({ [CONSENT_KEY]: false });
  return result[CONSENT_KEY] === true;
}
