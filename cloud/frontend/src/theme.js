import { useEffect, useState } from "react";

const KEY = "dla-theme";

// The browser may refuse storage (private window, blocked site data), so every access is guarded.
function stored() {
  try {
    const value = window.localStorage.getItem(KEY);
    return value === "dark" || value === "light" ? value : null;
  } catch {
    return null;
  }
}

function preferred() {
  try {
    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  } catch {
    return "light";
  }
}

/** "light" or "dark", remembered on this browser only, and set on the page as data-theme. */
export function useTheme() {
  const [theme, setTheme] = useState(() => stored() ?? preferred());

  useEffect(() => {
    document.documentElement.setAttribute("data-theme", theme);
    try {
      window.localStorage.setItem(KEY, theme);
    } catch {
      /* not remembered, still works */
    }
  }, [theme]);

  return [theme, () => setTheme((t) => (t === "dark" ? "light" : "dark"))];
}
