// Guards for the rules of this app: no sample numbers in the shipped code, one place that reads data, no token in browser code.
// The values listed below are the sample values of design/dashboard-concept.html. They are only used here, to prove they are NOT in the app.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const FRONTEND = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

const isTestFile = (file) => /\.test\.(js|jsx)$/.test(file) || file.includes(`${path.sep}src${path.sep}test${path.sep}`);
// The scans read text. The platform icon files (.png) are pictures, so they are checked separately below.
const isTextFile = (file) => /\.(js|jsx|css|html|svg)$/i.test(file);

// Everything that ships or runs the app: the browser code, its page, and the dev server settings.
const SHIPPED = [
  ...walk(path.join(FRONTEND, "src")).filter((f) => !isTestFile(f) && isTextFile(f)),
  path.join(FRONTEND, "index.html"),
  path.join(FRONTEND, "vite.config.js"),
  ...walk(path.join(FRONTEND, "dev")).filter((f) => !isTestFile(f) && isTextFile(f)),
];
const BROWSER_CODE = SHIPPED.filter((f) => f.includes(`${path.sep}src${path.sep}`));
const read = (file) => fs.readFileSync(file, "utf8");
const name = (file) => path.relative(FRONTEND, file).replaceAll("\\", "/");

const CONCEPT_SAMPLE_VALUES = [
  "2h 17m", "0h 42m", "0h 50m", "0h 38m", "0h 27m", "0h 20m", "200 Reels", "42 reels", "28 shorts", "38 videos", "27 reels",
  "4h 20m", "3h 25m", "2h 45m", "1h 50m", "1h 18m", "85 reels", "70 shorts", "65 videos", "55 reels",
  "12h 48m", "11h 28m", "52h 14m", "49h 05m", "14h 20m", "12h 50m", "11h 30m", "8h 25m", "5h 39m",
  "280 reels", "240 shorts", "230 videos", "170 reels", "+1h 20m", "+3h 15m", "−1h 20m", "−3h 15m",
  "Saturday, 31 October", "Mon, 27 Oct", "Sun, 02 Nov", "Mon, 20 Oct", "Sun, 26 Oct", "Oct 1 – Oct 31", "Sep 1 – Sep 30",
  "11h 20m", "13h 40m", "11h 30m", "18h 45m", "10h 10m", "12h 15m", "9h 50m", "15h 30m", "2h 12m", "2h 05m", "1h 36m",
];

describe("no sample data in the app", () => {
  it("finds the files it is meant to check", () => {
    const names = SHIPPED.map(name);
    expect(names).toEqual(expect.arrayContaining(["src/dataSource.js", "src/model.js", "src/format.js", "src/App.jsx", "src/styles.css", "dev/agentBridge.js", "index.html", "vite.config.js"]));
    expect(names.some((n) => n.includes(".test."))).toBe(false);
    expect(names.some((n) => n.startsWith("design/"))).toBe(false);
  });

  it.each(CONCEPT_SAMPLE_VALUES)("does not contain the concept's sample value %s", (value) => {
    const found = SHIPPED.filter((file) => read(file).includes(value)).map(name);
    expect(found).toEqual([]);
  });

  it("contains no written-out time or count at all (those come from the agent's data)", () => {
    for (const file of SHIPPED) {
      const text = read(file);
      expect(text.match(/\b\d+h \d{1,2}m\b/g), `${name(file)} has a time`).toBeNull();
      expect(text.match(/\b\d+ (reels?|shorts?|videos?|items?)\b/gi), `${name(file)} has a count`).toBeNull();
    }
  });
});

