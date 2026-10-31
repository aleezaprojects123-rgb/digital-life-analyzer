// Facebook: high-level activity and, where the address allows it, which content item is on screen. Nothing else.
// It reads no page content (no post text, names, captions, comments, messages) and withholds the tab title, because on Facebook
// the title can contain post text or a notification count. Only the sanitized path decides the activity type.
//
// What can be counted: an item with its own address, i.e. /reel/<id>, /watch/?v=<id>, /<page>/videos/<id>, /<user>/posts/<id>,
// /groups/<g>/posts/<id>, /permalink.php?story_fbid=<id>. The id is read from the real address in memory only, to notice that
// the user moved to another item, and is never stored.
//
// LIMIT (not verified in a real browser): the main News Feed and any viewer that changes the item without changing the address
// have NO item identity. They are recorded as activity with time, but they are not counted (content_count 0), so the count
// under-reports there rather than guessing.

const ID = /^[A-Za-z0-9_-]{5,40}$/;

function segmentsOf(path) {
  return path.split("/").filter(Boolean);
}

export const facebookAdapter = Object.freeze({
  platform: "facebook",

  identify: (domain) => domain === "facebook.com",

  withholdTitles: true,

  contentKey(url) {
    const segments = segmentsOf(url.pathname);
    const [a, b] = segments;
    if (a === "reel" && b && ID.test(b)) return `reel:${b}`;
    if (a === "watch") {
      const v = url.searchParams.get("v");
      return v && ID.test(v) ? `video:${v}` : null;
    }
    if (a === "permalink.php") {
      const id = url.searchParams.get("story_fbid");
      return id && ID.test(id) ? `post:${id}` : null;
    }
    const videos = segments.indexOf("videos");
    if (videos >= 0 && segments[videos + 1] && ID.test(segments[videos + 1])) return `video:${segments[videos + 1]}`;
    const posts = segments.indexOf("posts");
    if (posts >= 0 && segments[posts + 1] && ID.test(segments[posts + 1])) return `post:${segments[posts + 1]}`;
    return null;
  },

  getActivityType({ path }) {
    const segments = segmentsOf(path);
    const [a] = segments;
    if (segments.length === 0) return "social_feed";
    if (a === "reel" || a === "reels") return "reel";
    if (a === "watch") return "video";
    if (a === "permalink.php") return "post";
    if (a === "messages" || a === "messenger") return "messaging";
    if (a === "marketplace") return "shopping";
    if (segments.includes("videos")) return "video";
    if (segments.includes("posts")) return "post";
    return null; // profiles, pages, groups lists, events, settings ...: not reliably one kind of activity
  },

  extractTitle() {
    return null; // the tab title can hold post text or a notification count: never kept
  },
});
