// Link to the desktop agent (PLACEHOLDER).
//
// Nothing is sent anywhere in Phase 0. The real transport (Chrome native messaging registered per user in HKCU,
// or a localhost link) is decided in Phase 1 and will need its own manifest permission at that time.
//
// What a heartbeat may contain is deliberately tiny: a timestamp and the extension version. Never page content,
// URLs, titles or keystrokes in this message.

/**
 * @param {{ts: string, version: string}} heartbeat
 * @returns {Promise<{sent: boolean, reason: string}>}
 */
export async function sendHeartbeat(heartbeat) {
  void heartbeat;
  return { sent: false, reason: 'agent link not implemented yet (Phase 0 placeholder)' };
}
