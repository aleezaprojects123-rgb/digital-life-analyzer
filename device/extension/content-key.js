// "Which piece of content is this?", used only to notice that the content changed inside the same tab
// (YouTube, Instagram, TikTok and Reddit change the video/Reel/post without leaving the tab, and the stored URL hides the id).
//
// The key lives in memory only. It is never put into an event, never stored and never sent anywhere.
// Each platform adapter says how to read its own key from the real URL (adapter.contentKey). A site without such an adapter has
// no key (null): the tab logger then keeps one event for the same tab + URL and never splits on a guess.

import { ADAPTERS } from "./platforms.js";
import { getDomain } from "./url-sanitizer.js";

/** @returns {string | null} e.g. "short:AbC...", "video:xyz...", "reel:...", "post:...", or null when no reliable identity exists. */
export function contentKey(rawUrl, adapters = ADAPTERS) {
  let url;
  try { url = new URL(rawUrl); } catch { return null; }
  if (url.protocol !== "https:" && url.protocol !== "http:") return null;
  const host = url.hostname.toLowerCase();
  const domain = getDomain(host);
  if (!domain) return null;

  for (const adapter of adapters) {
    if (typeof adapter.contentKey !== "function" || !adapter.identify(domain, host)) continue;
    const key = adapter.contentKey(url, host);
    if (key) return key;
  }
  return null;
}

/**
 * Pages whose title is public content even though the id in the path was masked (the id looks random, but it is a public
 * video id, shortcode or post id, not a secret). Exactly the pages that have a content key.
 */
export function isPublicVideoPage(rawUrl, adapters = ADAPTERS) {
  return contentKey(rawUrl, adapters) !== null;
}