describe("one place reads data", () => {
  it("only dataSource.js calls fetch (or any other network API)", () => {
    const callers = BROWSER_CODE.filter((f) => /\b(fetch|XMLHttpRequest|WebSocket|EventSource|sendBeacon)\b/.test(read(f))).map(name);
    expect(callers).toEqual(["src/dataSource.js"]);
  });

  it("the page only ever calls the /agent prefix", () => {
    const text = read(path.join(FRONTEND, "src", "dataSource.js"));
    expect(text).toContain('export const AGENT_BASE = "/agent";');
    expect(text.match(/https?:\/\//g)).toBeNull();
    expect(text).not.toMatch(/127\.0\.0\.1|localhost|49721/);
  });
});

describe("everything stays local: no remote images, no CDN, no external requests", () => {
  // The only web addresses allowed anywhere in the shipped files are this PC's own (the dev proxy to the backend and to the agent).
  const LOCAL_HOST = /^(127\.0\.0\.1|localhost|\[::1\]|\$\{AGENT_HOST\})(?=[:/]|$)/i;
  const addressesIn = (text) => [...text.matchAll(/https?:\/\/[^\s"'`)<>\\]+/gi)].map((m) => m[0]);

  it("every http(s) address in the shipped files is on this PC", () => {
    const remote = SHIPPED.flatMap((file) =>
      addressesIn(read(file))
        .filter((address) => !LOCAL_HOST.test(address.replace(/^https?:\/\//i, "")))
        .map((address) => `${name(file)}: ${address}`),
    );
    expect(remote).toEqual([]);
  });

  it("the page, the code and the stylesheet never point an image, script, style or font at a web address", () => {
    const REMOTE_SOURCES = [
      /\b(src|srcset|href|poster|data-src|xlink:href)\s*=\s*\{?\s*["'`]\s*(https?:)?\/\//i, // src="http://...", src="//cdn..."
      /url\(\s*["']?\s*(https?:)?\/\//i, // url(http://...), url(//cdn...)
      /@import\s+(url\()?\s*["']?\s*(https?:)?\/\//i,
      /\bimport\s*\(\s*["'`]\s*(https?:)?\/\//i, // import("http://...")
      /\bfrom\s+["']\s*(https?:)?\/\//i, // import x from "http://..."
      /\bnew\s+Image\s*\(/, // an image loaded from code
      /\bnavigator\.sendBeacon\b|\bnew\s+Worker\s*\(\s*["'`]https?:/i,
    ];
    for (const file of [...BROWSER_CODE, path.join(FRONTEND, "index.html")]) {
      const text = read(file);
      for (const pattern of REMOTE_SOURCES) expect(text, `${name(file)} matches ${pattern}`).not.toMatch(pattern);
    }
  });

  it("any image is imported as a local file (import.meta.glob of src/assets), never from a web address", () => {
    // the first argument of each import.meta.glob( ... ) is a string; it must be a pattern inside src/assets/platform-icons
    const globs = BROWSER_CODE.flatMap((file) =>
      [...read(file).matchAll(/import\.meta\.glob\(\s*(["'`])(.*?)\1/g)].map((m) => ({ file: name(file), pattern: m[2] })),
    );
    expect(globs.length).toBeGreaterThan(0);
    for (const { file, pattern } of globs) {
      expect(pattern, file).toMatch(/^\.\/assets\/platform-icons\/[^:]*$/);
      expect(pattern, file).not.toMatch(/https?:|\/\/|\.\./);
    }
  });

  const ICON_DIR = path.join(FRONTEND, "src", "assets", "platform-icons");
  const iconFiles = fs.existsSync(ICON_DIR) ? fs.readdirSync(ICON_DIR) : [];

  it("the icon folder holds only png and svg files, and every png really is a png", () => {
    for (const file of iconFiles) {
      expect(file, "an icon file").toMatch(/\.(png|svg)$/i);
      if (/\.png$/i.test(file)) {
        const head = fs.readFileSync(path.join(ICON_DIR, file)).subarray(0, 8);
        expect([...head], `${file} should start with the PNG signature`).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
      }
    }
  });

  it("an svg icon, if there is one, loads nothing from outside itself", () => {
    for (const file of iconFiles.filter((f) => /\.svg$/i.test(f))) {
      const text = fs.readFileSync(path.join(ICON_DIR, file), "utf8");
      expect(text, file).not.toMatch(/<script|<foreignObject|<iframe|@import/i);
      expect(text, file).not.toMatch(/(xlink:)?href\s*=\s*["']\s*(?!#)/i); // only references inside the file ("#id")
      expect(text, file).not.toMatch(/url\(\s*["']?\s*(?!#)/i);
    }
  });
});

describe("the token stays out of the browser", () => {
  it("no browser file names the token, its header or its file", () => {
    for (const file of BROWSER_CODE) {
      const text = read(file);
      expect(text, name(file)).not.toMatch(/ui-access/i);
      expect(text, name(file)).not.toMatch(/X-DLA-UI-Token/i);
      expect(text, name(file)).not.toMatch(/LOCALAPPDATA/i);
      expect(text, name(file)).not.toMatch(/DigitalLifeAnalyzer/);
    }
  });

  it("browser code never imports the dev bridge or any Node module", () => {
    for (const file of BROWSER_CODE) {
      const text = read(file);
      // import statements only (a comment may mention the bridge)
      const imports = text.match(/^\s*import\b[^;]*;?$/gm) ?? [];
      for (const line of imports) {
        expect(line, name(file)).not.toMatch(/agentBridge|dev\//);
        expect(line, name(file)).not.toMatch(/["']node:/);
      }
    }
  });
});
