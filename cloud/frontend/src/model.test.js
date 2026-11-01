// TEST DATA below is made up for these tests. It is not real activity and it is not used by the app.
import { describe, expect, it } from "vitest";
import {
  barHeights, cardsFromReport, cardsFromToday, compareTotals, comparePeriods, conicGradient, contentText, donutShares, GRIDLINE_FRACTIONS, gridlines,
  elapsedDays, highlightText, MONTHLY_CHART_CAPACITY_SECONDS, statusBadge, totalContent, WEEKLY_CHART_CAPACITY_SECONDS,
} from "./model.js";
import * as model from "./model.js";

// everything model.js exports, to prove the old minimum-scale rule is gone
const modelExports = Object.keys(model);

// TEST DATA: today, in the shape of dataSource.parseToday().
const testToday = {
  date: "2026-10-31",
  totalSeconds: 8100,
  otherSeconds: 900,
  platforms: [
    { platform: "instagram", seconds: 3600, content: { reels: 20 } },
    { platform: "youtube", seconds: 1800, content: { shorts: 5, videos: 1 } },
    { platform: "tiktok", seconds: 1800, content: { videos: 10 } },
    { platform: "facebook", seconds: 0, content: { reels: 0 } },
  ],
};

describe("the fixed bar scale (seconds / capacity)", () => {
  const DAY = WEEKLY_CHART_CAPACITY_SECONDS;
  const WEEK = MONTHLY_CHART_CAPACITY_SECONDS;

  it("has two named capacities: 24 hours for a day and 7 days (168 hours) for a week block", () => {
    expect(WEEKLY_CHART_CAPACITY_SECONDS).toBe(24 * 3600);
    expect(WEEKLY_CHART_CAPACITY_SECONDS).toBe(86400);
    expect(MONTHLY_CHART_CAPACITY_SECONDS).toBe(168 * 3600);
    expect(MONTHLY_CHART_CAPACITY_SECONDS).toBe(604800);
  });

  it("no longer has the old minimum-scale rule", () => {
    expect(modelExports).not.toContain("MIN_SCALE_SECONDS");
    expect(modelExports).not.toContain("chartMax");
  });

  it("makes 1h 23m of a day about 5.8 percent of the chart height", () => {
    const [height] = barHeights([83 * 60], DAY);
    expect(height).toBeCloseTo(5.76, 2);
    expect(height).toBeGreaterThan(5.7);
    expect(height).toBeLessThan(5.8);
  });

  it("makes 12 hours of a day exactly half", () => {
    expect(barHeights([12 * 3600], DAY)).toEqual([50]);
  });

  it("makes 24 hours of a day exactly full height", () => {
    expect(barHeights([24 * 3600], DAY)).toEqual([100]);
  });

  it("makes 3 minutes of a day tiny (about 0.2 percent) but above 0", () => {
    const [height] = barHeights([180], DAY);
    expect(height).toBeCloseTo((180 / 86400) * 100, 10);
    expect(height).toBeGreaterThan(0);
    expect(height).toBeLessThan(0.5);
  });

  it("does not depend on the other bars: a lone small day is not scaled up", () => {
    expect(barHeights([180], DAY)).toEqual([barHeights([180, 0, 0], DAY)[0]]);
    expect(barHeights([180, 18000], DAY)[0]).toBe(barHeights([180], DAY)[0]);
  });

  it("makes a quarter of the capacity a quarter of the chart, for a day and for a week block", () => {
    expect(barHeights([6 * 3600], DAY)).toEqual([25]);
    expect(barHeights([42 * 3600], WEEK)).toEqual([25]);
  });

  it("makes 42 hours, 84 hours and 168 hours of a week block a quarter, a half and full", () => {
    expect(barHeights([42 * 3600, 84 * 3600, 126 * 3600, 168 * 3600], WEEK)).toEqual([25, 50, 75, 100]);
  });

  it("keeps the ratio between bars", () => {
    const [a, b] = barHeights([9 * 3600, 3 * 3600], DAY);
    expect(a / b).toBeCloseTo(3, 10);
  });

  it("caps a bar at full height when the data is above the capacity", () => {
    expect(barHeights([25 * 3600], DAY)).toEqual([100]);
    expect(barHeights([48 * 3600, 1e9], DAY)).toEqual([100, 100]);
    expect(barHeights([169 * 3600], WEEK)).toEqual([100]);
    for (const seconds of [DAY + 1, DAY * 2, 1e12]) expect(barHeights([seconds], DAY)[0]).toBeLessThanOrEqual(100);
  });

  it("gives a bar with no time height 0, and all-zero or empty data no height at all", () => {
    expect(barHeights([0, 0, 0], DAY)).toEqual([0, 0, 0]);
    expect(barHeights([7200, 0], DAY)[1]).toBe(0);
    expect(barHeights([], DAY)).toEqual([]);
  });

  it("ignores values that are not positive numbers", () => {
    expect(barHeights([43200, -50, NaN, Infinity, undefined, null, "9"], DAY)).toEqual([50, 0, 0, 0, 0, 0, 0]);
  });
});

