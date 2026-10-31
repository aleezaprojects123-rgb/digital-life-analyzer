// =====================================================================================================================
// TEST DATA. Everything below that looks like a report is MADE UP for these tests: small numbers that can be checked by
// hand. It is not real activity, it is not the design concept's sample, and none of it exists outside this test file.
// The fake agent answers with the same field names as the real one (Reports/DashboardModels.cs, ReportModels.cs).
// =====================================================================================================================
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.jsx";

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

// this month: 7200 + 14400 + 3600 + 0 + 3600 = 28800 (8h 00m)
const rawMonthCurrent = (over = {}) =>
  reportBase({
    kind: "Monthly",
    firstDay: "2026-10-01",
    lastDay: "2026-10-31",
    status: "InProgress",
    totalSeconds: 28800,
    days: [],
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
    days: [],
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

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute("data-theme");
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const card = (container, key) => {
  const el = container.querySelector(`[data-platform="${key}"]`);
  expect(el, `card ${key}`).not.toBeNull();
  return { time: el.querySelector(".platform-time").textContent, detail: el.querySelector(".platform-count").textContent };
};
const barHeights = (container) => [...container.querySelectorAll(".bar")].map((el) => el.style.height);
const barLabels = (container) => [...container.querySelectorAll(".bar-day")].map((el) => el.textContent);
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
    const rows = [...container.querySelectorAll(".legend-row")].map((r) => r.textContent);
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
    expect([...container.querySelectorAll(".bar-time")].map((e) => e.textContent)).toEqual([
      "2h 00m", "1h 00m", "0h 30m", "0h 00m", "1h 30m", "1h 00m", "0h 00m",
    ]);
    expect(card(container, "instagram")).toEqual({ time: "2h 00m", detail: "40 reels" });
    expect(card(container, "youtube")).toEqual({ time: "1h 30m", detail: "12 shorts · 3 videos" });
    expect(card(container, "tiktok")).toEqual({ time: "1h 00m", detail: "25 videos" });
    expect(card(container, "facebook")).toEqual({ time: "0h 30m", detail: "9 reels" });
    expect(card(container, "other")).toEqual({ time: "1h 00m", detail: "Combined time" });
  });

  it("draws bar heights relative to the largest bar", async () => {
    const { container } = await openWeekly();
    expect(barHeights(container)).toEqual(["100%", "50%", "25%", "0%", "75%", "50%", "0%"]);
  });

  it("compares with the previous week, fetched from the previous-week route", async () => {
    const { container, fn } = await openWeekly();
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs last week");
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
    expect(barHeights(container)).toEqual(["100%", "100%", "100%", "100%", "0%", "0%", "0%"]);
    expect(container.querySelector(".comparison")).toHaveTextContent("−2h 00m vs this week");
    expect(container.querySelector(".comparison")).toHaveAttribute("data-direction", "down");
    expect(container.querySelector(".comparison-note")).toBeNull();
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

  it("says there is no change when both weeks are the same", async () => {
    const { container } = await openWeekly(everything({ [URLS.weekPrevious]: [200, rawWeekPrevious({ totalSeconds: 21600 })] }));
    expect(container.querySelector(".comparison")).toHaveTextContent("No change vs last week");
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

  it("draws the week blocks relative to the largest", async () => {
    const { container } = await openMonthly();
    expect(barHeights(container)).toEqual(["50%", "100%", "25%", "0%", "25%"]);
  });

  it("compares with the previous month", async () => {
    const { container } = await openMonthly();
    expect(container.querySelector(".comparison")).toHaveTextContent("+2h 00m vs last month");
  });

  it("switches to the previous month, which is completed", async () => {
    const { container } = await openMonthly();
    fireEvent.click(screen.getByRole("button", { name: "Previous month" }));
    expect(container.querySelector(".status")).toHaveTextContent("Completed");
    expect(container.querySelector(".report-date")).toHaveTextContent("Sep 1 – Sep 30");
    expect(container.querySelector(".report-total .total-time")).toHaveTextContent("6h 00m");
    expect(barHeights(container)).toEqual(["50%", "50%", "100%", "100%", "0%"]);
    expect(container.querySelector(".comparison")).toHaveTextContent("−2h 00m vs this month");
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
