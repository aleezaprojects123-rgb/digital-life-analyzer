// Run from device/extension/ with:  node --test
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { sanitizeUrl, getDomain, isHiddenSite } from "../url-sanitizer.js";

// ---- the contract's own rules for "url" and "domain", read from contracts/event.schema.json -------------------------

const schema = JSON.parse(readFileSync(new URL("../../../contracts/event.schema.json", import.meta.url), "utf8"));
const urlRule = schema.properties.url;
const domainRule = schema.properties.domain;
const urlPattern = new RegExp(urlRule.pattern);
const unsafePatterns = urlRule.not.anyOf.map((rule) => new RegExp(rule.pattern));
const domainPattern = new RegExp(domainRule.pattern);

/** True if the URL would pass the url rules of Contract 1 (pattern, max length and the two safety-net checks). */
function passesContract(url) {
  return (
    typeof url === "string" &&
    url.length <= urlRule.maxLength &&
    urlPattern.test(url) &&
    !unsafePatterns.some((p) => p.test(url))
  );
}

function check(input, expected) {
  const actual = sanitizeUrl(input);
  assert.equal(actual, expected, `sanitizeUrl(${JSON.stringify(input.length > 80 ? input.slice(0, 80) + "..." : input)})`);
  if (actual !== null) assert.ok(passesContract(actual), `${actual} must pass the url rules of the contract`);
}

// ---- basics --------------------------------------------------------------------------------------------------------

test("the YouTube Shorts example from the task", () => {
  check("https://www.youtube.com/shorts/Ab3dE9xQ2zK?x=1#t", "https://www.youtube.com/shorts/*");
});

test("query string and fragment are always removed", () => {
  check("https://example.com/search?q=secret+words&token=abc#section", "https://example.com/search");
  check("https://example.com/a/b?#", "https://example.com/a/b");
  check("https://example.com/?only=query", "https://example.com");
  check("https://example.com#frag", "https://example.com");
});

test("short readable words are kept", () => {
  check("https://en.wikipedia.org/wiki/Albert_Einstein", "https://en.wikipedia.org/wiki/Albert_Einstein");
  check("https://example.com/docs/getting-started/install", "https://example.com/docs/getting-started/install");
  check("https://example.com/blog/how-to-learn-python-3", "https://example.com/blog/how-to-learn-python-3");
  check("https://example.com/products/iphone-15-pro-max-review", "https://example.com/products/iphone-15-pro-max-review");
  check("https://example.com/page2/v1.2.3", "https://example.com/page2/v1.2.3");
  check("https://example.com/2024/12345/index.html", "https://example.com/2024/12345/index.html");
  check("https://www.youtube.com/@somechannel/videos", "https://www.youtube.com/@somechannel/videos");
});

test("http and https are both kept as they are", () => {
  check("http://example.com/a", "http://example.com/a");
  check("https://example.com/a", "https://example.com/a");
});

// ---- trailing slashes ----------------------------------------------------------------------------------------------

test("trailing slashes are dropped, so /a and /a/ are the same page", () => {
  check("https://example.com/a/", "https://example.com/a");
  check("https://example.com/a///", "https://example.com/a");
  check("https://example.com/", "https://example.com");
  check("https://example.com", "https://example.com");
  check("https://example.com//a//b", "https://example.com/a/b");
});

// ---- hosts ---------------------------------------------------------------------------------------------------------

test("hosts are lowercased", () => {
  check("https://WWW.Example.COM/Path", "https://www.example.com/Path");
  check("HTTPS://EXAMPLE.COM/x", "https://example.com/x");
});

test("a trailing dot on the host is removed", () => {
  check("https://example.com./x", "https://example.com/x");
});

test("ports are removed, including default ports", () => {
  check("https://example.com:8443/app", "https://example.com/app");
  check("http://example.com:80/app", "http://example.com/app");
  check("https://example.com:443/app", "https://example.com/app");
});

test("user:pass@host: the credentials are removed", () => {
  check("https://user:pass@example.com/private", "https://example.com/private");
  check("https://admin@example.com/", "https://example.com");
  check("https://user:p%40ss@example.com:8080/x?y=1", "https://example.com/x");
  assert.ok(!sanitizeUrl("https://user:hunter2@example.com/").includes("hunter2"));
});

test("IPv4 addresses are kept (host only, no port)", () => {
  check("http://192.168.1.10:8080/admin", "http://192.168.1.10/admin");
  check("http://127.0.0.1/", "http://127.0.0.1");
  check("https://8.8.8.8/dns", "https://8.8.8.8/dns");
});

