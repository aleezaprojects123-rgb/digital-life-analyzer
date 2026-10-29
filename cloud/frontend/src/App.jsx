import { useEffect, useState } from "react";
import { fetchStatus } from "./api.js";

const WORDS = {
  online: "Online",
  down: "Not reachable",
  unknown: "Unknown",
  checking: "Checking…",
};

function StatusRow({ label, state }) {
  return (
    <li className="status-row">
      <span>{label}</span>
      <span className={`pill pill-${state}`}>{WORDS[state]}</span>
    </li>
  );
}

export default function App() {
  const [status, setStatus] = useState({ api: "checking", database: "checking" });

  useEffect(() => {
    let cancelled = false;
    fetchStatus().then((result) => {
      if (!cancelled) setStatus(result);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <main className="page">
      <header>
        <h1>Digital Life Analyzer</h1>
        <p className="muted">Dashboard shell</p>
      </header>

      <section className="card" aria-labelledby="overview-title">
        <h2 id="overview-title">Your reports</h2>
        <p>
          Nothing to show yet. Reports, score, forecast and plan will appear here once your PC has synced
          its daily summaries.
        </p>
      </section>

      <section className="card" aria-labelledby="status-title">
        <h2 id="status-title">System status</h2>
        <ul className="status-list">
          <StatusRow label="Backend API" state={status.api} />
          <StatusRow label="Database" state={status.database} />
        </ul>
      </section>
    </main>
  );
}
