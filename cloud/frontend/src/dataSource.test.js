// TEST DATA below is made up for these tests. It is not real activity and it is not used by the app.
import { afterEach, describe, expect, it, vi } from "vitest";
import { AgentError, fetchReport, fetchToday, parseReport, parseToday } from "./dataSource.js";

// TEST DATA: what the agent sends (field names as in Reports/DashboardModels.cs and ReportModels.cs, camelCase).
const rawToday = () => ({
  date: "2026-10-31",
  totalSeconds: 600.5,
  platforms: [
    { platform: "instagram", totalSeconds: 300, content: { reels: 3 } },
    { platform: "youtube", totalSeconds: 200, content: { shorts: 1, videos: 2 } },
  ],
  otherPlatformsSeconds: 100.5,
});

const rawReport = () => ({
  kind: "Weekly",
  firstDay: "2026-10-26",
  lastDay: "2026-11-01",
  timeZoneId: "Test/Zone",
  totalSeconds: 100,
  platforms: [{ platform: "instagram", seconds: 60 }],
  otherPlatformsSeconds: 40,
  content: [{ platform: "instagram", activityType: "reel", count: 2, watchSeconds: 30 }],
  days: [{ date: "2026-10-26", dayOfWeek: "Monday", totalSeconds: 100 }],
  status: "InProgress",
  weeks: [],
});

function stubFetch(answer) {
  const fn = vi.fn(async () => {
    if (answer instanceof Error) throw answer;
    return { ok: answer.status >= 200 && answer.status < 300, status: answer.status, json: async () => { if (answer.notJson) throw new SyntaxError("not json"); return answer.body; } };
  });
  vi.stubGlobal("fetch", fn);
  return fn;
}

afterEach(() => vi.unstubAllGlobals());

describe("which routes are read", () => {
  it("reads today from /agent/dashboard/today, without a cache", async () => {
    const fn = stubFetch({ status: 200, body: rawToday() });
    await fetchToday();
    expect(fn).toHaveBeenCalledTimes(1);
    expect(fn.mock.calls[0][0]).toBe("/agent/dashboard/today");
    expect(fn.mock.calls[0][1].cache).toBe("no-store");
  });

  it.each([
    ["weekly", "current", "/agent/reports/weekly/current"],
    ["weekly", "previous", "/agent/reports/weekly/previous"],
    ["monthly", "current", "/agent/reports/monthly/current"],
    ["monthly", "previous", "/agent/reports/monthly/previous"],
  ])("reads the %s %s report from %s", async (kind, which, url) => {
    const fn = stubFetch({ status: 200, body: rawReport() });
    await fetchReport(kind, which);
    expect(fn.mock.calls[0][0]).toBe(url);
  });

  it("refuses to build a route from anything else", async () => {
    const fn = stubFetch({ status: 200, body: rawReport() });
    const odd = [["daily", "current"], ["weekly", "next"], ["weekly", "../status"], ["__proto__", "current"], ["today", "0"], ["constructor", "name"], [undefined, undefined]];
    for (const [kind, which] of odd) {
      await expect(fetchReport(kind, which)).rejects.toMatchObject({ kind: "failed" });
    }
    expect(fn).not.toHaveBeenCalled();
  });
});

describe("why a read fails", () => {
  it("unreachable: the request could not be made at all", async () => {
    stubFetch(new TypeError("Failed to fetch"));
    await expect(fetchToday()).rejects.toMatchObject({ name: "AgentError", kind: "unreachable" });
  });

  it.each([404, 502, 503, 504])("unreachable: the dev bridge or server answers %s", async (status) => {
    stubFetch({ status, body: {} });
    await expect(fetchToday()).rejects.toMatchObject({ kind: "unreachable", status });
  });

  it("refused: 403 is what the agent says when it will not give reports", async () => {
    stubFetch({ status: 403, body: null });
    await expect(fetchToday()).rejects.toMatchObject({ kind: "refused", status: 403 });
  });

  it.each([400, 500])("failed: the agent answers %s", async (status) => {
    stubFetch({ status, body: { error: "report_failed" } });
    await expect(fetchReport("weekly", "current")).rejects.toMatchObject({ kind: "failed", status });
  });

  it("malformed: the answer is not JSON (for example the page itself)", async () => {
    stubFetch({ status: 200, notJson: true });
    await expect(fetchToday()).rejects.toMatchObject({ kind: "malformed" });
  });
});

