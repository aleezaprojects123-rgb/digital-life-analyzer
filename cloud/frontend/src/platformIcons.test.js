// TEST DATA below is made up for these tests (file names and addresses that do not exist). It is not used by the app.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { buildIconMap, PLATFORM_ICON_NAMES, platformIconUrl } from "./platformIcons.js";

const ICON_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "assets", "platform-icons");
const dir = "./assets/platform-icons/";

describe("buildIconMap (which file is which platform's icon)", () => {
  it("maps the four platform names to the files that exist", () => {
    const map = buildIconMap({
      [`${dir}instagram.png`]: "/fixture/a.png",
      [`${dir}youtube.png`]: "/fixture/b.png",
      [`${dir}tiktok.png`]: "/fixture/c.png",
      [`${dir}facebook.png`]: "/fixture/d.png",
    });
    expect(map).toEqual({ instagram: "/fixture/a.png", youtube: "/fixture/b.png", tiktok: "/fixture/c.png", facebook: "/fixture/d.png" });
  });

  it("has no entry for a file that is missing (so the generic icon is shown)", () => {
    const map = buildIconMap({ [`${dir}youtube.png`]: "/fixture/b.png" });
    expect(map).toEqual({ youtube: "/fixture/b.png" });
    expect(Object.hasOwn(map, "instagram")).toBe(false);
  });

  it("is an empty map, and does not throw, when there are no files at all", () => {
    expect(buildIconMap({})).toEqual({});
    expect(buildIconMap(undefined)).toEqual({});
    expect(buildIconMap(null)).toEqual({});
  });

  it("accepts png and svg, whatever the letter case of the name or the extension", () => {
    const map = buildIconMap({ [`${dir}TikTok.SVG`]: "/fixture/t.svg", [`${dir}Facebook.PNG`]: "/fixture/f.png" });
    expect(map).toEqual({ tiktok: "/fixture/t.svg", facebook: "/fixture/f.png" });
  });

  it("prefers an svg when both an svg and a png exist, in either order", () => {
    expect(buildIconMap({ [`${dir}instagram.png`]: "/fixture/p.png", [`${dir}instagram.svg`]: "/fixture/s.svg" }).instagram).toBe("/fixture/s.svg");
    expect(buildIconMap({ [`${dir}instagram.svg`]: "/fixture/s.svg", [`${dir}instagram.png`]: "/fixture/p.png" }).instagram).toBe("/fixture/s.svg");
  });

  it("ignores any name that is not one of the four platforms, so 'Other sites' can never get a file", () => {
    const map = buildIconMap({ [`${dir}other.png`]: "/fixture/o.png", [`${dir}reddit.png`]: "/fixture/r.png", [`${dir}instagram-old.png`]: "/fixture/x.png" });
    expect(map).toEqual({});
    expect(PLATFORM_ICON_NAMES).toEqual(["instagram", "youtube", "tiktok", "facebook"]);
  });

  it("ignores other file types and entries that have no usable address", () => {
    const map = buildIconMap({
      [`${dir}instagram.gif`]: "/fixture/a.gif",
      [`${dir}youtube.png`]: "",
      [`${dir}tiktok.png`]: undefined,
      [`${dir}facebook.png`]: { default: "/fixture/not-a-string" },
      "no-extension": "/fixture/z",
    });
    expect(map).toEqual({});
  });

  it("reads the name from a Windows path too", () => {
    expect(buildIconMap({ "C:\\app\\assets\\platform-icons\\youtube.png": "/fixture/y.png" })).toEqual({ youtube: "/fixture/y.png" });
  });
});

describe("platformIconUrl with the real folder src/assets/platform-icons", () => {
  const present = (fs.existsSync(ICON_DIR) ? fs.readdirSync(ICON_DIR) : [])
    .map((file) => /^(.+)\.(png|svg)$/i.exec(file)?.[1].toLowerCase())
    .filter((base) => PLATFORM_ICON_NAMES.includes(base));

  it("gives a local address for every platform whose file is in the folder, and nothing for the others", () => {
    for (const platform of PLATFORM_ICON_NAMES) {
      const url = platformIconUrl(platform);
      if (present.includes(platform)) expect(typeof url, `${platform} has a file`).toBe("string");
      else expect(url, `${platform} has no file`).toBeUndefined();
    }
  });

  it("never gives an address on the internet (local files only)", () => {
    for (const platform of PLATFORM_ICON_NAMES) {
      const url = platformIconUrl(platform);
      if (url) {
        expect(url).not.toMatch(/^(https?:)?\/\//i);
        expect(url).toMatch(/\.(png|svg)(\?.*)?$|^data:image\//i);
      }
    }
  });

  it("gives nothing for 'other' or for a platform that is not one of the four", () => {
    for (const name of ["other", "reddit", "", "constructor", "__proto__", "toString", undefined, null]) {
      expect(platformIconUrl(name)).toBeUndefined();
    }
  });
});
