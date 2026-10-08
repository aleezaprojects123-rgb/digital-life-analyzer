import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';

import { runHeartbeat } from '../src/heartbeat.js';
import { isConsentGranted, CONSENT_KEY } from '../src/consent.js';
import { sendHeartbeat } from '../src/agentLink.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const manifest = JSON.parse(readFileSync(join(root, 'manifest.json'), 'utf8'));

function fakeDeps(consent) {
  const sent = [];
  const logs = [];
  return {
    sent,
    logs,
    deps: {
      isConsentGranted: async () => consent,
      send: async (hb) => { sent.push(hb); return { sent: false, reason: 'placeholder' }; },
      log: { log: (m) => logs.push(m), debug: (m) => logs.push(m) },
      now: () => new Date('2026-10-09T10:00:00.000Z'),
      version: '0.0.1',
      reason: 'test',
    },
  };
}

test('without consent the extension stays inactive and sends nothing', async () => {
  const { deps, sent, logs } = fakeDeps(false);
  assert.equal(await runHeartbeat(deps), 'inactive');
  assert.equal(sent.length, 0);
  assert.match(logs[0], /inactive/);
});

test('with consent a heartbeat is logged and only timestamp + version are sent', async () => {
  const { deps, sent, logs } = fakeDeps(true);
  assert.equal(await runHeartbeat(deps), 'beat');
  assert.deepEqual(sent, [{ ts: '2026-10-09T10:00:00.000Z', version: '0.0.1' }]);
  assert.match(logs[0], /heartbeat 2026-10-09T10:00:00.000Z/);
});

test('consent flag defaults to false and is true only for an explicit true', async () => {
  const store = (value) => ({ get: async (d) => (value === undefined ? d : { [CONSENT_KEY]: value }) });
  assert.equal(await isConsentGranted(store(undefined)), false);
  assert.equal(await isConsentGranted(store(false)), false);
  assert.equal(await isConsentGranted(store('true')), false);
  assert.equal(await isConsentGranted(store(true)), true);
});

test('agent link is a placeholder that sends nothing', async () => {
  const r = await sendHeartbeat({ ts: 'x', version: '0.0.1' });
  assert.equal(r.sent, false);
});

test('manifest is MV3 and can never read pages, URLs, tabs or typing', () => {
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.content_scripts, undefined, 'no content scripts');
  assert.equal(manifest.host_permissions, undefined, 'no host permissions');
  assert.equal(manifest.optional_host_permissions, undefined);
  assert.equal(manifest.optional_permissions, undefined);
  assert.deepEqual([...manifest.permissions].sort(), ['alarms', 'storage']);
  for (const forbidden of ['tabs', 'activeTab', 'scripting', 'webNavigation', 'history', 'cookies', 'webRequest', 'nativeMessaging', 'debugger', 'clipboardRead'])
    assert.ok(!manifest.permissions.includes(forbidden), `${forbidden} must not be requested`);
});

test('private/incognito windows are never allowed', () => {
  assert.equal(manifest.incognito, 'not_allowed');
});

test('source code uses no page-reading or keyboard APIs', () => {
  const dir = join(root, 'src');
  const banned = [/chrome\.tabs/, /chrome\.scripting/, /chrome\.webNavigation/, /chrome\.history/, /addEventListener\(['"]key/, /onkey/i, /document\./, /executeScript/];
  for (const file of readdirSync(dir)) {
    const code = readFileSync(join(dir, file), 'utf8')
      .split('\n').filter((l) => !l.trim().startsWith('//')).join('\n'); // comments may mention these words
    for (const re of banned) assert.ok(!re.test(code), `${file} must not match ${re}`);
  }
});
