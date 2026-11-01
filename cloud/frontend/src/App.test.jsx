// =====================================================================================================================
// TEST DATA. Everything below that looks like a report is MADE UP for these tests: small numbers that can be checked by
// hand. It is not real activity, it is not the design concept's sample, and none of it exists outside this test file.
// The fake agent answers with the same field names as the real one (Reports/DashboardModels.cs, ReportModels.cs).
// =====================================================================================================================
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.jsx";

// TEST DOUBLE for the icon lookup. By default the real lookup is used (the real files in src/assets/platform-icons). A test can set
// `icons.override` to a made-up { platform: address } map to say exactly which icon files "exist". The addresses are not real files.
const icons = vi.hoisted(() => ({ override: null }));
vi.mock("./platformIcons.js", async (importOriginal) => {
  const real = await importOriginal();
  return { ...real, platformIconUrl: (platform) => (icons.override ? icons.override[platform] : real.platformIconUrl(platform)) };
});

const rawToday = (over = {}) => ({
  date: "2026-10-31", // a Saturday
  totalSeconds: 8100, // 2h 15m = 3600 + 1800 + 1800 + 0 + 900
  platforms: [
    { platform: "instagram", totalSeconds: 3600, content: { reels: 20 } },
    { platform: "youtube", totalSeconds: 1800, content: { shorts: 5, videos: 1 } },
    { platform: "tiktok", totalSeconds: 1800, content: { videos: 10 } },
    { platform: "facebook", totalSeconds: 0, content: { reels: 0 } },
  ],
  otherPlatformsSeconds: 900,
  ...over,
});

const WEEKDAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const THIS_WEEK = ["2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30", "2026-10-31", "2026-11-01"];
const LAST_WEEK = ["2026-10-19", "2026-10-20", "2026-10-21", "2026-10-22", "2026-10-23", "2026-10-24", "2026-10-25"];
const daysOf = (dates, totals) => dates.map((date, i) => ({ date, dayOfWeek: WEEKDAYS[i], totalSeconds: totals[i] }));

const reportBase = (over) => ({
  kind: "Weekly",
  timeZoneId: "Test/Zone",
  platforms: [
    { platform: "instagram", seconds: 7200 },
    { platform: "youtube", seconds: 5400 },
    { platform: "tiktok", seconds: 3600 },
    { platform: "facebook", seconds: 1800 },
  ],
  otherPlatformsSeconds: 3600,
  content: [
    { platform: "instagram", activityType: "reel", count: 40, watchSeconds: 3000 },
    { platform: "youtube", activityType: "short_video", count: 12, watchSeconds: 600 },
    { platform: "youtube", activityType: "video", count: 3, watchSeconds: 1200 },
    { platform: "tiktok", activityType: "short_video", count: 25, watchSeconds: 900 },
    { platform: "facebook", activityType: "reel", count: 9, watchSeconds: 400 },
  ],
  weeks: [],
  ...over,
});

// this week: 7200 + 3600 + 1800 + 0 + 5400 + 3600 + 0 = 21600 (6h 00m)
const rawWeekCurrent = (over = {}) =>
  reportBase({
    firstDay: "2026-10-26",
    lastDay: "2026-11-01",
    status: "InProgress",
    totalSeconds: 21600,
    days: daysOf(THIS_WEEK, [7200, 3600, 1800, 0, 5400, 3600, 0]),
    ...over,
  });

// last week: 4 x 3600 = 14400 (4h 00m)
const rawWeekPrevious = (over = {}) =>
  reportBase({
    firstDay: "2026-10-19",
    lastDay: "2026-10-25",
    status: "Completed",
    totalSeconds: 14400,
    days: daysOf(LAST_WEEK, [3600, 3600, 3600, 3600, 0, 0, 0]),
    ...over,
  });

const week = (number, firstDay, lastDay, totalSeconds) => ({ number, firstDay, lastDay, totalSeconds });

// A month's day list (the real agent sends one entry for every day of a month, as for a week). Each week block's total sits on
// the block's first day, so the days add up to the week blocks.
const monthDays = (prefix, count, blockTotals) =>
  Array.from({ length: count }, (_, i) => ({
    date: `${prefix}-${String(i + 1).padStart(2, "0")}`,
    dayOfWeek: "",
    totalSeconds: i % 7 === 0 ? (blockTotals[i / 7] ?? 0) : 0,
  }));

// this month: 7200 + 14400 + 3600 + 0 + 3600 = 28800 (8h 00m)
const rawMonthCurrent = (over = {}) =>
  reportBase({
    kind: "Monthly",
    firstDay: "2026-10-01",
    lastDay: "2026-10-31",
    status: "InProgress",
    totalSeconds: 28800,
    days: monthDays("2026-10", 31, [7200, 14400, 3600, 0, 3600]),
    weeks: [
      week(1, "2026-10-01", "2026-10-07", 7200),
      week(2, "2026-10-08", "2026-10-14", 14400),
      week(3, "2026-10-15", "2026-10-21", 3600),
      week(4, "2026-10-22", "2026-10-28", 0),
      week(5, "2026-10-29", "2026-10-31", 3600),
    ],
    ...over,
  });

// last month: 21600 (6h 00m)
const rawMonthPrevious = (over = {}) =>
  reportBase({
    kind: "Monthly",
    firstDay: "2026-09-01",
    lastDay: "2026-09-30",
    status: "Completed",
    totalSeconds: 21600,
    days: monthDays("2026-09", 30, [3600, 3600, 7200, 7200, 0]),
    weeks: [
      week(1, "2026-09-01", "2026-09-07", 3600),
      week(2, "2026-09-08", "2026-09-14", 3600),
      week(3, "2026-09-15", "2026-09-21", 7200),
      week(4, "2026-09-22", "2026-09-28", 7200),
      week(5, "2026-09-29", "2026-09-30", 0),
    ],
    ...over,
  });

const URLS = {
  today: "/agent/dashboard/today",
  weekCurrent: "/agent/reports/weekly/current",
  weekPrevious: "/agent/reports/weekly/previous",
  monthCurrent: "/agent/reports/monthly/current",
  monthPrevious: "/agent/reports/monthly/previous",
};

