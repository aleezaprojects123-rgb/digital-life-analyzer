// Instagram: tells Reels, posts and direct messages apart from the sanitized path only.
// It does not read the page: no caption, creator, audio or hashtags. The title is the browser's tab title, and only when that
// title is about the content: Instagram often shows just "Instagram" and then the title stays null.
// What matters is the behaviour: a Reel (or post) was on screen, for how long, and when the address moved to another one.
//
// Reels come with a /reel/<code>/, /reels/<code>/ or /<user>/reel/<code>/ path. The code is masked in the stored URL.

const GENERIC_TITLES = new Set(["instagram", "reels", "instagram reels", "instagram photos and videos"]);

function segmentsOf(path) {
  return path.split("/").filter(Boolean);
}

export const instagramAdapter = Object.freeze({
  platform: "instagram",

  identify: (domain) => domain === "instagram.com",

  /**
   * When the user comes back to an Instagram tab, the page still shows the Reel that was left for a moment (1 to 2 s), then moves on.
   * A Reel period that opens on arrival is therefore counted only if it lasts this long; a shorter one is a transition, not a Reel
   * the user watched. Swipes inside the tab are not delayed.
   */
  settleOnReturnMs: 3000,

  /** In-memory identity from the real url: /reel/<code>, /reels/<code>, /p/<code>, /<user>/reel/<code>. The code is a public shortcode. */
  contentKey(url) {
    const code = /^[A-Za-z0-9_-]{5,20}$/;
    const [a, b, c] = segmentsOf(url.pathname);
    if ((a === "reel" || a === "reels") && b && code.test(b)) return `reel:${b}`;
    if (a === "p" && b && code.test(b)) return `post:${b}`;
    if (b === "reel" && c && code.test(c)) return `reel:${c}`;
    return null;
  },

  getActivityType({ path }) {
    const [a, b, c] = segmentsOf(path);
    if (a === undefined) return "social_feed";                   // the home feed: time is measured, but no item is counted (no item address exists)
    if ((a === "reel" || a === "reels") && b) return "reel";   // an individual Reel (bare /reels/ is only the feed)
    if (b === "reel" && c) return "reel";
    if (a === "p" && b) return "post";
    if (a === "direct") return "messaging";
    return null; // home, explore, profiles, stories ...: not reliably one kind of content
  },

  extractTitle(rawTitle, { path }) {
    if (segmentsOf(path).length === 0) return rawTitle ?? null;   // the home feed keeps its tab title exactly as before this page got a type
    if (segmentsOf(path)[0] === "direct") return null;            // chats: never keep a title
    if (typeof rawTitle !== "string") return null;
    const plain = rawTitle.replace(/^\(\d+\)\s*/, "").replace(/[^\p{L}\p{N}]+/gu, " ").trim().toLowerCase();
    return GENERIC_TITLES.has(plain) ? null : rawTitle;           // a generic "Instagram" is not the Reel's title
  },
});