describe("gridlines (0, a quarter, a half, three quarters, full capacity)", () => {
  it("are at the five fractions", () => {
    expect(GRIDLINE_FRACTIONS).toEqual([0, 0.25, 0.5, 0.75, 1]);
    expect(gridlines(WEEKLY_CHART_CAPACITY_SECONDS).map((g) => g.fraction)).toEqual([0, 0.25, 0.5, 0.75, 1]);
  });

  it("are labelled 0, 6h, 12h, 18h and 24h for the weekly chart", () => {
    const lines = gridlines(WEEKLY_CHART_CAPACITY_SECONDS);
    expect(lines.map((g) => g.seconds)).toEqual([0, 6 * 3600, 12 * 3600, 18 * 3600, 24 * 3600]);
    expect(lines.map((g) => g.label)).toEqual(["0m", "6h 00m", "12h 00m", "18h 00m", "24h 00m"]);
  });

  it("are labelled 0, 42h, 84h, 126h and 168h for the monthly chart", () => {
    const lines = gridlines(MONTHLY_CHART_CAPACITY_SECONDS);
    expect(lines.map((g) => g.seconds)).toEqual([0, 42 * 3600, 84 * 3600, 126 * 3600, 168 * 3600]);
    expect(lines.map((g) => g.label)).toEqual(["0m", "42h 00m", "84h 00m", "126h 00m", "168h 00m"]);
  });

  it("use the existing time formatter and follow whatever capacity is given", () => {
    const lines = gridlines(4 * 3600);
    expect(lines.map((g) => g.label)).toEqual(["0m", "1h 00m", "2h 00m", "3h 00m", "4h 00m"]);
  });

  it("put a bar of exactly a gridline's time on that gridline", () => {
    const lines = gridlines(WEEKLY_CHART_CAPACITY_SECONDS);
    const heights = barHeights(lines.map((g) => g.seconds), WEEKLY_CHART_CAPACITY_SECONDS);
    expect(heights).toEqual(lines.map((g) => g.fraction * 100));
  });
});

// TEST DATA: reports reduced to what comparePeriods() reads (status, dates, totals and the per-day list).
const dayList = (firstDay, seconds) =>
  seconds.map((totalSeconds, i) => ({ date: `${firstDay.slice(0, 8)}${String(Number(firstDay.slice(8)) + i).padStart(2, "0")}`, dayOfWeek: "", totalSeconds }));
const period = (firstDay, status, seconds) => ({
  firstDay,
  lastDay: "",
  status,
  totalSeconds: seconds.reduce((a, b) => a + b, 0),
  days: dayList(firstDay, seconds),
});

describe("elapsedDays", () => {
  const week = period("2026-10-26", "InProgress", [0, 0, 0, 0, 0, 0, 0]);

  it("counts the days that have gone by, today included", () => {
    expect(elapsedDays(week, "2026-10-26")).toBe(1);
    expect(elapsedDays(week, "2026-10-28")).toBe(3);
    expect(elapsedDays(week, "2026-11-01")).toBe(7);
  });

  it("is null when today is outside the period, or the period has no day list", () => {
    expect(elapsedDays(week, "2026-10-25")).toBeNull();
    expect(elapsedDays(week, "2026-11-02")).toBeNull();
    expect(elapsedDays(week, "nonsense")).toBeNull();
    expect(elapsedDays({ ...week, days: [] }, "2026-10-28")).toBeNull();
  });
});

