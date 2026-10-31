// The keep-alive timer on its own: fake interval, fake ping. (The real worker wiring is tested in background.test.js.)
import { test } from "node:test";
import assert from "node:assert/strict";
import { createKeepAlive, KEEP_ALIVE_MS } from "../keep-alive.js";

function make({ run = true, pingFails = false, runThrows = false } = {}) {
  const state = { run, pings: 0, timers: [] };
  const keepAlive = createKeepAlive({
    shouldRun: async () => { if (runThrows) throw new Error("x"); return state.run; },
    ping: async () => { state.pings++; if (pingFails) throw new Error("x"); },
    setIntervalFn: (fn, ms) => { const t = { fn, ms, cleared: false }; state.timers.push(t); return t; },
    clearIntervalFn: (t) => { t.cleared = true; },
  });
  return { keepAlive, state };
}

test("the interval is 20 seconds, comfortably under Chrome's 30 second idle limit", () => {
  assert.equal(KEEP_ALIVE_MS, 20_000);
});

test("start schedules one timer at that interval, and starting again does not add another", () => {
  const { keepAlive, state } = make();
  assert.equal(keepAlive.running, false);
  keepAlive.start();
  keepAlive.start();
  assert.deepEqual(state.timers.map((t) => t.ms), [20_000]);
  assert.equal(keepAlive.running, true);
});

test("each tick makes the cheap call while it should run", async () => {
  const { keepAlive, state } = make();
  keepAlive.start();
  await state.timers[0].fn();
  await state.timers[0].fn();
  assert.equal(state.pings, 2);
  assert.equal(keepAlive.running, true);
});

test("a tick stops the timer, without a call, as soon as it should no longer run", async () => {
  const { keepAlive, state } = make();
  keepAlive.start();
  await state.timers[0].fn();
  state.run = false;
  await state.timers[0].fn();
  assert.equal(state.pings, 1);
  assert.equal(state.timers[0].cleared, true);
  assert.equal(keepAlive.running, false);
});

test("if the check itself fails the timer stops rather than run blind", async () => {
  const { keepAlive, state } = make({ runThrows: true });
  keepAlive.start();
  await state.timers[0].fn();
  assert.equal(state.pings, 0);
  assert.equal(keepAlive.running, false);
});

test("a failing call never throws into the worker and the timer keeps going", async () => {
  const { keepAlive, state } = make({ pingFails: true });
  keepAlive.start();
  await assert.doesNotReject(() => state.timers[0].fn());
  assert.equal(state.pings, 1);
  assert.equal(keepAlive.running, true);
});

test("stop clears the timer, is safe to repeat, and start works again afterwards", () => {
  const { keepAlive, state } = make();
  keepAlive.stop();                       // nothing to stop yet
  keepAlive.start();
  keepAlive.stop();
  keepAlive.stop();
  assert.equal(state.timers[0].cleared, true);
  keepAlive.start();
  assert.equal(state.timers.length, 2);
  assert.equal(keepAlive.running, true);
});
