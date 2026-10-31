import { Donut, EmptyState, ErrorState, LoadingState, PageHead, PlatformCard } from "./components.jsx";
import { fetchToday } from "./dataSource.js";
import { formatDuration, formatLongDate } from "./format.js";
import { cardsFromToday, donutShares, highlightText } from "./model.js";
import { useAgentData } from "./useAgentData.js";

export default function TodayPage() {
  const [state, retry] = useAgentData(fetchToday);

  if (state.status !== "ready") {
    return (
      <section>
        <PageHead eyebrow="Your daily overview" title="Today" />
        {state.status === "loading" ? <LoadingState /> : <ErrorState kind={state.kind} onRetry={retry} />}
      </section>
    );
  }

  const today = state.data;
  const heading = formatLongDate(today.date) || "Today";
  if (!(today.totalSeconds > 0)) {
    return (
      <section>
        <PageHead eyebrow="Your daily overview" title={heading} />
        <EmptyState />
      </section>
    );
  }

  const cards = cardsFromToday(today);
  const highlight = highlightText(cards, today.totalSeconds);
  const shares = donutShares(cards);

  return (
    <section>
      <PageHead eyebrow="Your daily overview" title={heading} note="Times shown in hours and minutes" />
      <div className="grid summary-grid">
        <article className="card total-card">
          <div className="total-label">Total tracked time today</div>
          <div className="total-time">{formatDuration(today.totalSeconds)}</div>
        </article>
        {cards.map((card) => (
          <PlatformCard card={card} key={card.key} />
        ))}
      </div>
      {highlight ? (
        <div className="highlight">
          <span className="spark" aria-hidden="true">✦</span>
          <span>{highlight}</span>
        </div>
      ) : null}
      <div className="grid lower-grid">
        <article className="card chart-card">
          <h2>Today's time by platform</h2>
          <Donut shares={shares} totalSeconds={today.totalSeconds} />
        </article>
        <article className="card chart-card">
          <h2>A gentle note</h2>
          <p className="subtle">
            Your time is shown clearly, without scores or judgement. Use these summaries to understand your own patterns at your pace.
          </p>
          <div className="comparison note">
            <span aria-hidden="true">✦</span>Your activity, your choice.
          </div>
        </article>
      </div>
    </section>
  );
}