describe("comparePeriods (a fair comparison while one period is in progress)", () => {
  // this week, Monday to Sunday, with 3 days gone by on 2026-10-28
  const thisWeek = period("2026-10-26", "InProgress", [3600, 7200, 3600, 0, 0, 0, 0]);
  // last week had a very busy Sunday, which this week has not reached yet
  const lastWeek = period("2026-10-19", "Completed", [3600, 3600, 3600, 3600, 0, 0, 36000]);

  it("compares only the days gone by so far with the same first days of the other period", () => {
    // this week so far: 14400. First 3 days of last week: 10800. (The whole of last week is 54000.)
    const result = comparePeriods(thisWeek, lastWeek, "2026-10-28");
    expect(result.days).toBe(3);
    expect(result.comparison).toEqual({ direction: "up", diffSeconds: 3600, text: "+1h 00m" });
  });

  it("is different from comparing the whole periods", () => {
    const whole = compareTotals(thisWeek.totalSeconds, lastWeek.totalSeconds);
    expect(whole.direction).toBe("down");
    expect(comparePeriods(thisWeek, lastWeek, "2026-10-28").comparison.direction).toBe("up");
  });

  it("grows with the days: on the 6th day the first 6 days are compared", () => {
    const later = period("2026-10-26", "InProgress", [3600, 7200, 3600, 0, 7200, 3600, 0]);
    const result = comparePeriods(later, lastWeek, "2026-10-31");
    expect(result.days).toBe(6);
    expect(result.comparison).toEqual({ direction: "up", diffSeconds: 10800, text: "+3h 00m" });
  });

  it("compares the first day alone on the first day", () => {
    const result = comparePeriods(period("2026-10-26", "InProgress", [1800, 0, 0, 0, 0, 0, 0]), lastWeek, "2026-10-26");
    expect(result.days).toBe(1);
    expect(result.comparison).toEqual({ direction: "down", diffSeconds: 1800, text: "−0h 30m" });
  });

  it("works the same way round when the finished period is the one shown", () => {
    const result = comparePeriods(lastWeek, thisWeek, "2026-10-28");
    expect(result.days).toBe(3);
    expect(result.comparison).toEqual({ direction: "down", diffSeconds: 3600, text: "−1h 00m" });
  });

  it("never compares more days than either period has (a short previous month)", () => {
    const october = period("2026-10-01", "InProgress", Array(31).fill(3600));
    const september = period("2026-09-01", "Completed", Array(30).fill(1800));
    const result = comparePeriods(october, september, "2026-10-31");
    expect(result.days).toBe(30);
    expect(result.comparison.diffSeconds).toBe(30 * 1800);
  });

  it("hides the comparison when the other period has no time in those first days", () => {
    const quietStart = period("2026-10-19", "Completed", [0, 0, 0, 0, 3600, 0, 0]);
    const result = comparePeriods(thisWeek, quietStart, "2026-10-28");
    expect(result.days).toBe(3);
    expect(result.comparison).toBeNull();
  });

  it("compares whole periods when both are finished", () => {
    const done = period("2026-10-26", "Completed", [3600, 3600, 0, 0, 0, 0, 0]);
    const result = comparePeriods(done, lastWeek, "2026-11-20");
    expect(result.days).toBeNull();
    expect(result.comparison).toEqual(compareTotals(done.totalSeconds, lastWeek.totalSeconds));
  });

  it("falls back to whole periods when the day list or today is missing", () => {
    const whole = compareTotals(thisWeek.totalSeconds, lastWeek.totalSeconds);
    expect(comparePeriods({ ...thisWeek, days: [] }, lastWeek, "2026-10-28")).toEqual({ comparison: whole, days: null });
    expect(comparePeriods(thisWeek, { ...lastWeek, days: [] }, "2026-10-28")).toEqual({ comparison: whole, days: null });
    expect(comparePeriods(thisWeek, lastWeek, "2026-12-25")).toEqual({ comparison: whole, days: null });
    expect(comparePeriods(thisWeek, lastWeek, "nonsense")).toEqual({ comparison: whole, days: null });
  });

  it("ignores a bad number in a day list instead of breaking", () => {
    const messy = { ...thisWeek, days: thisWeek.days.map((d, i) => (i === 1 ? { ...d, totalSeconds: NaN } : d)) };
    expect(comparePeriods(messy, lastWeek, "2026-10-28").comparison.diffSeconds).toBe(10800 - 7200);
  });
});