test("IPv6 addresses and one-word hosts cannot satisfy the contract, so they are not recorded", () => {
  assert.equal(sanitizeUrl("http://[::1]:3000/"), null);
  assert.equal(sanitizeUrl("http://[2001:db8::1]/x"), null);
  assert.equal(sanitizeUrl("http://localhost:3000/app"), null);
  assert.equal(sanitizeUrl("http://intranet/page"), null);
});

test("international host names become punycode", () => {
  check("https://münchen.example/x", "https://xn--mnchen-3ya.example/x");
});

test("hosts with characters the contract does not allow are not recorded", () => {
  assert.equal(sanitizeUrl("https://under_score.example.com/"), null);
});

// ---- tokens and ids in the path --------------------------------------------------------------------------------------

test("UUIDs are masked", () => {
  check("https://example.com/orders/123e4567-e89b-12d3-a456-426614174000/view", "https://example.com/orders/*/view");
  check("https://example.com/u/123E4567-E89B-12D3-A456-426614174000", "https://example.com/u/*");
});

test("long hex strings are masked", () => {
  check("https://example.com/commit/9fceb02d0ae598e95dc970b74767f19372d61af8", "https://example.com/commit/*");
  check("https://example.com/h/0123456789abcdef", "https://example.com/h/*");
  check("https://example.com/h/0123456789abcde", "https://example.com/h/*"); // 15 chars: below the hex rule, but letters+digits 10+
});

test("a segment of 10+ characters mixing letters and digits is masked", () => {
  check("https://example.com/x/abc123def456", "https://example.com/x/*");
  check("https://example.com/x/a1b2c3d4e5", "https://example.com/x/*");
  check("https://example.com/watch/dQw4w9WgXcQ", "https://example.com/watch/*");
});

test("a segment of 10+ characters mixing upper and lower case is masked", () => {
  check("https://example.com/s/aBcDeFgHiJkL", "https://example.com/s/*");
  check("https://example.com/s/xyzABCxyzabc", "https://example.com/s/*");
});

test("long single-case or Capitalized words are kept", () => {
  check("https://example.com/s/internationalization", "https://example.com/s/internationalization");
  check("https://example.com/s/Photosynthesis", "https://example.com/s/Photosynthesis");
  check("https://example.com/s/UNIVERSITYADMISSIONS", "https://example.com/s/UNIVERSITYADMISSIONS");
});

test("numeric ids of 6 or more digits are masked, shorter numbers are kept", () => {
  check("https://example.com/user/123456", "https://example.com/user/*");
  check("https://example.com/user/9876543210", "https://example.com/user/*");
  check("https://example.com/user/12345", "https://example.com/user/12345");
  check("https://example.com/2024/10/27/news", "https://example.com/2024/10/27/news");
});

test("email addresses in the path are masked, encoded or not", () => {
  check("https://example.com/users/alice@example.com/profile", "https://example.com/users/*/profile");
  check("https://example.com/users/alice%40example.com", "https://example.com/users/*");
  check("https://example.com/unsubscribe/bob.smith%2Bnews@mail.example.org", "https://example.com/unsubscribe/*");
  check("https://example.com/a/x@y", "https://example.com/a/*");
});

test("the segment right after reset, verify, confirm, token, invite, auth, oauth, session or login is masked", () => {
  check("https://example.com/reset/abc", "https://example.com/reset/*");
  check("https://example.com/verify/Xy7", "https://example.com/verify/*");
  check("https://example.com/confirm/ok", "https://example.com/confirm/*");
  check("https://example.com/token/abcd", "https://example.com/token/*");
  check("https://example.com/invite/team", "https://example.com/invite/*");
  check("https://example.com/auth/callback", "https://example.com/auth/*");
  check("https://example.com/oauth/code", "https://example.com/oauth/*");
  check("https://example.com/session/s1", "https://example.com/session/*");
  check("https://example.com/login/github", "https://example.com/login/*");
  check("https://example.com/RESET/abc", "https://example.com/RESET/*");
  check("https://example.com/account/reset/abc/done", "https://example.com/account/reset/*/done");
});

test("those words alone, or in other positions, are not masked", () => {
  check("https://example.com/login", "https://example.com/login");
  check("https://example.com/reset", "https://example.com/reset");
  check("https://example.com/help/login-problems", "https://example.com/help/login-problems");
});

test("a token with hyphens or underscores is masked", () => {
  check("https://example.com/r/Ab3-dE9_xQ2zK1", "https://example.com/r/*");
  check("https://example.com/r/x7Kq-p2Zw-9mTa-Lv4", "https://example.com/r/*");
});

test("a file extension does not hide an id", () => {
  check("https://example.com/files/Ab3dE9xQ2zK.pdf", "https://example.com/files/*");
  check("https://example.com/files/report.pdf", "https://example.com/files/report.pdf");
});

