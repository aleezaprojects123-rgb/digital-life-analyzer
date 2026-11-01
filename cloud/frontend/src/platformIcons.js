// Platform icons. They are LOCAL files in src/assets/platform-icons/, named after the platform: instagram, youtube, tiktok,
// facebook, as .png or .svg. This code does not draw or create any logo, and nothing is loaded from the internet.
//
// import.meta.glob lists whichever of those files exist when the page is built. If a file is missing it is simply not in
// the list (a build never fails because of it), and the screens keep the generic icon for that platform.

export const PLATFORM_ICON_NAMES = ["instagram", "youtube", "tiktok", "facebook"];

// { "./assets/platform-icons/instagram.png": "<url of the file>", ... }, one entry per file that exists.
const found = import.meta.glob("./assets/platform-icons/*.{png,svg}", { eager: true, query: "?url", import: "default" });

/**
 * Turns the list of files into { platform: url }. Only the four platform names are used (so "Other sites" can never get a
 * file), a name is matched whatever its letter case, and an .svg is preferred when both an .svg and a .png exist.
 * `modules` is { filePath: url }; it is a parameter so the rules can be tested without real files.
 */
export function buildIconMap(modules, names = PLATFORM_ICON_NAMES) {
  const map = {};
  for (const [file, url] of Object.entries(modules ?? {})) {
    const match = /([^/\\]+)\.(png|svg)$/i.exec(file);
    if (!match || typeof url !== "string" || url === "") continue;
    const name = match[1].toLowerCase();
    if (!names.includes(name)) continue;
    if (!Object.hasOwn(map, name) || match[2].toLowerCase() === "svg") map[name] = url;
  }
  return map;
}

const ICONS = buildIconMap(found);

/** The url of the platform's icon file, or undefined when there is none (then the generic icon is shown). */
export function platformIconUrl(platform) {
  return Object.hasOwn(ICONS, platform) ? ICONS[platform] : undefined;
}
