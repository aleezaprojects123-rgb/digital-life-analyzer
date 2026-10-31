// YouTube: tells search, normal videos, Shorts and YouTube Music apart from the sanitized path only.
// It does not read the page or the search results. The video title comes from the tab title; media details from the agent.

const ID = /^[A-Za-z0-9_-]{6,20}$/;

export const youtubeAdapter = Object.freeze({
  platform: "youtube",

  identify: (domain) => domain === "youtube.com" || domain === "youtu.be",

  /** In-memory "which video is this?" from the REAL url (the stored url masks the id). Never stored or sent. */
  contentKey(url, host) {
    const segments = url.pathname.split("/").filter(Boolean);
    if (host === "youtu.be") return segments[0] && ID.test(segments[0]) ? `video:${segments[0]}` : null;
    if (segments[0] === "shorts") return segments[1] && ID.test(segments[1]) ? `short:${segments[1]}` : null;
    if (segments[0] === "live" || segments[0] === "embed") return segments[1] && ID.test(segments[1]) ? `video:${segments[1]}` : null;
    if (segments[0] === "watch") {
      const v = url.searchParams.get("v");
      return v && ID.test(v) ? `video:${v}` : null;
    }
    return null;
  },

  getActivityType({ host, path }) {
    if (host === "youtu.be") return path.length > 1 ? "video" : null;
    if (path === "/results" || path.startsWith("/results/")) return host === "music.youtube.com" ? null : "search";
    if (path.startsWith("/shorts/")) return "short_video";
    if (path === "/watch" || path.startsWith("/live/") || path.startsWith("/embed/")) return host === "music.youtube.com" ? "music" : "video";
    return null; // home, channel, playlist list ...: not reliably one kind of activity
  },
});