// A fake agent. Each URL maps to [status, body], to an Error ("cannot connect"), or to a function that returns a promise.
function fakeAgent(routes) {
  const fn = vi.fn((url) => {
    const answer = routes[url];
    if (answer === undefined) return Promise.resolve({ ok: false, status: 404, json: async () => null });
    if (typeof answer === "function") return answer();
    if (answer instanceof Error) return Promise.reject(answer);
    const [status, body] = answer;
    return Promise.resolve({ ok: status >= 200 && status < 300, status, json: async () => body });
  });
  vi.stubGlobal("fetch", fn);
  return fn;
}

const everything = (over = {}) => ({
  [URLS.today]: [200, rawToday()],
  [URLS.weekCurrent]: [200, rawWeekCurrent()],
  [URLS.weekPrevious]: [200, rawWeekPrevious()],
  [URLS.monthCurrent]: [200, rawMonthCurrent()],
  [URLS.monthPrevious]: [200, rawMonthPrevious()],
  ...over,
});

const OFFLINE = new TypeError("Failed to fetch");

// "Today" is pinned (only the date is faked, the timers are real) so no test depends on the real clock: Saturday 31 October 2026,
// the 6th day of the test week (Mon 26 Oct to Sun 1 Nov) and the 31st day of the test month.
const PINNED_NOW = [2026, 9, 31, 12, 0, 0];
const pinToday = (...parts) => vi.setSystemTime(new Date(...parts));

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute("data-theme");
  vi.useFakeTimers({ toFake: ["Date"] });
  pinToday(...PINNED_NOW);
});

