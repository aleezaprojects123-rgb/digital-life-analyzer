// Turns the agent's report data into what the screens show. Pure functions: every number comes from the data passed in.
// The only fixed things here are names, icons and colours of the four supported platforms.

import { countWord, formatDuration } from "./format.js";

export const PLATFORM_NAMES = {
  instagram: "Instagram",
  youtube: "YouTube",
  tiktok: "TikTok",
  facebook: "Facebook",
};

// Generic shapes, not brand logos.
export const PLATFORM_ICONS = { instagram: "◎", youtube: "▶", tiktok: "♪", facebook: "◈" };
export const OTHER_ICON = "▦";

// What each platform's content is called, in the order it is shown. The keys are the ones of GET /dashboard/today.
const CONTENT_WORDS = {
  instagram: [["reels", "reel"]],
  youtube: [["shorts", "short"], ["videos", "video"]],
  tiktok: [["videos", "video"]],
  facebook: [["reels", "reel"]],
};

// The agent's report lists content as (platform, activityType). This is how that maps to the same keys.
const REPORT_CONTENT_KEY = {
  "instagram/reel": "reels",
  "youtube/short_video": "shorts",
  "youtube/video": "videos",
  "tiktok/short_video": "videos",
  "facebook/reel": "reels",
};

/** Colours of the donut, in the order of the platforms and then "Other". They are CSS variables of the theme. */
export const DONUT_COLORS = ["var(--accent)", "var(--teal)", "var(--purple)", "var(--pink)", "#9ca9bb"];

export function platformName(platform) {
  return PLATFORM_NAMES[platform] ?? platform.charAt(0).toUpperCase() + platform.slice(1);
}

/** The counts of a platform in words, for example "<n> reels"; YouTube's shorts and videos are shown separately. */
export function contentText(platform, content) {
  const words = CONTENT_WORDS[platform];
  if (!words) return "";
  return words.map(([key, singular]) => countWord(content[key] ?? 0, singular)).join(" · ");
}

/** Cards for the platform row. Today's data already has the content per platform. */
export function cardsFromToday(today) {
  const cards = today.platforms.map((p) => ({
    key: p.platform,
    name: platformName(p.platform),
    icon: PLATFORM_ICONS[p.platform] ?? OTHER_ICON,
    seconds: p.seconds,
    detail: contentText(p.platform, p.content),
    content: p.content,
  }));
  return [...cards, otherCard(today.otherSeconds)];
}

/** Cards from a weekly or monthly report: the same cards, with the content picked out of the report's content list. */
export function cardsFromReport(report) {
  const cards = report.platforms.map((p) => {
    const content = {};
    for (const stat of report.content) {
      const key = REPORT_CONTENT_KEY[`${stat.platform}/${stat.activityType}`];
      if (stat.platform === p.platform && key) content[key] = (content[key] ?? 0) + stat.count;
    }
    return {
      key: p.platform,
      name: platformName(p.platform),
      icon: PLATFORM_ICONS[p.platform] ?? OTHER_ICON,
      seconds: p.seconds,
      detail: contentText(p.platform, content),
      content,
    };
  });
  return [...cards, otherCard(report.otherSeconds)];
}

function otherCard(seconds) {
  return { key: "other", name: "Other sites", icon: OTHER_ICON, seconds, detail: "Combined time", content: {} };
}

/** All content counted on the cards (reels, shorts and videos together). */
export function totalContent(cards) {
  return cards.reduce((sum, card) => sum + Object.values(card.content).reduce((a, b) => a + b, 0), 0);
}

/** "<n> items in <time>", built from the real totals. null (so the line is hidden) when nothing was counted. */
export function highlightText(cards, totalSeconds) {
  const items = totalContent(cards);
  if (!(items > 0)) return null;
  return `${countWord(items, "item")} in ${formatDuration(totalSeconds)}`;
}

/** Bar heights as a percentage of the largest bar (the largest is 100). All zero when there is nothing to compare. */
export function barHeights(values) {
  const largest = Math.max(0, ...values.map((v) => (Number.isFinite(v) && v > 0 ? v : 0)));
  if (largest === 0) return values.map(() => 0);
  return values.map((v) => (Number.isFinite(v) && v > 0 ? (v / largest) * 100 : 0));
}

/**
 * The donut: each part's share of the time on the cards. The whole numbers for the legend always add up to 100
 * (the largest remainders get the leftover points). With no time at all every share is 0 and nothing divides by zero.
 */
export function donutShares(cards) {
  const total = cards.reduce((sum, c) => sum + (c.seconds > 0 ? c.seconds : 0), 0);
  if (!(total > 0)) {
    return { total: 0, parts: cards.map((c) => ({ key: c.key, name: c.name, seconds: 0, share: 0, percent: 0 })) };
  }
  const parts = cards.map((c) => {
    const seconds = c.seconds > 0 ? c.seconds : 0;
    return { key: c.key, name: c.name, seconds, share: seconds / total, percent: Math.floor((seconds / total) * 100) };
  });
  let leftover = 100 - parts.reduce((sum, p) => sum + p.percent, 0);
  const byRemainder = parts
    .map((p, index) => ({ index, remainder: p.share * 100 - p.percent }))
    .sort((a, b) => b.remainder - a.remainder || a.index - b.index);
  for (const { index } of byRemainder) {
    if (leftover <= 0) break;
    if (parts[index].seconds > 0) {
      parts[index].percent += 1;
      leftover -= 1;
    }
  }
  return { total, parts };
}

/** The CSS for the donut ring. The exact shares draw the ring; an empty ring is one neutral colour. */
export function conicGradient(parts, colors = DONUT_COLORS) {
  const used = parts.filter((p) => p.share > 0);
  if (used.length === 0) return "conic-gradient(var(--line) 0 100%)";
  let from = 0;
  const stops = parts.map((p, i) => {
    const start = from;
    from += p.share * 100;
    return p.share > 0 ? `${colors[i % colors.length]} ${start}% ${from}%` : null;
  });
  return `conic-gradient(${stops.filter(Boolean).join(", ")})`;
}

/**
 * The comparison line: how this period differs from the other one. null (hidden) when the other period is missing or empty.
 * `direction` is "up", "down" or "same" (a difference that rounds to 0 minutes is "same").
 */
export function compareTotals(shownSeconds, otherSeconds) {
  if (!Number.isFinite(shownSeconds) || !Number.isFinite(otherSeconds) || !(otherSeconds > 0)) return null;
  const diff = shownSeconds - otherSeconds;
  if (Math.round(Math.abs(diff) / 60) === 0) return { direction: "same", diffSeconds: 0, text: "No change" };
  const direction = diff > 0 ? "up" : "down";
  const sign = direction === "up" ? "+" : "−";
  return { direction, diffSeconds: Math.abs(diff), text: `${sign}${formatDuration(Math.abs(diff))}` };
}

const STATUS = {
  InProgress: { label: "In progress", tone: "live" },
  Completed: { label: "Completed", tone: "done" },
  Upcoming: { label: "Upcoming", tone: "soon" },
};

/** The badge for a report's status, or null for a word the screen does not know (the badge is then hidden). */
export function statusBadge(status) {
  return STATUS[status] ?? null;
}
