// TikTok: individual videos are /@user/video/<id>. The feed (/, /foryou, /following) is a feed.
// Only the sanitized path and the tab title are used. The numeric video id is masked in the stored URL, so the in-memory
// contentKey is what tells video A from video B. If TikTok's feed changes the video without changing the URL, nothing in the
// URL or tab title shows it, and the event is NOT split on a guess (needs real-browser verification, see README).

const GENERIC_TITLES = new Set(["tiktok", "tiktok make your day", "tiktok make your day for you"]);

const VIDEO_PATH = /^\/[^/]+\/video\/[^/]+/;

export const tiktokAdapter = Object.freeze({
  platform: "tiktok",

  identify: (domain) => domain === "tiktok.com",

  contentKey(url) {
    const m = /^\/@[^/]+\/video\/(\d{8,25})\/?$/.exec(url.pathname);
    return m ? `video:${m[1]}` : null;
  },

  getActivityType({ path }) {
    if (VIDEO_PATH.test(path)) return "short_video";
    if (path === "/" || path === "/foryou" || path === "/following") return "social_feed";
    return null;
  },

  extractTitle(rawTitle) {
    if (typeof rawTitle !== "string") return null;
    const plain = rawTitle.replace(/^\(\d+\)\s*/, "").replace(/[^\p{L}\p{N}]+/gu, " ").trim().toLowerCase();
    return GENERIC_TITLES.has(plain) ? null : rawTitle;
  },
});
