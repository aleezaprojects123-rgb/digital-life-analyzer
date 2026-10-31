// The universal browser-activity vocabulary and the adapter registry. Pure logic, no browser calls.
//
//   generic collector (tab-logger.js)  ->  describeActivity()  ->  event-normalizer.js  ->  agent
//
// Every website gets an event. A website with a platform adapter gets a more precise activity_type; any other website
// falls back to the generic adapter (platform from the known list or "unknown", activity_type "webpage").
// Nothing here reads page content: it looks only at the already sanitized host and path.

import { youtubeAdapter } from "./adapters/youtube.js";
import { instagramAdapter } from "./adapters/instagram.js";
import { tiktokAdapter } from "./adapters/tiktok.js";
import { redditAdapter } from "./adapters/reddit.js";
import { facebookAdapter } from "./adapters/facebook.js";
import { genericAdapter } from "./adapters/generic.js";

export const ACTIVITY_TYPES = Object.freeze([
  "webpage", "search", "video", "short_video", "reel", "music", "post", "article", "document",
  "social_feed", "messaging", "meeting", "shopping", "game", "unknown",
]);

export const PLATFORMS = Object.freeze([
  "youtube", "instagram", "tiktok", "reddit", "facebook", "whatsapp", "spotify", "netflix", "github", "google", "unknown",
]);

/** Registrable domain -> platform. Only identifies the site; it never needs an adapter. */
const PLATFORM_BY_DOMAIN = Object.freeze({
  "youtube.com": "youtube", "youtu.be": "youtube",
  "instagram.com": "instagram",
  "tiktok.com": "tiktok",
  "reddit.com": "reddit",
  "facebook.com": "facebook",
  "whatsapp.com": "whatsapp",
  "spotify.com": "spotify",
  "netflix.com": "netflix",
  "github.com": "github",
  "google.com": "google",
});

export function identifyPlatform(domain) {
  return Object.hasOwn(PLATFORM_BY_DOMAIN, domain) ? PLATFORM_BY_DOMAIN[domain] : "unknown";
}

/**
 * A platform adapter turns the sanitized location of a page into a normalized activity.
 *   platform                      one of PLATFORMS
 *   identify(domain, host)        true if this adapter handles the page
 *   getActivityType({host, path}) one of ACTIVITY_TYPES, or null when the page cannot be told apart reliably
 *   contentKey(url, host)         optional: in-memory identity of the current item from the REAL url (null = none); never stored
 *   settleOnReturnMs              optional: a content period that opens on arrival in a tab is counted only if it lasts this long (ms)
 *   withholdTitles: true          optional: never keep the tab title on this platform, even on pages the adapter cannot classify
 *   extractTitle(rawTitle, ctx)   optional: a better title from the tab title (never from page content); omit to keep the tab title
 * Media (title, artist, playback ...) is NOT an adapter job: the Windows agent adds it from the system media controls.
 */
export const ADAPTERS = Object.freeze([youtubeAdapter, instagramAdapter, tiktokAdapter, redditAdapter, facebookAdapter]);

/**
 * @param {{url: string, host: string, domain: string, rawTitle?: string|null}} page  sanitized url, its host, registrable domain
 * @returns {{platform: string, activity_type: string, rawTitle: string|null|undefined}}
 */
export function describeActivity(page, adapters = ADAPTERS) {
  let path = "/";
  try { path = new URL(page.url).pathname; } catch { /* keep "/" */ }
  const ctx = { host: page.host, path, domain: page.domain };
  let fallbackTitle = page.rawTitle;

  for (const adapter of adapters) {
    if (!adapter.identify(page.domain, page.host)) continue;
    const type = adapter.getActivityType(ctx);
    if (type === null || !ACTIVITY_TYPES.includes(type)) {
      if (adapter.withholdTitles === true) fallbackTitle = null; // this platform's tab titles are never kept, whatever the page
      break; // adapter cannot tell: use the generic fallback
    }
    const rawTitle = adapter.extractTitle ? adapter.extractTitle(page.rawTitle, ctx) : page.rawTitle;
    return { platform: adapter.platform, activity_type: type, rawTitle, settleMs: adapter.settleOnReturnMs ?? 0 };
  }
  return {
    platform: identifyPlatform(page.domain),
    activity_type: genericAdapter.getActivityType(ctx),
    rawTitle: fallbackTitle,
    settleMs: 0,
  };
}
