import { useCallback, useEffect, useState } from "react";

/**
 * Runs `load` (a function from dataSource.js) and reports where it is: { status: "loading" },
 * { status: "ready", data } or { status: "error", kind }. The second value starts it again ("Try again").
 * An answer that arrives after the page was left, or after a newer try, is ignored.
 */
export function useAgentData(load) {
  const [state, setState] = useState({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setState({ status: "loading" });
    load().then(
      (data) => {
        if (!cancelled) setState({ status: "ready", data });
      },
      (error) => {
        if (!cancelled) setState({ status: "error", kind: error?.kind ?? "failed" });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [load, attempt]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);
  return [state, retry];
}