afterEach(() => {
  cleanup();
  icons.override = null;
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const card = (container, key) => {
  const el = container.querySelector(`[data-platform="${key}"]`);
  expect(el, `card ${key}`).not.toBeNull();
  return { time: el.querySelector(".platform-time").textContent, detail: el.querySelector(".platform-count").textContent };
};
// One entry per bar column: its name, its bar height (null when no bar is drawn) and the time label above the bar (null when none).
const barColumns = (container) =>
  [...container.querySelectorAll(".bar-col")].map((col) => ({
    name: col.querySelector(".bar-day").textContent,
    height: col.querySelector(".bar")?.style.height ?? null,
    value: col.querySelector(".bar-value")?.textContent ?? null,
  }));
const barHeights = (container) => barColumns(container).map((c) => c.height);
const barValues = (container) => barColumns(container).map((c) => c.value);
const barLabels = (container) => barColumns(container).map((c) => c.name);
const pct = (height) => parseFloat(height);

// The fixed capacities, written out here on purpose (not imported): a bar of 24 hours is a day, 7 x 24 hours is a week block.
const DAY_SECONDS = 24 * 3600;
const WEEK_SECONDS = 7 * DAY_SECONDS;
// Checks every bar against its seconds (null = no bar at all): height = seconds / capacity.
const expectBars = (container, seconds, capacity) => {
  const heights = barHeights(container);
  expect(heights).toHaveLength(seconds.length);
  seconds.forEach((s, i) => {
    if (s === null) expect(heights[i], `bar ${i} should not be drawn`).toBeNull();
    else expect(pct(heights[i]), `bar ${i}`).toBeCloseTo((s / capacity) * 100, 5);
  });
};
const openTab = (name) => fireEvent.click(within(screen.getByRole("navigation")).getByRole("button", { name }));

// ---------------------------------------------------------------------------------------------------- the frame

describe("the frame", () => {
  it("has three screens and the privacy note", async () => {
    fakeAgent(everything());
    render(<App />);
    const nav = screen.getByRole("navigation", { name: "Reports" });
    expect(within(nav).getAllByRole("button").map((b) => b.textContent)).toEqual(["Today", "Weekly", "Monthly"]);
    expect(screen.getByText("Your data stays on this PC")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Toggle dark theme" })).toBeInTheDocument();
    await screen.findByText("Saturday, 31 October");
  });

  it("has no login, score, forecast, goals or app names", async () => {
    fakeAgent(everything());
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    // (\bscore\b: the gentle note says "without scores", which is the opposite of showing one)
    for (const word of [/log ?in/i, /sign ?in/i, /\bscore\b/i, /forecast/i, /recovery/i, /goal/i, /\.exe\b/i]) {
      expect(container.textContent).not.toMatch(word);
    }
  });

  it("shows only the agent's routes, and only the ones it needs", async () => {
    const fn = fakeAgent(everything());
    render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(fn.mock.calls.map((c) => c[0])).toEqual([URLS.today]);
    openTab("Weekly");
    await screen.findByText("Weekly report");
    await screen.findByText("Total tracked time");
    openTab("Monthly");
    await screen.findByText("Monthly report");
    await screen.findByText("Time per week block");
    const asked = new Set(fn.mock.calls.map((c) => c[0]));
    expect(asked).toEqual(new Set(Object.values(URLS)));
  });
});

// ---------------------------------------------------------------------------------------------------- Today

describe("Today", () => {
  it("says it is loading before the agent has answered", () => {
    fakeAgent({ [URLS.today]: () => new Promise(() => {}) });
    render(<App />);
    expect(screen.getByRole("status")).toHaveTextContent("Loading your data…");
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Today");
  });

  it("shows the date, the total and one card per platform plus Other sites", async () => {
    fakeAgent(everything());
    const { container } = render(<App />);
    expect(await screen.findByRole("heading", { level: 1, name: "Saturday, 31 October" })).toBeInTheDocument();
    expect(container.querySelector(".total-time")).toHaveTextContent("2h 15m");
    expect(card(container, "instagram")).toEqual({ time: "1h 00m", detail: "20 reels" });
    expect(card(container, "youtube")).toEqual({ time: "0h 30m", detail: "5 shorts · 1 video" });
    expect(card(container, "tiktok")).toEqual({ time: "0h 30m", detail: "10 videos" });
    expect(card(container, "facebook")).toEqual({ time: "0h 00m", detail: "0 reels" });
    expect(card(container, "other")).toEqual({ time: "0h 15m", detail: "Combined time" });
    expect(container.querySelectorAll(".platform-card")).toHaveLength(5);
  });

  it("builds the highlight line from the real totals", async () => {
    fakeAgent(everything());
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(container.querySelector(".highlight")).toHaveTextContent("36 items in 2h 15m");
  });

  it("changes the numbers when the agent's numbers change (nothing is fixed in the page)", async () => {
    fakeAgent(
      everything({
        [URLS.today]: [200, rawToday({ totalSeconds: 3600, otherPlatformsSeconds: 0, platforms: [{ platform: "tiktok", totalSeconds: 3600, content: { videos: 2 } }] })],
      }),
    );
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(container.querySelector(".total-time")).toHaveTextContent("1h 00m");
    expect(container.querySelector(".highlight")).toHaveTextContent("2 items in 1h 00m");
    expect(container.querySelectorAll(".platform-card")).toHaveLength(2); // TikTok and Other sites: only what the agent sent
  });

  it("hides the highlight when no content was counted", async () => {
    const none = rawToday({
      platforms: [
        { platform: "instagram", totalSeconds: 3600, content: { reels: 0 } },
        { platform: "youtube", totalSeconds: 4500, content: { shorts: 0, videos: 0 } },
      ],
      totalSeconds: 8100,
      otherPlatformsSeconds: 0,
    });
    fakeAgent(everything({ [URLS.today]: [200, none] }));
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(container.querySelector(".highlight")).toBeNull();
    expect(container.querySelector(".total-time")).toHaveTextContent("2h 15m");
  });

  it("draws the time share per platform; the legend adds up to 100%", async () => {
    fakeAgent(everything());
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    const rows = [...container.querySelectorAll(".legend-row")].map((r) => r.querySelector(".legend-name").textContent + r.querySelector("strong").textContent);
    expect(rows).toEqual(["Instagram45%", "YouTube22%", "TikTok22%", "Facebook0%", "Other sites11%"]);
    expect(container.querySelector(".donut-center")).toHaveTextContent("2h 15m");
    expect(container.querySelector(".donut").style.background).toContain("conic-gradient");
  });

  it("shows 'Nothing tracked yet' when the total is 0, with no cards and no donut", async () => {
    const empty = rawToday({
      totalSeconds: 0,
      otherPlatformsSeconds: 0,
      platforms: ["instagram", "youtube", "tiktok", "facebook"].map((p) => ({ platform: p, totalSeconds: 0, content: {} })),
    });
    fakeAgent(everything({ [URLS.today]: [200, empty] }));
    const { container } = render(<App />);
    expect(await screen.findByRole("heading", { name: "Nothing tracked yet" })).toBeInTheDocument();
    expect(screen.getByText("Start using Digital Life Analyzer to see your time summaries here.")).toBeInTheDocument();
    expect(container.querySelector(".platform-card")).toBeNull();
    expect(container.querySelector(".donut")).toBeNull();
    expect(container.querySelector(".highlight")).toBeNull();
  });
});

// ---------------------------------------------------------------------------------------------------- errors

describe("when the app cannot be reached", () => {
  const HEADING = "Can't reach the Digital Life Analyzer app on this PC";

  it("says so when nothing answers", async () => {
    fakeAgent(everything({ [URLS.today]: OFFLINE }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: HEADING })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("Please check the tray icon, then try again.");
  });

  it("says so when the dev bridge reports the agent is down (502)", async () => {
    fakeAgent(everything({ [URLS.today]: [502, { error: "agent_unreachable" }] }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: HEADING })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("tray icon");
  });

  it("explains a refusal (403: no consent yet, tracking paused, or no access token)", async () => {
    fakeAgent(everything({ [URLS.today]: [403, null] }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: HEADING })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("not sharing reports right now");
  });

  it("explains an error from the agent (500)", async () => {
    fakeAgent(everything({ [URLS.today]: [500, { error: "report_failed" }] }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: HEADING })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("reported a problem");
  });

  it("explains an answer that is not a report", async () => {
    fakeAgent(everything({ [URLS.today]: [200, { hello: "world" }] }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: HEADING })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("could not read");
  });

  it("'Try again' asks again and shows the data once the app is back", async () => {
    const routes = everything({ [URLS.today]: OFFLINE });
    const fn = fakeAgent(routes);
    render(<App />);
    await screen.findByRole("heading", { name: HEADING });
    routes[URLS.today] = [200, rawToday()];
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("Saturday, 31 October")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: HEADING })).toBeNull();
    expect(fn.mock.calls.filter((c) => c[0] === URLS.today)).toHaveLength(2);
  });

  it("the weekly screen shows it too when the current week can not be read", async () => {
    fakeAgent(everything({ [URLS.weekCurrent]: OFFLINE }));
    render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    expect(await screen.findByRole("heading", { name: HEADING })).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------------------------------- Weekly

describe("Weekly", () => {
  async function openWeekly(routes = everything()) {
    const fn = fakeAgent(routes);
    const view = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    await screen.findByText("Time per day");
    return { fn, ...view };
  }

  it("shows the status, dates, total, daily bars and platform cards of this week", async () => {
    const { container } = await openWeekly();
    expect(container.querySelector(".status")).toHaveTextContent("In progress");
    expect(container.querySelector(".report-date")).toHaveTextContent("Mon, 26 Oct – Sun, 01 Nov");
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("6h 00m");
    expect(barLabels(container)).toEqual(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]);
    expect(barValues(container)).toEqual(["2h 00m", "1h 00m", "30m", null, "1h 30m", "1h 00m", null]);
    expect(card(container, "instagram")).toEqual({ time: "2h 00m", detail: "40 reels" });
    expect(card(container, "youtube")).toEqual({ time: "1h 30m", detail: "12 shorts · 3 videos" });
    expect(card(container, "tiktok")).toEqual({ time: "1h 00m", detail: "25 videos" });
    expect(card(container, "facebook")).toEqual({ time: "0h 30m", detail: "9 reels" });
    expect(card(container, "other")).toEqual({ time: "1h 00m", detail: "Combined time" });
  });

  it("draws each day against 24 hours, and no bar for a day with no time", async () => {
    const { container } = await openWeekly();
    expectBars(container, [7200, 3600, 1800, null, 5400, 3600, null], DAY_SECONDS);
    expect(pct(barHeights(container)[0])).toBeCloseTo(8.3333, 3); // 2 hours of 24
    expect(container.querySelectorAll(".bar")).toHaveLength(5);
    expect(container.querySelectorAll(".bar-col")).toHaveLength(7); // the empty days still have their name
  });

  it("compares with the previous week, fetched from the previous-week route", async () => {
    const { container, fn } = await openWeekly();
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs the same days last week");
    expect(container.querySelector(".comparison")).toHaveAttribute("data-direction", "up");
    expect(fn.mock.calls.map((c) => c[0])).toContain(URLS.weekPrevious);
    expect(container.querySelector(".comparison-note")).toHaveTextContent("still in progress");
  });

  it("switches to the previous week without asking the agent again", async () => {
    const { container, fn } = await openWeekly();
    const calls = fn.mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    expect(container.querySelector(".status")).toHaveTextContent("Completed");
    expect(container.querySelector(".report-date")).toHaveTextContent("Mon, 19 Oct – Sun, 25 Oct");
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("4h 00m");
    expectBars(container, [3600, 3600, 3600, 3600, null, null, null], DAY_SECONDS); // one hour of 24 each, the same scale as this week
    // this week is still in progress, so last week is compared with the same first 6 days of this week
    expect(container.querySelector(".comparison")).toHaveTextContent("−2h 00m vs the same days this week");
    expect(container.querySelector(".comparison")).toHaveAttribute("data-direction", "down");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("only the first 6 days of each week are compared");
    expect(fn.mock.calls.length).toBe(calls);
    fireEvent.click(screen.getByRole("button", { name: "This week" }));
    expect(container.querySelector(".status")).toHaveTextContent("In progress");
  });

  it("marks the chosen period on the switch", async () => {
    await openWeekly();
    expect(screen.getByRole("button", { name: "This week" })).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    expect(screen.getByRole("button", { name: "Previous week" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "This week" })).toHaveAttribute("aria-pressed", "false");
  });

  it("hides the comparison when the previous week has no data", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, rawWeekPrevious({ totalSeconds: 0, days: daysOf(LAST_WEEK, [0, 0, 0, 0, 0, 0, 0]) })] }));
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("6h 00m");
    expect(container.querySelector(".comparison")).toBeNull();
    expect(container.querySelector(".comparison-note")).toBeNull();
  });

  it("hides the comparison, but still shows this week, when the previous week can not be read", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [403, null] }));
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("6h 00m");
    expect(container.querySelector(".comparison")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    expect(screen.getByRole("alert")).toHaveTextContent("Can't reach the Digital Life Analyzer app on this PC");
  });

  it("says there is no change when the same days of both weeks are the same", async () => {
    const same = rawWeekPrevious({ totalSeconds: 21600, days: daysOf(LAST_WEEK, [7200, 3600, 1800, 0, 5400, 3600, 0]) });
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, same] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("No change vs the same days last week");
    expect(container.querySelector(".comparison")).toHaveAttribute("data-direction", "same");
  });

  it("shows 'Nothing tracked yet' for a week with no time, and keeps the switch and the dates", async () => {
    const quiet = rawWeekCurrent({ totalSeconds: 0, otherPlatformsSeconds: 0, days: daysOf(THIS_WEEK, [0, 0, 0, 0, 0, 0, 0]), platforms: [], content: [] });
    fakeAgent(everything({ [URLS.weekCurrent]: [200, quiet] }));
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    expect(await screen.findByRole("heading", { name: "Nothing tracked yet" })).toBeInTheDocument();
    expect(container.querySelector(".report-date")).toHaveTextContent("Mon, 26 Oct – Sun, 01 Nov");
    expect(screen.getByRole("button", { name: "Previous week" })).toBeInTheDocument();
    expect(container.querySelector(".bar-chart")).toBeNull();
    expect(container.querySelector(".comparison")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("4h 00m");
  });

  it("shows the Upcoming badge", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekCurrent]: [200, rawWeekCurrent({ status: "Upcoming" })] }));
    expect(container.querySelector(".status")).toHaveTextContent("Upcoming");
  });

  it("shows no badge for a status it does not know", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekCurrent]: [200, rawWeekCurrent({ status: "Paused" })] }));
    expect(container.querySelector(".status")).toBeNull();
  });
});

