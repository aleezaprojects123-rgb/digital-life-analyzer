// Reddit: an individual post is /r/<sub>/comments/<id>/<slug>/ (or /comments/<id>). Listings and the home page are feeds.
// The tab title of a post page is the post's public title. Only the sanitized path and the tab title are used.

const POST_PATH = /^\/(r\/[^/]+\/)?comments\/[^/]+/;
const FEED_PATH = /^\/(r\/[^/]+(\/(hot|new|top|rising|best))?|hot|new|top|rising|best|popular|all)?\/?$/;

export const redditAdapter = Object.freeze({
  platform: "reddit",

  identify: (domain) => domain === "reddit.com",

  contentKey(url) {
    const m = /^\/(?:r\/[^/]+\/)?comments\/([a-z0-9]{4,10})(?:\/|$)/.exec(url.pathname);
    return m ? `post:${m[1]}` : null;
  },

  getActivityType({ path }) {
    if (POST_PATH.test(path)) return "post";
    if (FEED_PATH.test(path)) return "social_feed";
    return null;
  },
});
