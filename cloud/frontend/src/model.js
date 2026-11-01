// Turns the agent's report data into what the screens show. Pure functions: every number comes from the data passed in.
// The only fixed things here are names, icons and colours of the four supported platforms.

import { countWord, daysBetween, formatDuration, formatDurationShort } from "./format.js";

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

// The bar charts have a FIXED real-time scale: a full-height bar is the most time that fits in what the bar stands for,
// whatever the data says. So a few minutes is always a tiny bar and 12 hours of a day is always half the chart.
/** One bar of the weekly chart is one day: 24 hours. */
export const WEEKLY_CHART_CAPACITY_SECONDS = 24 * 60 * 60;
/** One bar of the monthly chart is one week block (7 days): 168 hours. */
export const MONTHLY_CHART_CAPACITY_SECONDS = 7 * 24 * 60 * 60;
/** The labelled gridlines, as fractions of the capacity: 0, a quarter, a half, three quarters, full. */
export const GRIDLINE_FRACTIONS = [0, 0.25, 0.5, 0.75, 1];

const isTime = (v) => Number.isFinite(v) && v > 0;

/**
 * Bar heights as a percentage of the chart (0 to 100): seconds / capacity. A bar with no time has height 0 (and is not
 * drawn). A bar can never be taller than the chart: more than the capacity is cut to 100.
 */
export function barHeights(values, capacitySeconds) {
  return values.map((v) => (isTime(v) ? Math.min(100, (v / capacitySeconds) * 100) : 0));
}

/** The gridlines of a chart: for each fraction, its place (0 to 1), the time it stands for and the label for that time. */
export function gridlines(capacitySeconds) {
  return GRIDLINE_FRACTIONS.map((fraction) => {
    const seconds = fraction * capacitySeconds;
    return { fraction, seconds, label: formatDurationShort(seconds) };
  });
}

/**
 * The donut: each part's share of the time on the cards. The whole numbers for the legend always add up to 100
 * (the largest remainders get the leftover points). With no time at all every share is 0 and nothing divides by zero.
 */
export function donutShares(cards) {
  const total = cards.reduce((sum, c) => sum + (c.seconds > 0 ? c.seconds : 0), 0);
  if (!(total > 0)) {
    return { total: 0, parts: cards.map((c) => ({ key: c.key, name: c.name, icon: c.icon, seconds: 0, share: 0, percent: 0 })) };
  }
  const parts = cards.map((c) => {
    const seconds = c.seconds > 0 ? c.seconds : 0;
    return { key: c.key, name: c.name, icon: c.icon, seconds, share: seconds / total, percent: Math.floor((seconds / total) * 100) };
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

/**
 * How many days of an in-progress period have gone by, counting today (1 on its first day). null when it can not be told:
 * the period has no day list, or today is outside it (for example the clock of this page and the agent disagree).
 */
export function elapsedDays(report, today) {
  const n = daysBetween(report.firstDay, today) + 1;
  return Number.isInteger(n) && n >= 1 && n <= report.days.length ? n : null;
}

/**
 * Compares the shown period with the other one. Two whole periods are compared as they are, unless one of them is still
 * in progress: then the comparison is fair. Only the first M days of BOTH are compared, where M is the number of days
 * that have gone by in the period in progress (and no more than either period has days). The days come from the
 * per-day lists of both reports.
 *
 * Returns { comparison, days }. `comparison` is compareTotals() or null (hidden); `days` is M for a fair comparison,
 * or null when whole periods were compared (also when a fair comparison was wanted but the day lists or today were missing).
 */
export function comparePeriods(shown, other, today) {
  const whole = { comparison: compareTotals(shown.totalSeconds, other.totalSeconds), days: null };
  const running = [shown, other].find((report) => report.status === "InProgress");
  if (!running) return whole;

  const elapsed = elapsedDays(running, today);
  if (elapsed === null || other.days.length === 0 || shown.days.length === 0) return whole;

  const days = Math.min(elapsed, shown.days.length, other.days.length);
  const firstDays = (report) => report.days.slice(0, days).reduce((sum, d) => sum + (isTime(d.totalSeconds) ? d.totalSeconds : 0), 0);
  return { comparison: compareTotals(firstDays(shown), firstDays(other)), days };
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