describe("checking what came back", () => {
  it("reads today's data into plain names", () => {
    expect(parseToday(rawToday())).toEqual({
      date: "2026-10-31",
      totalSeconds: 600.5,
      otherSeconds: 100.5,
      platforms: [
        { platform: "instagram", seconds: 300, content: { reels: 3 } },
        { platform: "youtube", seconds: 200, content: { shorts: 1, videos: 2 } },
      ],
    });
  });

  it("reads a report", () => {
    const report = parseReport(rawReport());
    expect(report).toMatchObject({ kind: "Weekly", firstDay: "2026-10-26", lastDay: "2026-11-01", status: "InProgress", totalSeconds: 100, otherSeconds: 40 });
    expect(report.platforms).toEqual([{ platform: "instagram", seconds: 60 }]);
    expect(report.content).toEqual([{ platform: "instagram", activityType: "reel", count: 2, watchSeconds: 30 }]);
    expect(report.days).toEqual([{ date: "2026-10-26", dayOfWeek: "Monday", totalSeconds: 100 }]);
  });

  it("turns a tiny negative rounding error (total minus parts) into 0, but not a real negative", () => {
    expect(parseToday({ ...rawToday(), otherPlatformsSeconds: -1e-9 }).otherSeconds).toBe(0);
    expect(() => parseToday({ ...rawToday(), otherPlatformsSeconds: -5 })).toThrow(AgentError);
  });

  const broken = [
    ["not an object", (r) => null],
    ["a list", (r) => []],
    ["no date", (r) => ({ ...r, date: undefined })],
    ["total is text", (r) => ({ ...r, totalSeconds: "600" })],
    ["total is NaN", (r) => ({ ...r, totalSeconds: NaN })],
    ["platforms is not a list", (r) => ({ ...r, platforms: {} })],
    ["a platform has no name", (r) => ({ ...r, platforms: [{ totalSeconds: 1, content: {} }] })],
    ["a count is not a whole number", (r) => ({ ...r, platforms: [{ platform: "instagram", totalSeconds: 1, content: { reels: 1.5 } }] })],
    ["a count is negative", (r) => ({ ...r, platforms: [{ platform: "instagram", totalSeconds: 1, content: { reels: -1 } }] })],
    ["content is missing", (r) => ({ ...r, platforms: [{ platform: "instagram", totalSeconds: 1 }] })],
  ];
  it.each(broken)("today: refuses %s", (_name, change) => {
    expect(() => parseToday(change(rawToday()))).toThrowError(expect.objectContaining({ kind: "malformed" }));
  });

  const brokenReport = [
    ["no status", (r) => ({ ...r, status: undefined })],
    ["no first day", (r) => ({ ...r, firstDay: "" })],
    ["days is not a list", (r) => ({ ...r, days: null })],
    ["weeks is missing", (r) => ({ ...r, weeks: undefined })],
    ["a content count is text", (r) => ({ ...r, content: [{ platform: "instagram", activityType: "reel", count: "2", watchSeconds: 0 }] })],
    ["a content item has no activity type", (r) => ({ ...r, content: [{ platform: "instagram", count: 2, watchSeconds: 0 }] })],
    ["a day has a bad total", (r) => ({ ...r, days: [{ date: "2026-10-26", dayOfWeek: "Monday", totalSeconds: -3 }] })],
    ["a week has no number", (r) => ({ ...r, weeks: [{ firstDay: "2026-10-01", lastDay: "2026-10-07", totalSeconds: 1 }] })],
  ];
  it.each(brokenReport)("report: refuses %s", (_name, change) => {
    expect(() => parseReport(change(rawReport()))).toThrowError(expect.objectContaining({ kind: "malformed" }));
  });

  it("a read of a broken answer rejects with malformed", async () => {
    stubFetch({ status: 200, body: { nonsense: true } });
    await expect(fetchToday()).rejects.toMatchObject({ kind: "malformed" });
    await expect(fetchReport("monthly", "current")).rejects.toMatchObject({ kind: "malformed" });
  });
});
