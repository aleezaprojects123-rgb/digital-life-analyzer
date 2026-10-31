// The Instagram key-shape diagnostic: it must show the shape of a Reel code and never the code or the address.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createKeyDiagnostics, describeToken } from "../key-diag.js";

const REEL = (code) => `https://www.instagram.com/reels/${code}/`;
const CODE_MIXED = "DeT_HthytfP";
const CODE_DIGITS = "3712345678901234567";

function make() {
  const lines = [];
  const diag = createKeyDiagnostics((l) => lines.push(l), () => new Date(Date.UTC(2026, 9, 31, 6, 39, 5)));
  return { diag, lines };
}

test("the shape of a code: digits-only or mixed, its length, and a 4-character fingerprint", () => {
  assert.deepEqual(describeToken(CODE_DIGITS), { type: "digits", len: 19, hash: describeToken(CODE_DIGITS).hash });
  assert.equal(describeToken(CODE_MIXED).type, "mixed");
  assert.equal(describeToken(CODE_MIXED).len, 11);
  assert.match(describeToken(CODE_MIXED).hash, /^[0-9a-z]{4}$/);
  assert.equal(describeToken("DeT_HthytfP").hash, describeToken("DeT_HthytfP").hash, "the same code always gives the same fingerprint");
  assert.notEqual(describeToken("DeT_HthytfP").hash, describeToken("DeT_HthytfQ").hash);
});

test("a key change prints exactly one line of that shape", () => {
  const { diag, lines } = make();
  diag.observe(1, REEL(CODE_MIXED));
  assert.equal(lines.length, 1);
  assert.match(lines[0], /^\[DLA-KEY 06:39:05\] Instagram key change: type=mixed len=11 hash=[0-9a-z]{4}$/);
});

test("digits-only and mixed codes are told apart, which is what the investigation needs", () => {
  const { diag, lines } = make();
  diag.observe(1, REEL(CODE_DIGITS));
  diag.observe(1, REEL(CODE_MIXED));
  assert.match(lines[0], /type=digits len=19 /);
  assert.match(lines[1], /type=mixed len=11 /);
});

test("neither the code nor the address nor the domain is ever printed", () => {
  const { diag, lines } = make();
  for (const code of [CODE_MIXED, CODE_DIGITS, "C8aBcDeFgHi"]) diag.observe(1, `${REEL(code)}?igsh=SECRET#frag`);
  const all = lines.join("\n");
  for (const secret of [CODE_MIXED, CODE_DIGITS, "C8aBcDeFgHi", "SECRET", "igsh", "frag", "instagram.com", "http", "/reels"]) assert.ok(!all.includes(secret), secret);
  assert.equal(lines.length, 3);
});

test("it prints only when the key changes: the same Reel again, a different form of the address, or a re-render print nothing", () => {
  const { diag, lines } = make();
  diag.observe(1, REEL(CODE_MIXED));
  diag.observe(1, REEL(CODE_MIXED));
  diag.observe(1, `https://www.instagram.com/reel/${CODE_MIXED}/?x=1`);
  diag.observe(1, `https://www.instagram.com/someone/reel/${CODE_MIXED}/`);
  assert.equal(lines.length, 1);
});

test("it mirrors the counting memory: leaving and coming back to the same Reel prints nothing; a flip to another code and back prints twice", () => {
  const { diag, lines } = make();
  diag.observe(1, REEL(CODE_MIXED));
  diag.observe(1, "https://example.com/");
  diag.observe(1, "https://www.instagram.com/");
  diag.observe(1, REEL(CODE_MIXED));
  assert.equal(lines.length, 1, "same Reel after leaving: not a change");
  diag.observe(1, REEL(CODE_DIGITS));
  diag.observe(1, REEL(CODE_MIXED));
  assert.equal(lines.length, 3, "a flip there and back is visible as two more lines with repeating fingerprints");
  assert.equal(lines[0].match(/hash=(\S+)$/)[1], lines[2].match(/hash=(\S+)$/)[1]);
});

test("each tab has its own memory", () => {
  const { diag, lines } = make();
  diag.observe(1, REEL(CODE_MIXED));
  diag.observe(2, REEL(CODE_MIXED));
  assert.equal(lines.length, 2);
});

test("nothing is printed for other sites, other pages, or bad input, and it never throws", () => {
  const { diag, lines } = make();
  for (const url of ["https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.facebook.com/reel/1234567890123/", "https://www.instagram.com/", "https://www.instagram.com/reels/", "https://evil-instagram.com/reels/DeT_HthytfP/", "not a url", "", null, undefined, 42]) {
    assert.doesNotThrow(() => diag.observe(1, url));
  }
  assert.equal(lines.length, 0);
});

test("a logger that throws cannot break recording", () => {
  const diag = createKeyDiagnostics(() => { throw new Error("console broke"); });
  assert.doesNotThrow(() => diag.observe(1, REEL(CODE_MIXED)));
});