test("any segment of 32+ id characters is masked, whatever it looks like", () => {
  const long = "a".repeat(40);
  check(`https://example.com/${long}`, "https://example.com/*");
  check("https://example.com/this-is-a-very-long-readable-title-slug-here", "https://example.com/*");
});

// ---- encoded characters ----------------------------------------------------------------------------------------------

test("encoded characters stay encoded in the output", () => {
  check("https://example.com/caf%C3%A9/menu", "https://example.com/caf%C3%A9/menu");
  check("https://example.com/a%20b/c", "https://example.com/a%20b/c");
  check("https://example.com/ca fe", "https://example.com/ca%20fe");
});

test("an encoded ? or # in the path never turns into a query or fragment", () => {
  const out = sanitizeUrl("https://example.com/a%3Fb%23c/d");
  assert.equal(out, "https://example.com/a%3Fb%23c/d");
  assert.ok(passesContract(out));
});

test("an id hidden behind %-encoding is still masked", () => {
  check("https://example.com/%41b3dE9xQ2zK", "https://example.com/*"); // %41 = "A"
  check("https://example.com/%31%32%33%34%35%36", "https://example.com/*"); // "123456"
});

test("a broken %-escape does not crash", () => {
  check("https://example.com/100%/done", "https://example.com/100%/done");
  check("https://example.com/%zz%/x", "https://example.com/%zz%/x");
  check("https://example.com/%E0%A4%A/x", "https://example.com/%E0%A4%A/x");
});

test("encoded dot segments cannot climb out of the path", () => {
  check("https://example.com/a/%2e%2e/b", "https://example.com/b");
  check("https://example.com/a/../../b", "https://example.com/b");
  check("https://example.com/a/./b", "https://example.com/a/b");
});

// ---- very long input -------------------------------------------------------------------------------------------------

test("a very long path is cut so the result fits the contract", () => {
  const path = Array.from({ length: 800 }, (_, i) => `page${i % 7}`).join("/");
  const out = sanitizeUrl(`https://example.com/${path}`);
  assert.ok(out.length <= 2048, `length ${out.length}`);
  assert.ok(out.endsWith("/*"));
  assert.ok(passesContract(out));
});

test("a single enormous segment is masked", () => {
  check(`https://example.com/${"x".repeat(100000)}`, "https://example.com/*");
});

test("a very long query string is simply dropped", () => {
  check(`https://example.com/a?${"q=1&".repeat(50000)}`, "https://example.com/a");
});

test("a very long host is not recorded", () => {
  assert.equal(sanitizeUrl(`https://${"a".repeat(300)}.com/`), null);
});

// ---- pages that must never be recorded ----------------------------------------------------------------------------------

test("browser pages, local files, extension pages and the new tab page give null", () => {
  const never = [
    "chrome://extensions",
    "chrome://newtab/",
    "edge://settings",
    "edge://newtab/",
    "about:blank",
    "about:newtab",
    "file:///C:/Users/me/secret.txt",
    "chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html",
    "moz-extension://123e4567-e89b-12d3-a456-426614174000/page.html",
    "view-source:https://example.com/",
    "data:text/html,<h1>hi</h1>",
    "javascript:alert(1)",
    "blob:https://example.com/123e4567-e89b-12d3-a456-426614174000",
    "ftp://example.com/file",
    "ws://example.com/socket",
    "devtools://devtools/bundled/inspector.html",
    "chrome-search://local-ntp/local-ntp.html",
  ];
  for (const url of never) assert.equal(sanitizeUrl(url), null, url);
});

test("garbage input gives null and never throws", () => {
  for (const bad of ["", "   ", "not a url", "http://", "https://", "//example.com", "example.com", "http:///x", null, undefined, 42, {}, [], true]) {
    assert.equal(sanitizeUrl(bad), null, String(bad));
  }
});

test("surrounding spaces and line breaks are ignored", () => {
  check("  https://example.com/a  ", "https://example.com/a");
  check("https://exam\nple.com/a", "https://example.com/a");
});

// ---- every output passes the contract ----------------------------------------------------------------------------------

