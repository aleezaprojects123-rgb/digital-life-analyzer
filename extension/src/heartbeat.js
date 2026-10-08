// One heartbeat tick. Pure logic with injected dependencies so it can be unit-tested without a browser.

/**
 * @param {{
 *   isConsentGranted: () => Promise<boolean>,
 *   send: (hb: {ts: string, version: string}) => Promise<{sent: boolean, reason: string}>,
 *   log: {log: Function, debug: Function},
 *   now: () => Date,
 *   version: string,
 *   reason: string
 * }} deps
 * @returns {Promise<'inactive' | 'beat'>}
 */
export async function runHeartbeat({ isConsentGranted, send, log, now, version, reason }) {
  if (!(await isConsentGranted())) {
    log.debug(`[DLA] inactive: no consent from the agent yet (${reason})`);
    return 'inactive';
  }
  const ts = now().toISOString();
  const result = await send({ ts, version });
  log.log(`[DLA] heartbeat ${ts} (${reason}) -> ${result.sent ? 'sent' : 'not sent: ' + result.reason}`);
  return 'beat';
}
