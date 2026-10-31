// TEST DATA below is made up for these tests. It is not real activity and it is not used by the app.
import { describe, expect, it } from "vitest";
import {
  barHeights, cardsFromReport, cardsFromToday, compareTotals, conicGradient, contentText, donutShares, highlightText,
  statusBadge, totalContent,
} from "./model.js";

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

describe("barHeights (relative to the largest bar)", () => {
  it("makes the largest bar 100 and the others a share of it", () => {
    expect(barHeights([7200, 3600, 1800, 0, 5400])).toEqual([100, 50, 25, 0, 75]);
  });

  it("does not depend on the unit", () => {
    expect(barHeights([2, 1])).toEqual(barHeights([2000, 1000]));
  });

  it("gives a single bar the full height", () => {
    expect(barHeights([42])).toEqual([100]);
  });

  it("gives every bar 0 when all are 0, and for no bars at all (no division by zero)", () => {
    expect(barHeights([0, 0, 0])).toEqual([0, 0, 0]);
    expect(barHeights([])).toEqual([]);
  });

  it("ignores values that are not positive numbers", () => {
    expect(barHeights([100, -50, NaN, Infinity, undefined])).toEqual([100, 0, 0, 0, 0]);
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