// ---------------------------------------------------------------------------------------------------- Monthly

describe("Monthly", () => {
  async function openMonthly(routes = everything()) {
    fakeAgent(routes);
    const view = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Monthly");
    await screen.findByText("Time per week block");
    return view;
  }

  it("shows the status, dates, total, week blocks and platform cards", async () => {
    const { container } = await openMonthly();
    expect(container.querySelector(".status")).toHaveTextContent("In progress");
    expect(container.querySelector(".report-date")).toHaveTextContent("Oct 1 – Oct 31");
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("8h 00m");
    expect(barLabels(container)).toEqual(["Week 1", "Week 2", "Week 3", "Week 4", "Week 5"]);
    expect(card(container, "instagram")).toEqual({ time: "2h 00m", detail: "40 reels" });
    expect(card(container, "youtube").detail).toBe("12 shorts · 3 videos");
  });

  it("draws each week block against 168 hours, with no bar for an empty block", async () => {
    const { container } = await openMonthly();
    expectBars(container, [7200, 14400, 3600, null, 3600], WEEK_SECONDS);
    expect(barValues(container)).toEqual(["2h 00m", "4h 00m", "1h 00m", null, "1h 00m"]);
  });

  it("compares the days gone by with the same first days of the previous month", async () => {
    const { container } = await openMonthly();
    // 31 days have gone by, but last month had 30: the first 30 days of both are compared
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs the same days last month");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("only the first 30 days of each month are compared");
  });

  it("switches to the previous month, which is completed", async () => {
    const { container } = await openMonthly();
    fireEvent.click(screen.getByRole("button", { name: "Previous month" }));
    expect(container.querySelector(".status")).toHaveTextContent("Completed");
    expect(container.querySelector(".report-date")).toHaveTextContent("Sep 1 – Sep 30");
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("6h 00m");
    expectBars(container, [3600, 3600, 7200, 7200, null], WEEK_SECONDS);
    expect(container.querySelector(".comparison")).toHaveTextContent("−2h 00m vs the same days this month");
  });

  it("hides the comparison when there is no previous month", async () => {
    const { container } = await openMonthly(everything({ [URLS.monthPrevious]: OFFLINE }));
    expect(container.querySelector(".comparison")).toBeNull();
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("8h 00m");
  });
});

describe("Monthly (empty month)", () => {
  it("shows 'Nothing tracked yet' and no chart", async () => {
    const quiet = rawMonthCurrent({ totalSeconds: 0, otherPlatformsSeconds: 0, weeks: [], platforms: [], content: [] });
    fakeAgent(everything({ [URLS.monthCurrent]: [200, quiet] }));
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Monthly");
    expect(await screen.findByRole("heading", { name: "Nothing tracked yet" })).toBeInTheDocument();
    expect(container.querySelector(".bar-chart")).toBeNull();
    expect(container.querySelector(".report-date")).toHaveTextContent("Oct 1 – Oct 31");
  });
});

