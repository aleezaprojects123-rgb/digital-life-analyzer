// The fallback for every website without an adapter (and for pages an adapter cannot tell apart).
// It uses only the sanitized host and path, and only patterns that hold across sites. Anything else is "webpage".
// No page content is read. Content identity: none, so the logger never splits an unknown site on a guess.

import { hasSensitiveHost } from "../sensitive-domains.js";

const MEETING_HOSTS = ["meet.google.com", "zoom.us", "whereby.com", "meet.jit.si"];
const DOCUMENT_HOSTS = ["docs.google.com", "notion.so", "notion.site", "overleaf.com"];

const onHost = (host, list) => list.some((h) => host === h || host.endsWith(`.${h}`));

export const genericAdapter = Object.freeze({
  platform: "unknown",
  identify: () => true,

  getActivityType({ host, path }) {
    if (hasSensitiveHost(host)) return "messaging";                              // webmail and chat apps (their titles are withheld)
    if (onHost(host, MEETING_HOSTS)) return path === "/" ? "webpage" : "meeting";
    if (/^\/(search|results)(\/|$)/.test(path)) return "search";
    if (onHost(host, DOCUMENT_HOSTS) && path !== "/") return "document";
    if (/\.pdf$/i.test(path)) return "document";
    if (/^\/((19|20)\d\d\/\d{1,2}|news|article|articles|blog)(\/|$)/.test(path)) return "article";
    return "webpage";
  },
});