test("a batch of mixed inputs all pass the url rules in contracts/event.schema.json", () => {
  const inputs = [
    "https://www.youtube.com/shorts/Ab3dE9xQ2zK?x=1#t",
    "https://mail.example.com/u/0/#inbox/FMfcgzGxRZkWzNqkTcbHsXdBvMcQwHpS",
    "https://example.com/reset/Zm9vYmFyYmF6cXV4",
    "https://user:pw@example.com:9000/a/b/c/?x=1",
    "http://10.0.0.5/printer/status",
    "https://example.co.uk/a%20b",
    "https://example.com/" + "word/".repeat(600),
    "https://example.com/" + "Ab3dE9xQ2zK/".repeat(300),
  ];
  for (const input of inputs) {
    const out = sanitizeUrl(input);
    assert.ok(passesContract(out), `${out}`);
    assert.ok(!/[?#]/.test(out), "no query or fragment");
    assert.ok(!out.includes("@"), "no userinfo or email");
  }
});

test("the contract check itself rejects an unclean URL (so the check above means something)", () => {
  assert.equal(passesContract("https://example.com/a?x=1"), false);
  assert.equal(passesContract("https://example.com/" + "a".repeat(40)), false);
  assert.equal(passesContract("https://example.com/a@b.com"), false);
  assert.equal(passesContract("http://localhost/"), false);
});

// ---- getDomain -------------------------------------------------------------------------------------------------------

test("getDomain: normal sites", () => {
  assert.equal(getDomain("www.youtube.com"), "youtube.com");
  assert.equal(getDomain("youtube.com"), "youtube.com");
  assert.equal(getDomain("m.facebook.com"), "facebook.com");
  assert.equal(getDomain("a.b.c.example.org"), "example.org");
  assert.equal(getDomain("en.wikipedia.org"), "wikipedia.org");
});

test("getDomain: co.uk and other UK suffixes", () => {
  assert.equal(getDomain("news.bbc.co.uk"), "bbc.co.uk");
  assert.equal(getDomain("bbc.co.uk"), "bbc.co.uk");
  assert.equal(getDomain("www.ox.ac.uk"), "ox.ac.uk");
  assert.equal(getDomain("www.gov.uk"), "www.gov.uk"); // gov.uk is a suffix here, so www.gov.uk is the site
});

test("getDomain: com.pk, edu.pk, org.pk, gov.pk, net.pk", () => {
  assert.equal(getDomain("www.daraz.com.pk"), "daraz.com.pk");
  assert.equal(getDomain("shop.daraz.com.pk"), "daraz.com.pk");
  assert.equal(getDomain("lums.edu.pk"), "lums.edu.pk");
  assert.equal(getDomain("portal.nust.edu.pk"), "nust.edu.pk");
  assert.equal(getDomain("www.example.org.pk"), "example.org.pk");
  assert.equal(getDomain("www.example.gov.pk"), "example.gov.pk");
  assert.equal(getDomain("www.example.net.pk"), "example.net.pk");
});

test("getDomain: other multi-part suffixes", () => {
  assert.equal(getDomain("www.amazon.co.in"), "amazon.co.in");
  assert.equal(getDomain("www.abc.net.au"), "abc.net.au");
  assert.equal(getDomain("shop.example.com.au"), "example.com.au");
  assert.equal(getDomain("www.example.co.jp"), "example.co.jp");
  assert.equal(getDomain("www.example.com.br"), "example.com.br");
  assert.equal(getDomain("www.example.co.za"), "example.co.za");
});

test("getDomain: a suffix on its own is not a site", () => {
  assert.equal(getDomain("co.uk"), null);
  assert.equal(getDomain("com.pk"), null);
});

test("getDomain: case, trailing dot and spaces", () => {
  assert.equal(getDomain("WWW.YouTube.COM"), "youtube.com");
  assert.equal(getDomain("www.youtube.com."), "youtube.com");
  assert.equal(getDomain("  news.BBC.co.uk "), "bbc.co.uk");
});

test("getDomain: IP addresses come back unchanged", () => {
  assert.equal(getDomain("192.168.1.10"), "192.168.1.10");
  assert.equal(getDomain("8.8.8.8"), "8.8.8.8");
});

test("getDomain: things that are not a plain hostname give null", () => {
  for (const bad of ["", " ", "localhost", "intranet", "example.com:8080", "https://example.com", "example.com/path", "user@example.com",
    "[::1]", "::1", "a..b.com", "-bad.com", "bad-.com", "under_score.com", null, undefined, 5, {}]) {
    assert.equal(getDomain(bad), null, String(bad));
  }
});

test("getDomain: the result passes the domain rule of the contract", () => {
  for (const host of ["www.youtube.com", "news.bbc.co.uk", "portal.nust.edu.pk", "192.168.1.10", "EXAMPLE.org"]) {
    assert.match(getDomain(host), domainPattern, host);
  }
});

test("getDomain agrees with sanitizeUrl on the same page", () => {
  const url = new URL(sanitizeUrl("https://User:pw@News.BBC.co.uk:8443/a?b=1"));
  assert.equal(url.hostname, "news.bbc.co.uk");
  assert.equal(getDomain(url.hostname), "bbc.co.uk");
});

// ---- hidden-site hook ------------------------------------------------------------------------------------------------

test("isHiddenSite is a hook that returns false for now", () => {
  assert.equal(isHiddenSite("youtube.com"), false);
  assert.equal(isHiddenSite("bank.example"), false);
  assert.equal(isHiddenSite(null), false);
  assert.equal(isHiddenSite(), false);
});
