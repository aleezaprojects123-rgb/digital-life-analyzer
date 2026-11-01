import { useState } from "react";
import { barHeights, conicGradient, DONUT_COLORS, gridlines, statusBadge } from "./model.js";
import { formatDuration, formatDurationShort } from "./format.js";
import { PLATFORM_ICON_NAMES, platformIconUrl } from "./platformIcons.js";

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

/**
 * The icon of a platform: its local file when there is one (src/assets/platform-icons/), otherwise the generic icon
 * (`fallback`). The name is always written next to it, so the picture is decoration: its alt text is empty. A file that
 * fails to load falls back to the generic icon too. "Other sites" has no file, so it always shows the generic icon.
 */
export function PlatformIcon({ platform, fallback, size = 20 }) {
  // Only the four platforms can have a file; anything else ("Other sites") always gets the generic icon.
  const url = PLATFORM_ICON_NAMES.includes(platform) ? platformIconUrl(platform) : undefined;
  return <IconImage key={url ?? "generic"} url={url} fallback={fallback} size={size} />;
}

function IconImage({ url, fallback, size }) {
  const [failed, setFailed] = useState(false);
  if (!url || failed) return <span className="platform-glyph">{fallback}</span>;
  return <img className="platform-img" src={url} alt="" width={size} height={size} draggable={false} onError={() => setFailed(true)} />;
}

export function PlatformCard({ card }) {
  return (
    <article className="card platform-card" data-platform={card.key}>
      <div className="platform-top">
        <span className="platform-icon" aria-hidden="true"><PlatformIcon platform={card.key} fallback={card.icon} size={20} /></span>
        <span className="platform-name">{card.name}</span>
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

/**
 * Bars on a fixed real-time scale: a full-height bar is `capacitySeconds` (24 hours for a day, 7 days for a week block),
 * whatever the data says, and a bar can not be taller than that. Gridlines with labels mark 0, a quarter, a half, three
 * quarters and the full capacity. A bar with no time is not drawn at all. Each drawn bar has its real time ABOVE it (never
 * inside it, so a very small bar is still readable). `bars` is [{ key, label, seconds, title }].
 */
export function BarChart({ bars, capacitySeconds }) {
  const heights = barHeights(bars.map((b) => b.seconds), capacitySeconds);
  const lines = gridlines(capacitySeconds);
  return (
    <div className="chart">
      <div className="axis" aria-hidden="true">
        {lines.map((line) => (
          <span className="axis-label" key={line.fraction} style={{ bottom: `${line.fraction * 100}%` }}>{line.label}</span>
        ))}
      </div>
      <div className="plot">
        <div className="gridlines" aria-hidden="true">
          {lines.map((line) => (
            <div className="gridline" key={line.fraction} style={{ bottom: `${line.fraction * 100}%` }} />
          ))}
        </div>
        <div className="bar-chart" role="list" style={{ "--bars": bars.length }}>
          {bars.map((bar, i) => (
            <div className="bar-col" role="listitem" key={bar.key} title={bar.title} aria-label={`${bar.label}: ${formatDuration(bar.seconds)}`}>
              <div className="bar-track">
                {heights[i] > 0 ? (
                  <>
                    <div className="bar-value" style={{ bottom: `calc(${heights[i]}% + 5px)` }}>{formatDurationShort(bar.seconds)}</div>
                    <div className="bar" style={{ height: `${heights[i]}%` }} />
                  </>
                ) : null}
              </div>
              <div className="bar-day">{bar.label}</div>
            </div>
          ))}
        </div>
      </div>
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
            <span className="legend-icon" aria-hidden="true"><PlatformIcon platform={part.key} fallback={part.icon} size={16} /></span>
            <span className="legend-name">{part.name}</span>
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
