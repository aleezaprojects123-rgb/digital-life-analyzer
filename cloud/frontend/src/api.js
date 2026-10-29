// Calls to the backend. Paths start with /api; in development Vite forwards them to FastAPI.

async function getJson(path) {
  const response = await fetch(path);
  return { ok: response.ok, body: await response.json().catch(() => null) };
}

/** Asks the backend two questions: is the API up, and does it reach its database? */
export async function fetchStatus() {
  try {
    const api = await getJson("/api/v1/health");
    if (!api.ok || api.body?.status !== "ok") return { api: "down", database: "unknown" };
  } catch {
    return { api: "down", database: "unknown" };
  }
  try {
    const db = await getJson("/api/v1/health/db");
    return { api: "online", database: db.ok && db.body?.database === "ok" ? "online" : "down" };
  } catch {
    return { api: "online", database: "down" };
  }
}
