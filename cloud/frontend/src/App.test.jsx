import { render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import App from "./App.jsx";

// A fake backend: each path maps to [status code, JSON body], or to an Error to simulate "cannot connect".
function fakeBackend(routes) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path) => {
      const answer = routes[path];
      if (answer instanceof Error) throw answer;
      const [status, body] = answer;
      return { ok: status >= 200 && status < 300, status, json: async () => body };
    }),
  );
}

const row = (label) => screen.getByText(label).closest("li");

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("dashboard shell", () => {
  it("shows the title and the empty reports message", async () => {
    fakeBackend({ "/api/v1/health": [200, { status: "ok" }], "/api/v1/health/db": [200, { database: "ok" }] });
    render(<App />);
    expect(screen.getByRole("heading", { name: "Digital Life Analyzer" })).toBeInTheDocument();
    expect(screen.getByText(/Nothing to show yet/)).toBeInTheDocument();
    expect(await within(row("Backend API")).findByText("Online")).toBeInTheDocument();
  });

  it("says Checking… before the backend has answered", () => {
    fakeBackend({ "/api/v1/health": [200, { status: "ok" }], "/api/v1/health/db": [200, { database: "ok" }] });
    render(<App />);
    expect(within(row("Backend API")).getByText("Checking…")).toBeInTheDocument();
  });

  it("shows the API and the database as online when both answer", async () => {
    fakeBackend({ "/api/v1/health": [200, { status: "ok" }], "/api/v1/health/db": [200, { database: "ok" }] });
    render(<App />);
    expect(await within(row("Database")).findByText("Online")).toBeInTheDocument();
    expect(within(row("Backend API")).getByText("Online")).toBeInTheDocument();
  });

  it("shows the database as not reachable when the API is up but the database is not", async () => {
    fakeBackend({
      "/api/v1/health": [200, { status: "ok" }],
      "/api/v1/health/db": [503, { detail: "database unavailable" }],
    });
    render(<App />);
    expect(await within(row("Database")).findByText("Not reachable")).toBeInTheDocument();
    expect(within(row("Backend API")).getByText("Online")).toBeInTheDocument();
  });

  it("shows the API as not reachable, and the database as unknown, when the backend is off", async () => {
    fakeBackend({ "/api/v1/health": new Error("connection refused"), "/api/v1/health/db": new Error("connection refused") });
    render(<App />);
    expect(await within(row("Backend API")).findByText("Not reachable")).toBeInTheDocument();
    expect(within(row("Database")).getByText("Unknown")).toBeInTheDocument();
  });
});
