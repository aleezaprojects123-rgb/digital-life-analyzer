import { useState } from "react";
import ReportPage from "./ReportPage.jsx";
import TodayPage from "./TodayPage.jsx";
import { useTheme } from "./theme.js";

const TABS = [
  { id: "today", label: "Today" },
  { id: "weekly", label: "Weekly" },
  { id: "monthly", label: "Monthly" },
];

export default function App() {
  const [tab, setTab] = useState("today");
  const [theme, toggleTheme] = useTheme();

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="logo" aria-hidden="true">✳</span>
          <span>Digital Life Analyzer</span>
        </div>
        <nav aria-label="Reports">
          {TABS.map((t) => (
            <button
              type="button"
              key={t.id}
              className={tab === t.id ? "active" : ""}
              aria-current={tab === t.id ? "page" : undefined}
              onClick={() => setTab(t.id)}
            >
              {t.label}
            </button>
          ))}
        </nav>
        <div className="privacy">
          <span className="shield" aria-hidden="true">◆</span>
          Your data stays on this PC
        </div>
        <button type="button" className="theme-toggle" aria-label="Toggle dark theme" onClick={toggleTheme}>
          {theme === "dark" ? "☀ Theme" : "☾ Theme"}
        </button>
      </header>
      <main>
        {tab === "today" ? <TodayPage /> : <ReportPage key={tab} kind={tab} />}
      </main>
    </div>
  );
}
