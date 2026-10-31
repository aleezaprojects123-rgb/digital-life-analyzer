// TEST DATA below is made up for these tests. It is not real activity and it is not used by the app.
import { describe, expect, it } from "vitest";
import {
  countWord, formatDuration, formatLongDate, formatMonthRange, formatWeekRange, parseDay, weekdayShort,
} from "./format.js";

describe("formatDuration (hours and minutes)", () => {
  it.each([
    [0, "0h 00m"],
    [60, "0h 01m"],
    [3600, "1h 00m"],
    [7500, "2h 05m"], // minutes always have two digits
    [8220, "2h 17m"],
    [360000, "100h 00m"],
  ])("%s seconds is %s", (seconds, text) => {
    expect(formatDuration(seconds)).toBe(text);
  });

  it("rounds to the nearest minute", () => {
    expect(formatDuration(29)).toBe("0h 00m");
    expect(formatDuration(30)).toBe("0h 01m");
    expect(formatDuration(89)).toBe("0h 01m");
    expect(formatDuration(90)).toBe("0h 02m");
  });

  it("never writes 60 minutes: the hour carries over", () => {
    expect(formatDuration(3599.6)).toBe("1h 00m");
    expect(formatDuration(7199)).toBe("2h 00m");
  });

  it.each([NaN, Infinity, -Infinity, -5, undefined, null, "12", {}])("treats %s as 0", (bad) => {
    expect(formatDuration(bad)).toBe("0h 00m");
  });
});

describe("dates", () => {
  it("reads a real day and nothing else", () => {
    expect(parseDay("2026-10-31")).toEqual({ year: 2026, month: 10, day: 31, weekday: 6 });
    for (const bad of ["2026-02-30", "2026-13-01", "26-10-31", "2026-10-31T00:00", "", null, undefined, 20261031]) {
      expect(parseDay(bad)).toBeNull();
    }
  });

  it("writes the long date", () => {
    expect(formatLongDate("2026-10-31")).toBe("Saturday, 31 October");
    expect(formatLongDate("2026-01-01")).toBe("Thursday, 1 January");
    expect(formatLongDate("nonsense")).toBe("");
  });

  it("writes a week range", () => {
    expect(formatWeekRange("2026-10-26", "2026-11-01")).toBe("Mon, 26 Oct – Sun, 01 Nov");
    expect(formatWeekRange("bad", "2026-11-01")).toBe("");
  });

  it("writes a month range", () => {
    expect(formatMonthRange("2026-10-01", "2026-10-31")).toBe("Oct 1 – Oct 31");
    expect(formatMonthRange("2026-02-01", "2026-02-28")).toBe("Feb 1 – Feb 28");
    expect(formatMonthRange("2026-02-01", "bad")).toBe("");
  });

  it("shortens the weekday, from the word or from the date", () => {
    expect(weekdayShort("Monday", "2026-10-26")).toBe("Mon");
    expect(weekdayShort("", "2026-10-31")).toBe("Sat");
    expect(weekdayShort(undefined, "bad")).toBe("");
  });
});

describe("countWord", () => {
  it("uses the singular for exactly one", () => {
    expect(countWord(1, "reel")).toBe("1 reel");
    expect(countWord(0, "reel")).toBe("0 reels");
    expect(countWord(7, "reel")).toBe("7 reels");
    expect(countWord(2, "short")).toBe("2 shorts");
    expect(countWord(1, "box", "boxes")).toBe("1 box");
    expect(countWord(3, "box", "boxes")).toBe("3 boxes");
  });
});