describe("donutShares", () => {
  const cards = (...seconds) => seconds.map((s, i) => ({ key: `p${i}`, name: `P${i}`, seconds: s }));

  it("gives each part its exact share of the time and a whole percent", () => {
    const { total, parts } = donutShares(cards(3600, 1800, 1800));
    expect(total).toBe(7200);
    expect(parts.map((p) => p.share)).toEqual([0.5, 0.25, 0.25]);
    expect(parts.map((p) => p.percent)).toEqual([50, 25, 25]);
  });

  it("makes the whole percents add up to exactly 100 (the largest remainder gets the leftover)", () => {
    expect(donutShares(cards(1, 1, 1)).parts.map((p) => p.percent)).toEqual([34, 33, 33]);
    const odd = donutShares(cards(3600, 1800, 1800, 0, 900)).parts.map((p) => p.percent);
    expect(odd).toEqual([45, 22, 22, 0, 11]);
    expect(odd.reduce((a, b) => a + b, 0)).toBe(100);
  });

  it("never gives time to a part that has none", () => {
    expect(donutShares(cards(1, 0, 0)).parts.map((p) => p.percent)).toEqual([100, 0, 0]);
  });

  it("handles a total of 0: every share is 0 and nothing is NaN", () => {
    const { total, parts } = donutShares(cards(0, 0, 0));
    expect(total).toBe(0);
    expect(parts.every((p) => p.share === 0 && p.percent === 0)).toBe(true);
    expect(donutShares([]).total).toBe(0);
  });

  it("ignores negative or missing seconds", () => {
    expect(donutShares(cards(60, -30, NaN)).parts.map((p) => p.percent)).toEqual([100, 0, 0]);
  });
});

describe("conicGradient", () => {
  const colors = ["A", "B", "C", "D", "E"];
  const parts = (shares) => shares.map((share, i) => ({ share, key: String(i) }));

  it("draws each part from where the last one ended", () => {
    expect(conicGradient(parts([0.5, 0.25, 0.25]), colors)).toBe("conic-gradient(A 0% 50%, B 50% 75%, C 75% 100%)");
  });

  it("leaves out parts with no time", () => {
    expect(conicGradient(parts([0.5, 0, 0.5]), colors)).toBe("conic-gradient(A 0% 50%, C 50% 100%)");
  });

  it("draws one neutral ring when there is no time", () => {
    expect(conicGradient(parts([0, 0, 0]), colors)).toBe("conic-gradient(var(--line) 0 100%)");
    expect(conicGradient([], colors)).toBe("conic-gradient(var(--line) 0 100%)");
  });
});

describe("compareTotals", () => {
  it("says how much more time this period has", () => {
    expect(compareTotals(21600, 14400)).toEqual({ direction: "up", diffSeconds: 7200, text: "+2h 00m" });
  });

  it("says how much less", () => {
    expect(compareTotals(14400, 21600)).toEqual({ direction: "down", diffSeconds: 7200, text: "−2h 00m" });
  });

  it("says there is no change when the difference is under half a minute", () => {
    expect(compareTotals(3600, 3620)).toEqual({ direction: "same", diffSeconds: 0, text: "No change" });
    expect(compareTotals(3600, 3600).direction).toBe("same");
  });

  it("is hidden (null) when there is nothing to compare with", () => {
    expect(compareTotals(3600, 0)).toBeNull();
    expect(compareTotals(3600, undefined)).toBeNull();
    expect(compareTotals(3600, NaN)).toBeNull();
    expect(compareTotals(undefined, 3600)).toBeNull();
    expect(compareTotals(3600, -10)).toBeNull();
  });

  it("still compares when this period is empty but the other is not", () => {
    expect(compareTotals(0, 3600)).toEqual({ direction: "down", diffSeconds: 3600, text: "−1h 00m" });
  });
});

