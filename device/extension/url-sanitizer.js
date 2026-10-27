// URL cleaning for the DLA extension (team decision 1). Plain JavaScript, no libraries.
//
// sanitizeUrl(raw)  -> "scheme://host/path" with everything risky removed, or null if the page must not be recorded.
// getDomain(host)   -> the registrable domain (www.youtube.com -> youtube.com), or null.
// isHiddenSite(d)   -> hook for the "hidden sites" setting. It always says false for now (the setting does not exist yet).
//
// This runs inside the extension before anything is kept or sent. The cleaned URL never leaves the PC.

const MAX_URL_LENGTH = 2048; // the limit in contracts/event.schema.json
const MAX_HOST_LENGTH = 253;
const MASK = "*";

// Same shape as the "domain" rule in contracts/event.schema.json: lowercase labels, at least one dot.
const DOMAIN_SHAPE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/;
const IPV4_SHAPE = /^\d{1,3}(\.\d{1,3}){3}$/;

// Path words after which the NEXT segment is a secret (reset token, invite code, session id ...), however short.
const SECRET_PREFIXES = new Set([
  "reset", "verify", "confirm", "token", "tokens", "invite", "invitation", "auth", "oauth", "oauth2", "session", "sessions",
  "login", "signin", "activate", "unsubscribe", "magic", "reset-password", "password-reset", "verify-email", "confirm-email",
]);

// Second-level public suffixes with two parts (small built-in list; not the full Public Suffix List).
// Known gap: shared hosts such as github.io or blogspot.com are treated as one site.
const MULTI_PART_SUFFIXES = new Set([
  "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk",
  "com.pk", "edu.pk", "org.pk", "gov.pk", "net.pk",
  "co.in", "net.in", "org.in", "ac.in", "gov.in", "edu.in",
  "com.au", "net.au", "org.au", "edu.au", "gov.au",
  "co.nz", "org.nz", "net.nz", "ac.nz",
  "co.jp", "ne.jp", "or.jp", "ac.jp",
  "co.kr", "co.za", "co.id", "co.il", "co.th",
  "com.br", "com.cn", "com.hk", "com.sg", "com.my", "com.tr", "com.mx", "com.ar", "com.sa", "com.eg", "com.ng",
  "com.bd", "com.lk", "com.ph", "com.vn", "com.tw", "com.ua", "com.pe", "com.co", "com.np", "edu.au",
]);

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Always false for now; later it will check the user's hidden-sites list. The caller skips the page before reading anything. */
export function isHiddenSite(domain) {
  void domain;
  return false;
}

/**
 * Registrable domain of a hostname: "www.youtube.com" -> "youtube.com", "news.bbc.co.uk" -> "bbc.co.uk".
 * An IPv4 address is returned as is. Anything that is not a plain hostname (port, path, userinfo, IPv6, one label) gives null.
 */
export function getDomain(host) {
  if (typeof host !== "string") return null;
  let h = host.trim().toLowerCase();
  if (h.endsWith(".")) h = h.slice(0, -1);
  if (h.length === 0 || h.length > MAX_HOST_LENGTH || !DOMAIN_SHAPE.test(h)) return null;

  if (IPV4_SHAPE.test(h)) return h;

  const labels = h.split(".");
  const lastTwo = labels.slice(-2).join(".");
  if (MULTI_PART_SUFFIXES.has(lastTwo)) {
    return labels.length >= 3 ? labels.slice(-3).join(".") : null; // "co.uk" alone is a suffix, not a site
  }
  return lastTwo;
}

/** Returns "scheme://host/path" or null. See the rules in the team decisions (CLAUDE.md, decision 1). */
export function sanitizeUrl(rawUrl) {
  return sanitizeUrlDetailed(rawUrl)?.url ?? null;
}

/**
 * Same as sanitizeUrl, but also says whether anything in the path was masked or cut: { url, masked } or null.
 * The tab logger drops the page title when `masked` is true (a masked path means the page is probably private).
 */
export function sanitizeUrlDetailed(rawUrl) {
  if (typeof rawUrl !== "string") return null;

  let url;
  try {
    url = new URL(rawUrl.trim());
  } catch {
    return null;
  }

  // Only normal web pages. chrome:, edge:, about:, file:, chrome-extension:, data:, javascript: ... are never recorded.
  if (url.protocol !== "http:" && url.protocol !== "https:") return null;

  // hostname is already lowercase, punycode, and has no port or userinfo. IPv6 ("[..]") and one-word hosts are not recorded.
  let host = url.hostname;
  if (host.endsWith(".")) host = host.slice(0, -1);
  if (host.length > MAX_HOST_LENGTH || !DOMAIN_SHAPE.test(host)) return null;

  const origin = `${url.protocol}//${host}`;
  const segments = url.pathname.split("/").filter((s) => s.length > 0); // drops the leading slash, trailing slash and "//"

  const cleaned = [];
  let masked = false;
  let previousWasSecretPrefix = false;
  for (const segment of segments) {
    const text = decode(segment);
    const hide = previousWasSecretPrefix || looksSecret(text);
    if (hide) masked = true;
    cleaned.push(hide ? MASK : segment);
    previousWasSecretPrefix = SECRET_PREFIXES.has(stripExtension(text).toLowerCase());
  }

  // Keep the whole thing under the contract's length limit by cutting the tail.
  let path = "";
  for (let i = 0; i < cleaned.length; i++) {
    const next = `${path}/${cleaned[i]}`;
    if (origin.length + next.length > MAX_URL_LENGTH - 2) {
      path += `/${MASK}`;
      masked = true;
      break;
    }
    path = next;
  }

  return { url: origin + path, masked };
}

// ---------------------------------------------------------------------------------------------------------------------

function decode(segment) {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment; // broken %-escape: look at it as it is
  }
}

function stripExtension(text) {
  return text.replace(/\.[A-Za-z0-9]{1,5}$/, "");
}

/** Is this path segment (already %-decoded) a random id, an email or another secret? */
function looksSecret(text) {
  // An email address, however it is written. A leading "@" alone is a public handle (/@name), not an email.
  if (/^.+@.+$/.test(text)) return true;

  // Anything 32+ characters made of id characters would be refused by the contract anyway.
  if (text.length >= 32 && /^[A-Za-z0-9_-]+$/.test(text)) return true;

  const base = stripExtension(text);
  if (UUID.test(base)) return true;

  const parts = base.split(/[-_.]+/).filter((p) => p.length > 0);
  if (parts.some(partLooksRandom)) return true;

  // A longer hyphen/underscore chain that is mostly not readable words ("Ab3-dE9_xQ2zK1") is a token, not a title.
  if (parts.length > 1 && base.length >= 12) {
    const readable = parts.filter((p) => /^[A-Za-z][a-z]*$/.test(p) || /^\d{1,4}$/.test(p)).length;
    if (readable / parts.length < 0.5) return true;
  }
  return false;
}

function partLooksRandom(part) {
  if (UUID.test(part)) return true;
  if (/^[0-9a-f]{16,}$/i.test(part)) return true; // long hex
  if (/^\d{6,}$/.test(part)) return true; // numeric id
  if (part.length >= 10) {
    if (/[A-Za-z]/.test(part) && /\d/.test(part)) return true; // letters and digits mixed
    if (/[a-z]/.test(part) && /[A-Z]/.test(part.slice(1))) return true; // lower and upper case mixed (not just "Capitalized")
  }
  return false;
}
