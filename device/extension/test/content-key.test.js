import { test } from "node:test";
import assert from "node:assert/strict";
import { contentKey, isPublicVideoPage } from "../content-key.js";

test("YouTube watch, Shorts, live, embed and youtu.be give a key", () => {
  assert.equal(contentKey("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=5"), "video:dQw4w9WgXcQ");
  assert.equal(contentKey("https://music.youtube.com/watch?v=dQw4w9WgXcQ"), "video:dQw4w9WgXcQ");
  assert.equal(contentKey("https://www.youtube.com/shorts/mF2kqwe91Zc"), "short:mF2kqwe91Zc");
  assert.equal(contentKey("https://youtu.be/dQw4w9WgXcQ"), "video:dQw4w9WgXcQ");
  assert.equal(contentKey("https://www.youtube.com/live/abcDEF12345"), "video:abcDEF12345");
});

test("everything else gives null", () => {
  for (const u of [
    "https://www.youtube.com/", "https://www.youtube.com/feed/subscriptions", "https://www.youtube.com/watch",
    "https://www.youtube.com/shorts/", "https://example.com/shorts/mF2kqwe91Zc", "https://evil-youtube.com/watch?v=dQw4w9WgXcQ",
    "https://www.youtube.com.evil.test/watch?v=dQw4w9WgXcQ", "not a url", "chrome://settings", "https://www.youtube.com/watch?v=a b",
  ]) assert.equal(contentKey(u), null, u);
});

test("only video pages count as public video pages", () => {
  assert.equal(isPublicVideoPage("https://www.youtube.com/shorts/mF2kqwe91Zc"), true);
  assert.equal(isPublicVideoPage("https://mail.example.com/reset/abcdef"), false);
});
