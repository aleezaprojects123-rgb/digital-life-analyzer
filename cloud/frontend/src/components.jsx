import { barHeights, conicGradient, DONUT_COLORS, statusBadge } from "./model.js";
import { formatDuration } from "./format.js";

// ---- the three states every page can be in -------------------------------------------------------------------------

const ERROR_HINTS = {
  unreachable: "Please check the tray icon, then try again.",
  refused: "The app answered, but it is not sharing reports right now. Check that tracking is switched on, then try again.",
  failed: "The app reported a problem. Please try again in a moment.",
  malformed: "The app sent an answer this page could not read. Please try again.",
};

export function StateCard({ icon, title, children, role }) {
  return (
    <article className="card state-card" role={role}>
      <div className="state-art" aria-hidden="true">{icon}</div>
      <h3>{title}</h3>
      {children}
    </article>
  );
}

export function LoadingState() {
  return (
    <StateCard icon="◌" title="Loading your data…" role="status">
      <p>Fetching your latest data. This usually takes a moment.</p>
    </StateCard>
  );
}

export function ErrorState({ kind, onRetry }) {
  return (
    <StateCard icon="⌁" title="Can't reach the Digital Life Analyzer app on this PC" role="alert">
      <p>{ERROR_HINTS[kind] ?? ERROR_HINTS.failed}</p>
      <button type="button" className="retry" onClick={onRetry}>Try again</button>
    </StateCard>
  );
}

export function EmptyState() {
  return (
    <StateCard icon="▤" title="Nothing tracked yet">
      <p>Start using Digital Life Analyzer to see your time summaries here.</p>
    </StateCard>
  );
}

// ---- pieces of the screens ---------------------------------------------------------------------------------------------

export function PageHead({ eyebrow, title, note }) {
  return (
    <div className="page-head">
      <div>
        <div className="eyebrow">{eyebrow}</div>
        <h1>{title}</h1>
      </div>
      {note ? <div className="subtle">{note}</div> : null}
    </div>
  );
}

export function PlatformCard({ card }) {
  return (
    <article className="card platform-card" data-platform={card.key}>
      <div className="platform-top">
        <span className="platform-icon" aria-hidden="true">{card.icon}</span>
        {card.name}
      </div>
      <div className="platform-time">{formatDuration(card.seconds)}</div>
      <div className="platform-count">{card.detail}</div>
    </article>
  );
}

export function StatusBadge({ status }) {
  const badge = statusBadge(status);
  if (!badge) return null;
  return <span className={`status status-${badge.tone}`}>{badge.label}</span>;
}

/** Bars whose heights are relative to the largest one. `bars` is [{ key, label, seconds, title }]. */
export function BarChart({ bars }) {
  const heights = barHeights(bars.map((b) => b.seconds));
  return (
    <div className="bar-chart" role="list">
      {bars.map((bar, i) => (
        <div className="bar-col" role="listitem" key={bar.key} title={bar.title} aria-label={`${bar.label}: ${formatDuration(bar.seconds)}`}>
          <div className="bar-space">
            <div className="bar" style={{ height: `${heights[i]}%` }} />
          </div>
          <div className="bar-day">{bar.label}</div>
          <div className="bar-time">{formatDuration(bar.seconds)}</div>
        </div>
      ))}
    </div>
  );
}

/** The ring and its legend. `shares` comes from donutShares(). */
export function Donut({ shares, totalSeconds }) {
  return (
    <div className="donut-layout">
      <div className="donut" style={{ background: conicGradient(shares.parts) }} role="img" aria-label="Share of time per platform">
        <div className="donut-center">
          {formatDuration(totalSeconds)}
          <small>Total</small>
        </div>
      </div>
      <div className="legend">
        {shares.parts.map((part, i) => (
          <div className="legend-row" key={part.key}>
            <span className="swatch" style={{ background: DONUT_COLORS[i % DONUT_COLORS.length] }} />
            {part.name}
            <strong>{part.percent}%</strong>
          </div>
        ))}
      </div>
    </div>
  );
}

export function Comparison({ comparison, label }) {
  const arrows = { up: "↗", down: "↘", same: "→" };
  return (
    <div className="comparison" data-direction={comparison.direction}>
      <span aria-hidden="true">{arrows[comparison.direction]}</span>
      {comparison.text} {label}
    </div>
  );
}
