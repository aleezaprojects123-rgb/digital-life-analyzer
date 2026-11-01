import { useCallback, useState } from "react";
import {
  BarChart, Comparison, EmptyState, ErrorState, LoadingState, PageHead, PlatformCard, StatusBadge,
} from "./components.jsx";
import { fetchReport } from "./dataSource.js";
import { formatDuration, formatMonthRange, formatWeekRange, localToday, weekdayShort } from "./format.js";
import { cardsFromReport, comparePeriods, MONTHLY_CHART_CAPACITY_SECONDS, WEEKLY_CHART_CAPACITY_SECONDS } from "./model.js";
import { useAgentData } from "./useAgentData.js";

const KINDS = {
  weekly: {
    eyebrow: "A wider view",
    title: "Weekly report",
    unit: "week",
    chartTitle: "Time per day",
    capacity: WEEKLY_CHART_CAPACITY_SECONDS, // a full bar is 24 hours
    range: formatWeekRange,
    bars: (report) =>
      report.days.map((d) => ({
        key: d.date,
        label: weekdayShort(d.dayOfWeek, d.date),
        seconds: d.totalSeconds,
        title: d.date,
      })),
  },
  monthly: {
    eyebrow: "The bigger picture",
    title: "Monthly report",
    unit: "month",
    chartTitle: "Time per week block",
    capacity: MONTHLY_CHART_CAPACITY_SECONDS, // a full bar is 7 days (168 hours)
    range: formatMonthRange,
    bars: (report) =>
      report.weeks.map((w) => ({
        key: String(w.number),
        label: `Week ${w.number}`,
        seconds: w.totalSeconds,
        title: `${w.firstDay} to ${w.lastDay}`,
      })),
  },
};

/** Both periods are read together: the one shown, and the other one for the comparison. Only the first is required. */
async function loadBoth(kind) {
  const [current, previous] = await Promise.allSettled([fetchReport(kind, "current"), fetchReport(kind, "previous")]);
  if (current.status === "rejected") throw current.reason;
  return {
    current: current.value,
    previous: previous.status === "fulfilled" ? { report: previous.value } : { kind: previous.reason?.kind ?? "failed" },
  };
}

export default function ReportPage({ kind }) {
  const words = KINDS[kind];
  const load = useCallback(() => loadBoth(kind), [kind]);
  const [state, retry] = useAgentData(load);
  const [viewing, setViewing] = useState("current");

  const head = <PageHead eyebrow={words.eyebrow} title={words.title} />;
  if (state.status === "loading") return <section>{head}<LoadingState /></section>;
  if (state.status === "error") return <section>{head}<ErrorState kind={state.kind} onRetry={retry} /></section>;

  const { current, previous } = state.data;
  const shownIsPrevious = viewing === "previous";
  const shown = shownIsPrevious ? previous.report : current;

  const controls = (
    <div className="report-controls">
      <div className="switch">
        <button type="button" className={viewing === "current" ? "active" : ""} aria-pressed={viewing === "current"} onClick={() => setViewing("current")}>
          This {words.unit}
        </button>
        <button type="button" className={viewing === "previous" ? "active" : ""} aria-pressed={viewing === "previous"} onClick={() => setViewing("previous")}>
          Previous {words.unit}
        </button>
      </div>
      {shown ? <StatusBadge status={shown.status} /> : null}
    </div>
  );

  // The previous period could not be read: say so only if the user asked to see it.
  if (!shown) return <section>{head}{controls}<ErrorState kind={previous.kind} onRetry={retry} /></section>;

  const range = <div className="report-date">{words.range(shown.firstDay, shown.lastDay)}</div>;
  if (!(shown.totalSeconds > 0)) return <section>{head}{controls}{range}<EmptyState /></section>;

  // The comparison is with the other period; it is hidden when that one is missing or empty. While one of the two is still
  // in progress it is a fair one: only the first days of both are compared (see comparePeriods).
  const other = shownIsPrevious ? current : previous.report;
  const { comparison, days: fairDays } = other ? comparePeriods(shown, other, localToday()) : { comparison: null, days: null };
  const comparisonLabel = fairDays
    ? `vs the same days ${shownIsPrevious ? "this" : "last"} ${words.unit}`
    : `vs ${shownIsPrevious ? "this" : "last"} ${words.unit}`;
  const cards = cardsFromReport(shown);

  return (
    <section>
      {head}
      {controls}
      <div className="report-top">
        <article className="card report-total">
          {range}
          <div className="total-label">Total tracked time</div>
          <div className="total-time">{formatDuration(shown.totalSeconds)}</div>
        </article>
        <article className="card">
          <h2>{words.chartTitle}</h2>
          <BarChart bars={words.bars(shown)} capacitySeconds={words.capacity} />
        </article>
      </div>
      <div className="grid platforms-week">
        {cards.map((card) => (
          <PlatformCard card={card} key={card.key} />
        ))}
      </div>
      {comparison ? <Comparison comparison={comparison} label={comparisonLabel} /> : null}
      {comparison && fairDays ? (
        <p className="subtle comparison-note">
          This {words.unit} is still in progress, so only the first {fairDays === 1 ? "day" : `${fairDays} days`} of each {words.unit}{" "}
          {fairDays === 1 ? "is" : "are"} compared.
        </p>
      ) : null}
      {comparison && !fairDays && shown.status === "InProgress" ? (
        <p className="subtle comparison-note">
          This {words.unit} is still in progress, so it is compared with the whole of last {words.unit}.
        </p>
      ) : null}
    </section>
  );
}