// ---------------------------------------------------------------------------------------------------- the bar scale

describe("the bar scale (seconds / capacity: 24 hours for a day, 168 hours for a week block)", () => {
  async function openWeeklyWithDays(totals, totalOverride) {
    const days = daysOf(THIS_WEEK, totals);
    const total = totalOverride ?? totals.reduce((a, b) => a + b, 0);
    fakeAgent(everything({ [URLS.weekCurrent]: [200, rawWeekCurrent({ totalSeconds: total, days })] }));
    const view = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    await screen.findByText("Time per day");
    return view;
  }

  async function openMonthlyWithWeeks(totals) {
    const dates = [["2026-10-01", "2026-10-07"], ["2026-10-08", "2026-10-14"], ["2026-10-15", "2026-10-21"], ["2026-10-22", "2026-10-28"], ["2026-10-29", "2026-10-31"]];
    const weeks = totals.map((seconds, i) => week(i + 1, dates[i][0], dates[i][1], seconds));
    fakeAgent(everything({ [URLS.monthCurrent]: [200, rawMonthCurrent({ totalSeconds: totals.reduce((a, b) => a + b, 0), weeks })] }));
    const view = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Monthly");
    await screen.findByText("Time per week block");
    return view;
  }

  const HOUR = 3600;

  it("a day of 1h 23m is about 5.8 percent of the chart height, labelled with its real time", async () => {
    const { container } = await openWeeklyWithDays([0, 0, 83 * 60, 0, 0, 0, 0]);
    const wednesday = barColumns(container)[2];
    expect(pct(wednesday.height)).toBeGreaterThan(5.7);
    expect(pct(wednesday.height)).toBeLessThan(5.8);
    expect(pct(wednesday.height)).toBeCloseTo(5.76, 2);
    expect(wednesday.value).toBe("1h 23m");
  });

  it("a day of 12 hours is exactly half the chart", async () => {
    const { container } = await openWeeklyWithDays([0, 12 * HOUR, 0, 0, 0, 0, 0]);
    expect(barColumns(container)[1]).toMatchObject({ name: "Tue", height: "50%", value: "12h 00m" });
  });

  it("a day of 24 hours is full height", async () => {
    const { container } = await openWeeklyWithDays([0, 0, 0, 0, 0, 24 * HOUR, 0]);
    expect(barColumns(container)[5]).toMatchObject({ name: "Sat", height: "100%", value: "24h 00m" });
  });

  it("a day of 3 minutes has a tiny but visible bar with the label 3m", async () => {
    const { container } = await openWeeklyWithDays([0, 0, 180, 0, 0, 0, 0]);
    const columns = barColumns(container);
    expect(columns.filter((c) => c.height !== null)).toHaveLength(1);
    const tiny = columns[2];
    expect(pct(tiny.height)).toBeGreaterThan(0);
    expect(pct(tiny.height)).toBeLessThan(0.5);
    expect(pct(tiny.height)).toBeCloseTo((180 / 86400) * 100, 6);
    expect(tiny.value).toBe("3m");
    expect(container.querySelector(".bar")).toBeInTheDocument();
  });

  it("the stylesheet keeps a tiny bar visible (a minimum height on the bar)", () => {
    const css = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "styles.css"), "utf8");
    const rule = /(^|\n)\.bar \{([^}]*)\}/.exec(css);
    expect(rule, "a .bar rule").not.toBeNull();
    expect(rule[2]).toMatch(/min-height:\s*2px/);
  });

  it("the time label is above the bar and never inside it", async () => {
    const { container } = await openWeeklyWithDays([0, 0, 180, 0, 0, 0, 0]);
    const bar = container.querySelector(".bar");
    const value = container.querySelector(".bar-value");
    expect(value).toHaveTextContent("3m");
    expect(bar.contains(value)).toBe(false);
    expect(value.parentElement).toBe(bar.parentElement); // side by side in the same track, the label placed from the bar's top
    expect(value.style.bottom).toMatch(/^calc\(.+% \+ \d+px\)$/); // its bottom edge is a little above the top of the bar
    expect(value.textContent).toBe("3m");
    expect(bar.textContent).toBe("");
  });

  it("a week block of 42 hours is a quarter of the monthly chart", async () => {
    const { container } = await openMonthlyWithWeeks([0, 42 * HOUR, 0, 0, 0]);
    expect(barColumns(container)[1]).toMatchObject({ name: "Week 2", height: "25%", value: "42h 00m" });
  });

  it("a week block of 168 hours is full height and 84 hours is half", async () => {
    const { container } = await openMonthlyWithWeeks([168 * HOUR, 84 * HOUR, 0, 0, 0]);
    expect(barHeights(container)).toEqual(["100%", "50%", null, null, null]);
    expect(barValues(container)).toEqual(["168h 00m", "84h 00m", null, null, null]);
  });

  it("a 3-minute week block is tiny but drawn, and a 5-hour one is still small", async () => {
    const { container } = await openMonthlyWithWeeks([0, 180, 0, 5 * HOUR, 0]);
    expect(pct(barHeights(container)[1])).toBeCloseTo((180 / 604800) * 100, 6);
    expect(pct(barHeights(container)[3])).toBeCloseTo((5 * HOUR / 604800) * 100, 6);
    expect(pct(barHeights(container)[3])).toBeLessThan(5);
    expect(barValues(container)).toEqual([null, "3m", null, "5h 00m", null]);
  });

  it("data above the capacity is capped at full height, and the label still shows the real time", async () => {
    const day = await openWeeklyWithDays([0, 30 * HOUR, 0, 0, 0, 0, 0]);
    expect(barColumns(day.container)[1]).toMatchObject({ height: "100%", value: "30h 00m" });
    cleanup();
    const block = await openMonthlyWithWeeks([0, 0, 200 * HOUR, 0, 0]);
    expect(barColumns(block.container)[2]).toMatchObject({ height: "100%", value: "200h 00m" });
  });

  it("the weekly gridline labels are 0, 6h, 12h, 18h and 24h, each at its own height", async () => {
    const { container } = await openWeeklyWithDays([0, 6 * HOUR, 0, 0, 0, 0, 0]);
    expect([...container.querySelectorAll(".axis-label")].map((e) => e.textContent)).toEqual(["0m", "6h 00m", "12h 00m", "18h 00m", "24h 00m"]);
    expect([...container.querySelectorAll(".axis-label")].map((e) => e.style.bottom)).toEqual(["0%", "25%", "50%", "75%", "100%"]);
    expect([...container.querySelectorAll(".gridline")].map((e) => e.style.bottom)).toEqual(["0%", "25%", "50%", "75%", "100%"]);
    // a bar of exactly 6 hours reaches the 6h line
    expect(barHeights(container)[1]).toBe("25%");
  });

  it("the monthly gridline labels are 0, 42h, 84h, 126h and 168h", async () => {
    const { container } = await openMonthlyWithWeeks([0, 42 * HOUR, 0, 0, 0]);
    expect([...container.querySelectorAll(".axis-label")].map((e) => e.textContent)).toEqual(["0m", "42h 00m", "84h 00m", "126h 00m", "168h 00m"]);
    expect([...container.querySelectorAll(".gridline")]).toHaveLength(5);
  });

  it("the gridlines and their labels are decoration for the eye: hidden from screen readers, and no bar is inside them", async () => {
    const { container } = await openWeeklyWithDays([0, 6 * HOUR, 0, 0, 0, 0, 0]);
    expect(container.querySelector(".axis")).toHaveAttribute("aria-hidden", "true");
    expect(container.querySelector(".gridlines")).toHaveAttribute("aria-hidden", "true");
    expect(container.querySelector(".gridlines .bar")).toBeNull();
    expect(container.querySelector(".bar-chart")).toHaveAttribute("role", "list");
  });

  it("there is no element at all for a day with no time", async () => {
    const { container } = await openWeeklyWithDays([0, 7200, 0, 0, 0, 0, 0]);
    expect(container.querySelectorAll(".bar")).toHaveLength(1);
    expect(container.querySelectorAll(".bar-value")).toHaveLength(1);
    expect(container.querySelectorAll(".bar-col")).toHaveLength(7); // the days keep their names
  });

  it("data that is all zero draws no bars and no labels, but keeps the gridlines", async () => {
    // (a week with no time at all shows 'Nothing tracked yet' instead of a chart; here the total is above 0 but no day has any
    // time, which can only come from inconsistent data, and must still draw nothing)
    const { container } = await openWeeklyWithDays([0, 0, 0, 0, 0, 0, 0], 600);
    expect(container.querySelectorAll(".bar")).toHaveLength(0);
    expect(container.querySelectorAll(".bar-value")).toHaveLength(0);
    expect(container.querySelectorAll(".bar-col")).toHaveLength(7);
    expect(container.querySelectorAll(".gridline")).toHaveLength(5);
  });

  it("a week with no time at all still shows the empty state, and no chart", async () => {
    fakeAgent(everything({ [URLS.weekCurrent]: [200, rawWeekCurrent({ totalSeconds: 0, otherPlatformsSeconds: 0, platforms: [], content: [], days: daysOf(THIS_WEEK, [0, 0, 0, 0, 0, 0, 0]) })] }));
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    expect(await screen.findByRole("heading", { name: "Nothing tracked yet" })).toBeInTheDocument();
    expect(container.querySelectorAll(".bar, .bar-value, .bar-col, .gridline, .axis-label")).toHaveLength(0);
  });

  it("a month with no time at all still shows the empty state, and no chart", async () => {
    const quiet = rawMonthCurrent({ totalSeconds: 0, otherPlatformsSeconds: 0, weeks: [], platforms: [], content: [] });
    fakeAgent(everything({ [URLS.monthCurrent]: [200, quiet] }));
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Monthly");
    expect(await screen.findByRole("heading", { name: "Nothing tracked yet" })).toBeInTheDocument();
    expect(container.querySelector(".chart")).toBeNull();
  });

  it("shows the real times: minutes alone under an hour, hours and two-digit minutes from an hour", async () => {
    const { container } = await openWeeklyWithDays([0, 180, 3900, 3600, 7500, 59 * 60, 0]);
    expect(barValues(container)).toEqual([null, "3m", "1h 05m", "1h 00m", "2h 05m", "59m", null]);
  });

  it("shows <1m for a bar that exists but is under half a minute", async () => {
    const { container } = await openWeeklyWithDays([0, 20, 0, 0, 0, 0, 0]);
    expect(barValues(container)[1]).toBe("<1m");
    expect(pct(barHeights(container)[1])).toBeGreaterThan(0);
  });

  it("does not show a time under the bars (the label is above the bar)", async () => {
    const { container } = await openWeeklyWithDays([7200, 0, 0, 0, 0, 0, 0]);
    expect(container.querySelector(".bar-time")).toBeNull();
    expect(container.querySelectorAll(".bar-value")).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------------------------------- a fair comparison

describe("the comparison while a period is still in progress", () => {
  // Last week had a very busy Sunday (36000 s). This week has not reached its Sunday yet.
  const busySunday = () => rawWeekPrevious({ totalSeconds: 50400, days: daysOf(LAST_WEEK, [3600, 3600, 3600, 3600, 0, 0, 36000]) });

  async function openWeekly(routes) {
    fakeAgent(routes);
    const view = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    await screen.findByText("Time per day");
    return view;
  }

  it("compares the days gone by with the same first days of last week, not with the whole of it", async () => {
    // this week so far: 21600 (6 days gone by). First 6 days of last week: 14400. The whole of last week: 50400.
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, busySunday()] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs the same days last week");
    expect(container.querySelector(".comparison")).toHaveAttribute("data-direction", "up");
  });

  it("says so in the small note", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, busySunday()] }));
    expect(container.querySelector(".comparison-note")).toHaveTextContent(
      "This week is still in progress, so only the first 6 days of each week are compared.",
    );
    expect(container.querySelector(".comparison-note").textContent).not.toContain("whole of last");
  });

  it("uses 1 day on the first day of the week", async () => {
    pinToday(2026, 9, 26, 9, 0, 0); // Monday 26 October: this week's first day (7200 s) against last week's first day (3600 s)
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, busySunday()] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("+1h 00m vs the same days last week");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("only the first day of each week is compared.");
  });

  it("grows by a day when the date moves on", async () => {
    pinToday(2026, 9, 28, 9, 0, 0); // Wednesday: 3 days. This week 12600, last week's first 3 days 10800.
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, busySunday()] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("+0h 30m vs the same days last week");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("only the first 3 days of each week are compared.");
  });

  it("is the same fair comparison seen from last week (the days it shows against the same days of this week)", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, busySunday()] }));
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    expect(container.querySelector(".comparison")).toHaveTextContent("−2h 00m vs the same days this week");
    expect(container.querySelector(".comparison")).toHaveAttribute("data-direction", "down");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("only the first 6 days of each week are compared.");
  });

  it("hides the comparison when last week has no time in those first days", async () => {
    const onlySunday = rawWeekPrevious({ totalSeconds: 7200, days: daysOf(LAST_WEEK, [0, 0, 0, 0, 0, 0, 7200]) });
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, onlySunday] }));
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("6h 00m");
    expect(container.querySelector(".comparison")).toBeNull();
    expect(container.querySelector(".comparison-note")).toBeNull();
  });

  it("falls back to the whole of last week, and says so, when the date is outside this week", async () => {
    pinToday(2026, 11, 25, 12, 0, 0); // 25 December: the PC's date and the report disagree, so the fair days can not be told
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, busySunday()] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("−8h 00m vs last week");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("compared with the whole of last week");
  });

  it("falls back to the whole of last week when a report has no day list", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, rawWeekPrevious({ days: [] })] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs last week");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("compared with the whole of last week");
  });

  it("monthly: compares the first days of this month with the same first days of last month", async () => {
    pinToday(2026, 9, 15, 12, 0, 0); // 15 October: 15 days gone by. October so far: 7200 + 14400 + 3600 = 25200. September's first 15 days: 3600 + 3600 + 7200 = 14400.
    fakeAgent(everything());
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Monthly");
    await screen.findByText("Time per week block");
    expect(container.querySelector(".comparison")).toHaveTextContent("+3h 00m vs the same days last month");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("only the first 15 days of each month are compared.");
  });

  it("monthly: keeps the whole-month comparison when the agent sends no per-day list", async () => {
    fakeAgent(everything({ [URLS.monthCurrent]: [200, rawMonthCurrent({ days: [] })] }));
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Monthly");
    await screen.findByText("Time per week block");
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs last month");
    expect(container.querySelector(".comparison-note")).toHaveTextContent("compared with the whole of last month");
  });

  it("a period that is no longer in progress is compared as a whole", async () => {
    const finished = rawWeekCurrent({ status: "Completed" });
    const { container } = await openWeekly(everything({ [URLS.weekCurrent]: [200, finished], [URLS.weekPrevious]: [200, busySunday()] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("−8h 00m vs last week");
    expect(container.querySelector(".comparison-note")).toBeNull();
  });
});

// ---------------------------------------------------------------------------------------------------- platform icons

describe("platform icons", () => {
  // TEST DATA: made-up addresses of icon files that "exist" in a test (they are not real files)
  const FILES = {
    instagram: "/test-fixture/instagram.png",
    youtube: "/test-fixture/youtube.svg",
    tiktok: "/test-fixture/tiktok.png",
    facebook: "/test-fixture/facebook.png",
  };
  const NAMES = { instagram: "Instagram", youtube: "YouTube", tiktok: "TikTok", facebook: "Facebook", other: "Other sites" };
  const GENERIC = { instagram: "◎", youtube: "▶", tiktok: "♪", facebook: "◈", other: "▦" };
  const LEGEND_ORDER = ["instagram", "youtube", "tiktok", "facebook", "other"];

  async function openToday() {
    fakeAgent(everything());
    const view = render(<App />);
    await screen.findByText("Saturday, 31 October");
    return view;
  }
  const cardOf = (container, key, scope = "") => container.querySelector(`${scope} [data-platform="${key}"]`.trim());
  const legendRows = (container) => [...container.querySelectorAll(".legend-row")];

  it("shows each platform's icon file next to its name on the Today cards", async () => {
    icons.override = FILES;
    const { container } = await openToday();
    for (const key of ["instagram", "youtube", "tiktok", "facebook"]) {
      const top = cardOf(container, key).querySelector(".platform-top");
      const img = top.querySelector("img.platform-img");
      expect(img, `${key} icon`).not.toBeNull();
      expect(img).toHaveAttribute("src", FILES[key]);
      expect(top.querySelector(".platform-name")).toHaveTextContent(NAMES[key]); // the name stays visible, next to the icon
      expect(top.querySelector(".platform-glyph")).toBeNull();
    }
  });

  it("shows them in the donut legend, next to each name", async () => {
    icons.override = FILES;
    const { container } = await openToday();
    const rows = legendRows(container);
    expect(rows).toHaveLength(5);
    LEGEND_ORDER.slice(0, 4).forEach((key, i) => {
      expect(rows[i].querySelector("img.platform-img")).toHaveAttribute("src", FILES[key]);
      expect(rows[i].querySelector(".legend-name")).toHaveTextContent(NAMES[key]);
    });
  });

  it("shows them on the Weekly and Monthly cards too", async () => {
    icons.override = FILES;
    fakeAgent(everything());
    const { container } = render(<App />);
    await screen.findByText("Saturday, 31 October");
    openTab("Weekly");
    await screen.findByText("Time per day");
    for (const key of ["instagram", "youtube", "tiktok", "facebook"]) {
      expect(cardOf(container, key, ".platforms-week").querySelector("img.platform-img")).toHaveAttribute("src", FILES[key]);
    }
    openTab("Monthly");
    await screen.findByText("Time per week block");
    for (const key of ["instagram", "youtube", "tiktok", "facebook"]) {
      expect(cardOf(container, key, ".platforms-week").querySelector("img.platform-img")).toHaveAttribute("src", FILES[key]);
      expect(cardOf(container, key, ".platforms-week").querySelector(".platform-name")).toHaveTextContent(NAMES[key]);
    }
  });

  it("keeps the generic icon for a platform whose file is missing", async () => {
    icons.override = { youtube: FILES.youtube }; // only YouTube has a file
    const { container } = await openToday();
    expect(cardOf(container, "youtube").querySelector("img.platform-img")).toHaveAttribute("src", FILES.youtube);
    for (const key of ["instagram", "tiktok", "facebook"]) {
      const top = cardOf(container, key).querySelector(".platform-top");
      expect(top.querySelector("img"), `${key} has no picture`).toBeNull();
      expect(top.querySelector(".platform-glyph")).toHaveTextContent(GENERIC[key]);
      expect(top.querySelector(".platform-name")).toHaveTextContent(NAMES[key]);
    }
    const rows = legendRows(container);
    expect(rows[0].querySelector("img")).toBeNull(); // Instagram, in the legend
    expect(rows[0].querySelector(".platform-glyph")).toHaveTextContent(GENERIC.instagram);
    expect(rows[1].querySelector("img")).toHaveAttribute("src", FILES.youtube);
  });

  it("works with no icon files at all: every platform shows its generic icon", async () => {
    icons.override = {};
    const { container } = await openToday();
    expect(container.querySelectorAll("img")).toHaveLength(0);
    for (const key of LEGEND_ORDER) {
      expect(cardOf(container, key).querySelector(".platform-glyph")).toHaveTextContent(GENERIC[key]);
    }
    expect(container.querySelector(".total-time")).toHaveTextContent("2h 15m"); // the screen is otherwise the same
  });

  it("'Other sites' uses the generic icon, even if someone puts a file for it in the folder", async () => {
    icons.override = { ...FILES, other: "/test-fixture/other.png" };
    const { container } = await openToday();
    const other = cardOf(container, "other");
    expect(other.querySelector("img")).toBeNull();
    expect(other.querySelector(".platform-glyph")).toHaveTextContent(GENERIC.other);
    expect(other.querySelector(".platform-name")).toHaveTextContent("Other sites");
    expect(legendRows(container)[4].querySelector("img")).toBeNull();
    expect(legendRows(container)[4].querySelector(".platform-glyph")).toHaveTextContent(GENERIC.other);
    expect(container.innerHTML).not.toContain("other.png");
  });

  it("'Other sites' uses the generic icon in the real build too (no file for it)", async () => {
    const { container } = await openToday(); // icons.override is null: the real lookup and the real folder
    expect(cardOf(container, "other").querySelector("img")).toBeNull();
    expect(cardOf(container, "other").querySelector(".platform-glyph")).toHaveTextContent(GENERIC.other);
  });

  it("uses the real files that are in src/assets/platform-icons, and the generic icon for any that are not", async () => {
    const dirFiles = fs.readdirSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "assets", "platform-icons"));
    const have = (key) => dirFiles.some((f) => new RegExp(`^${key}\\.(png|svg)$`, "i").test(f));
    const { container } = await openToday();
    for (const key of ["instagram", "youtube", "tiktok", "facebook"]) {
      const top = cardOf(container, key).querySelector(".platform-top");
      if (have(key)) expect(top.querySelector("img"), `${key} has a real file`).toHaveAttribute("src", expect.stringContaining(key));
      else expect(top.querySelector(".platform-glyph"), `${key} has no file`).toHaveTextContent(GENERIC[key]);
    }
  });

  it("falls back to the generic icon when a file fails to load", async () => {
    icons.override = FILES;
    const { container } = await openToday();
    const img = cardOf(container, "instagram").querySelector("img");
    fireEvent.error(img);
    expect(cardOf(container, "instagram").querySelector("img")).toBeNull();
    expect(cardOf(container, "instagram").querySelector(".platform-glyph")).toHaveTextContent(GENERIC.instagram);
    expect(cardOf(container, "youtube").querySelector("img")).not.toBeNull(); // the others are not affected
  });

  it("icons are decoration: empty alt text, hidden from screen readers, and the name is still text", async () => {
    icons.override = FILES;
    const { container } = await openToday();
    const images = [...container.querySelectorAll("img")];
    expect(images).toHaveLength(8); // four on the cards, four in the legend
    for (const img of images) {
      expect(img.getAttribute("alt")).toBe("");
      expect(img.closest("[aria-hidden='true']")).not.toBeNull();
    }
    expect(screen.queryAllByRole("img", { name: /instagram|youtube|tiktok|facebook/i })).toHaveLength(0);
    for (const key of ["instagram", "youtube", "tiktok", "facebook"]) expect(screen.getAllByText(NAMES[key]).length).toBeGreaterThan(0);
  });

  it("sizes every icon the same: 20px on a card, 16px in the legend, in a fixed box", async () => {
    icons.override = FILES;
    const { container } = await openToday();
    const sizes = (selector) => [...container.querySelectorAll(selector)].map((img) => `${img.getAttribute("width")}x${img.getAttribute("height")}`);
    expect(new Set(sizes(".platform-top img"))).toEqual(new Set(["20x20"]));
    expect(new Set(sizes(".legend-row img"))).toEqual(new Set(["16x16"]));
    const css = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "styles.css"), "utf8");
    expect(css).toMatch(/\.platform-img \{[^}]*object-fit:\s*contain/);
    expect(css).toMatch(/\.platform-icon \{[^}]*width:\s*28px;[^}]*height:\s*28px/);
    expect(css).toMatch(/\.legend-icon \{[^}]*width:\s*18px;[^}]*height:\s*18px/);
  });

  it("makes no request for an icon: the pictures are local files, so only the agent's routes are fetched", async () => {
    icons.override = FILES;
    const fn = fakeAgent(everything());
    render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(fn.mock.calls.map((c) => c[0])).toEqual([URLS.today]);
    expect(document.querySelectorAll("img[src^='http'], img[src^='//']")).toHaveLength(0);
  });
});

// ---------------------------------------------------------------------------------------------------- theme

describe("the theme toggle", () => {
  it("switches between light and dark and remembers the choice on this browser", async () => {
    fakeAgent(everything());
    render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(document.documentElement.getAttribute("data-theme")).toBe("light");
    const toggle = screen.getByRole("button", { name: "Toggle dark theme" });
    expect(toggle).toHaveTextContent("☾ Theme");
    fireEvent.click(toggle);
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");
    expect(toggle).toHaveTextContent("☀ Theme");
    expect(window.localStorage.getItem("dla-theme")).toBe("dark");
    fireEvent.click(toggle);
    expect(document.documentElement.getAttribute("data-theme")).toBe("light");
  });

  it("starts with the remembered theme", async () => {
    window.localStorage.setItem("dla-theme", "dark");
    fakeAgent(everything());
    render(<App />);
    await screen.findByText("Saturday, 31 October");
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");
  });

  it("still works when the browser refuses to store anything", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    fakeAgent(everything());
    render(<App />);
    await screen.findByText("Saturday, 31 October");
    fireEvent.click(screen.getByRole("button", { name: "Toggle dark theme" }));
    await waitFor(() => expect(document.documentElement.getAttribute("data-theme")).toBe("dark"));
  });
});