describe("statusBadge", () => {
  it("names the three statuses", () => {
    expect(statusBadge("InProgress")).toEqual({ label: "In progress", tone: "live" });
    expect(statusBadge("Completed")).toEqual({ label: "Completed", tone: "done" });
    expect(statusBadge("Upcoming")).toEqual({ label: "Upcoming", tone: "soon" });
  });

  it("hides the badge for a word it does not know", () => {
    expect(statusBadge("Paused")).toBeNull();
    expect(statusBadge(undefined)).toBeNull();
    expect(statusBadge("inprogress")).toBeNull();
  });
});

describe("platform cards", () => {
  it("shows YouTube's shorts and videos separately, and uses the singular for one", () => {
    expect(contentText("youtube", { shorts: 5, videos: 1 })).toBe("5 shorts · 1 video");
    expect(contentText("instagram", { reels: 20 })).toBe("20 reels");
    expect(contentText("tiktok", { videos: 1 })).toBe("1 video");
    expect(contentText("facebook", { reels: 0 })).toBe("0 reels");
    expect(contentText("youtube", {})).toBe("0 shorts · 0 videos");
  });

  it("builds today's five cards from the data", () => {
    const cards = cardsFromToday(testToday);
    expect(cards.map((c) => c.name)).toEqual(["Instagram", "YouTube", "TikTok", "Facebook", "Other sites"]);
    expect(cards.map((c) => c.seconds)).toEqual([3600, 1800, 1800, 0, 900]);
    expect(cards.map((c) => c.detail)).toEqual(["20 reels", "5 shorts · 1 video", "10 videos", "0 reels", "Combined time"]);
  });

  it("builds a report's cards by picking each platform's content out of the content list", () => {
    const report = {
      otherSeconds: 600,
      platforms: [
        { platform: "instagram", seconds: 100 },
        { platform: "youtube", seconds: 200 },
        { platform: "tiktok", seconds: 300 },
        { platform: "facebook", seconds: 400 },
      ],
      content: [
        { platform: "instagram", activityType: "reel", count: 4, watchSeconds: 1 },
        { platform: "youtube", activityType: "short_video", count: 3, watchSeconds: 1 },
        { platform: "youtube", activityType: "video", count: 2, watchSeconds: 1 },
        { platform: "tiktok", activityType: "short_video", count: 9, watchSeconds: 1 },
        { platform: "facebook", activityType: "reel", count: 1, watchSeconds: 1 },
      ],
    };
    const cards = cardsFromReport(report);
    expect(cards.map((c) => c.detail)).toEqual(["4 reels", "3 shorts · 2 videos", "9 videos", "1 reel", "Combined time"]);
    expect(cards.map((c) => c.seconds)).toEqual([100, 200, 300, 400, 600]);
  });

  it("does not count another platform's content on a card", () => {
    const cards = cardsFromReport({
      otherSeconds: 0,
      platforms: [{ platform: "instagram", seconds: 1 }],
      content: [{ platform: "facebook", activityType: "reel", count: 99, watchSeconds: 0 }],
    });
    expect(cards[0].detail).toBe("0 reels");
  });
});

describe("highlightText (the line under the cards)", () => {
  it("is built from the real totals", () => {
    const cards = cardsFromToday(testToday);
    expect(totalContent(cards)).toBe(36);
    expect(highlightText(cards, testToday.totalSeconds)).toBe("36 items in 2h 15m");
  });

  it("uses the singular for one item", () => {
    const one = cardsFromToday({ ...testToday, platforms: [{ platform: "tiktok", seconds: 60, content: { videos: 1 } }] });
    expect(highlightText(one, 60)).toBe("1 item in 0h 01m");
  });

  it("is hidden (null) when the count is 0", () => {
    const none = cardsFromToday({
      ...testToday,
      platforms: testToday.platforms.map((p) => ({ ...p, content: Object.fromEntries(Object.keys(p.content).map((k) => [k, 0])) })),
    });
    expect(highlightText(none, 8100)).toBeNull();
  });
});
