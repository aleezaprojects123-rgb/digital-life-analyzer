// TEMP-DIAG (Instagram counting investigation). Console only. Nothing is stored or sent, and counting is not touched.
//
// When the Reel code in an Instagram address changes inside a tab, it prints ONE line with the SHAPE of the new code only:
//   [DLA-KEY 06:39:05] Instagram key change: type=mixed len=11 hash=ab12
// type = digits (only digits) or mixed (anything else), len = how many characters, hash = a 4-character fingerprint.
// The code itself and the address are never printed. The fingerprint lets you see whether the code flips between two values.
//
// It mirrors the counting memory: a tab's last Instagram key is remembered even while the tab shows something else, so
// coming back to the same Reel prints nothing, exactly as it is not counted again.

import { contentKey } from "./content-key.js";

const hash4 = (text) => [...text].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) >>> 0, 7).toString(36).slice(-4).padStart(4, "0");

/** @returns {{type: "digits" | "mixed", len: number, hash: string}} */
export function describeToken(token) {
  return { type: /^\d+$/.test(token) ? "digits" : "mixed", len: token.length, hash: hash4(token) };
}

export function createKeyDiagnostics(log, now = () => new Date()) {
  const lastKey = new Map(); // tabId -> the last Instagram key seen in that tab (memory only)
  return {
    observe(tabId, url) {
      try {
        if (typeof url !== "string" || new URL(url).hostname.replace(/^www\./, "") !== "instagram.com") return;
        const key = contentKey(url);
        if (key === null || lastKey.get(tabId) === key) return;
        lastKey.set(tabId, key);
        const { type, len, hash } = describeToken(key.slice(key.indexOf(":") + 1));
        log(`[DLA-KEY ${now().toISOString().slice(11, 19)}] Instagram key change: type=${type} len=${len} hash=${hash}`);
      } catch {
        // a diagnostic must never affect recording
      }
    },
  };
}
