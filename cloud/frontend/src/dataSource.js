// The ONE place that reads data. Everything on the screens comes from here, and everything here comes from the
// Digital Life Analyzer agent on this PC (GET /dashboard/today and the four GET /reports/... routes).
//
// The page calls /agent/... on its own address. In development the Vite dev server forwards that to the agent
// (see dev/agentBridge.js; it adds the agent's access token, which this code never sees). How the final UI reaches the
// agent is Aleeza's decision; when it changes, only this file needs to change.

export const AGENT_BASE = "/agent";

// The exact routes. Nothing else is ever requested, and nothing from the user is put into a path.
const TODAY_ROUTE = "/dashboard/today";
const REPORT_ROUTES = {
  weekly: { current: "/reports/weekly/current", previous: "/reports/weekly/previous" },
  monthly: { current: "/reports/monthly/current", previous: "/reports/monthly/previous" },
};

/**
 * Why a read failed, in the screens' words:
 *   unreachable  the app on this PC did not answer (not running, or nothing behind /agent)
 *   refused      it answered 403: no consent yet, tracking paused, or the dev access token is missing or out of date
 *   failed       it answered with an error
 *   malformed    it answered, but not with a report
 */
export class AgentError extends Error {
  constructor(kind, status = null) {
    super(kind);
    this.name = "AgentError";
    this.kind = kind;
    this.status = status;
  }
}

async function getJson(route) {
  let response;
  try {
    response = await fetch(`${AGENT_BASE}${route}`, { cache: "no-store", headers: { Accept: "application/json" } });
  } catch {
    throw new AgentError("unreachable");
  }
  if (response.status === 403) throw new AgentError("refused", 403);
  if ([404, 502, 503, 504].includes(response.status)) throw new AgentError("unreachable", response.status);
  if (!response.ok) throw new AgentError("failed", response.status);
  try {
    return await response.json();
  } catch {
    throw new AgentError("malformed", response.status); // for example the page itself, when nothing is behind /agent
  }
}

// ---- checking what came back ---------------------------------------------------------------------------------------

const bad = () => new AgentError("malformed");
const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const isText = (v) => typeof v === "string" && v.length > 0;
const isSeconds = (v) => typeof v === "number" && Number.isFinite(v);
const isCount = (v) => Number.isInteger(v) && v >= 0;
// Floating point can leave -0.0000001 where the parts are subtracted from the total; a real negative is a bad answer.
const seconds = (v) => {
  if (!isSeconds(v) || v < -0.001) throw bad();
  return Math.max(0, v);
};
const list = (v) => {
  if (!Array.isArray(v)) throw bad();
  return v;
};

export function parseToday(raw) {
  if (!isObject(raw) || !isText(raw.date)) throw bad();
  return {
    date: raw.date,
    totalSeconds: seconds(raw.totalSeconds),
    otherSeconds: seconds(raw.otherPlatformsSeconds),
    platforms: list(raw.platforms).map((p) => {
      if (!isObject(p) || !isText(p.platform) || !isObject(p.content)) throw bad();
      const content = {};
      for (const [key, value] of Object.entries(p.content)) {
        if (!isCount(value)) throw bad();
        content[key] = value;
      }
      return { platform: p.platform, seconds: seconds(p.totalSeconds), content };
    }),
  };
}

export function parseReport(raw) {
  if (!isObject(raw) || !isText(raw.kind) || !isText(raw.firstDay) || !isText(raw.lastDay) || !isText(raw.status)) throw bad();
  return {
    kind: raw.kind,
    firstDay: raw.firstDay,
    lastDay: raw.lastDay,
    status: raw.status,
    totalSeconds: seconds(raw.totalSeconds),
    otherSeconds: seconds(raw.otherPlatformsSeconds),
    platforms: list(raw.platforms).map((p) => {
      if (!isObject(p) || !isText(p.platform)) throw bad();
      return { platform: p.platform, seconds: seconds(p.seconds) };
    }),
    content: list(raw.content).map((c) => {
      if (!isObject(c) || !isText(c.platform) || !isText(c.activityType) || !isCount(c.count)) throw bad();
      return { platform: c.platform, activityType: c.activityType, count: c.count, watchSeconds: seconds(c.watchSeconds) };
    }),
    days: list(raw.days).map((d) => {
      if (!isObject(d) || !isText(d.date)) throw bad();
      return { date: d.date, dayOfWeek: typeof d.dayOfWeek === "string" ? d.dayOfWeek : "", totalSeconds: seconds(d.totalSeconds) };
    }),
    weeks: list(raw.weeks).map((w) => {
      if (!isObject(w) || !isCount(w.number) || !isText(w.firstDay) || !isText(w.lastDay)) throw bad();
      return { number: w.number, firstDay: w.firstDay, lastDay: w.lastDay, totalSeconds: seconds(w.totalSeconds) };
    }),
  };
}

// ---- what the screens call -------------------------------------------------------------------------------------------

/** Today's platform times and content counts. */
export async function fetchToday() {
  return parseToday(await getJson(TODAY_ROUTE));
}

/** A weekly or monthly report. `kind` is "weekly" or "monthly"; `which` is "current" or "previous". */
export async function fetchReport(kind, which) {
  const known = Object.hasOwn(REPORT_ROUTES, kind) && Object.hasOwn(REPORT_ROUTES[kind], which);
  if (!known) throw new AgentError("failed");
  return parseReport(await getJson(REPORT_ROUTES[kind][which]));
}
