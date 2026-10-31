// DEVELOPMENT-ONLY BRIDGE between this page and the Digital Life Analyzer agent on this PC.
// It runs inside the Vite dev server (Node). It is never part of the built page, and the browser code never imports it.
//
// Why it exists: the agent only answers a caller that (1) sends its secret in the X-DLA-UI-Token header and (2) sends NO
// Origin header, so a web page can never read reports by itself. The secret is a file the agent writes while it runs:
//   %LOCALAPPDATA%\DigitalLifeAnalyzer\ui-access.token
// A browser page can not read that file and must not hold the secret, so in development this bridge does it:
// the page asks http://localhost:5173/agent/..., the bridge adds the token and forwards to http://127.0.0.1:<agent port>/...
//
// How the final UI talks to the agent (a bundled window, a different bridge, ...) is Aleeza's decision. This is only a
// way to see real data while building the screens.
//
// The token is read from the file for every request (the agent makes a new one each time it starts), is checked to look
// like a token before use, is only ever put into the outgoing header, and is never logged, returned or stored.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export const AGENT_HOST = "127.0.0.1";
// The agent's port is a constant, AgentLinkConfig.DefaultPort in device/agent/Dla.Agent/Link/AgentLinkConfig.cs
// (the extension's manifest hard-codes the same address). DLA_AGENT_PORT overrides it, for the tests.
export const DEFAULT_AGENT_PORT = 49721;
export const PREFIX = "/agent";
export const TOKEN_HEADER = "X-DLA-UI-Token";

// The only paths that are forwarded: the agent's read-only routes for the UI. Nothing else, and no query string.
export const ALLOWED_PATHS = new Set([
  "/dashboard/today",
  "/reports/weekly/current",
  "/reports/weekly/previous",
  "/reports/monthly/current",
  "/reports/monthly/previous",
]);

// The only request headers that reach the agent. Everything else the browser sent (Origin, Referer, Cookie, Sec-Fetch-*,
// Accept-Encoding, ...) is dropped: the agent refuses any request with an Origin next to the token, and any repeated header.
const FORWARDED_HEADERS = new Set(["host", "accept", "user-agent"]);

export function agentPort(env = process.env) {
  const n = Number(env.DLA_AGENT_PORT);
  return env.DLA_AGENT_PORT && Number.isInteger(n) && n > 0 && n < 65536 ? n : DEFAULT_AGENT_PORT;
}

export function tokenFilePath(env = process.env) {
  const base = env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
  return path.join(base, "DigitalLifeAnalyzer", "ui-access.token");
}

/** The secret in the file, or null when there is no file or it does not look like a token. Never throws, never logs. */
export function readToken(file = tokenFilePath()) {
  try {
    const text = fs.readFileSync(file, "latin1").trim(); // latin1 keeps every byte distinct, so a non-ASCII byte fails the check below
    return /^[A-Za-z0-9_-]{16,128}$/.test(text) ? text : null; // the agent writes 43 base64url characters
  } catch {
    return null;
  }
}

const LOCAL_ORIGIN = /^http:\/\/(localhost|127\.0\.0\.1|\[::1\]):\d{1,5}$/;

/** True only for a GET of one of the allowed paths, from this page itself (not from another site). */
export function isAllowedRequest(req) {
  if (req.method !== "GET") return false;
  const url = req.url ?? "";
  if (!url.startsWith(PREFIX) || !ALLOWED_PATHS.has(url.slice(PREFIX.length))) return false;
  const origin = req.headers?.origin;
  if (origin !== undefined && !LOCAL_ORIGIN.test(origin)) return false;
  const site = req.headers?.["sec-fetch-site"];
  return site === undefined || site === "same-origin" || site === "none";
}

/** Called for each outgoing request to the agent (the "proxyReq" event). */
export function prepareOutgoingRequest(proxyReq, { port, env = process.env }) {
  for (const name of proxyReq.getHeaderNames()) {
    if (!FORWARDED_HEADERS.has(name)) proxyReq.removeHeader(name);
  }
  proxyReq.setHeader("Host", `${AGENT_HOST}:${port}`); // the agent accepts exactly this Host
  const token = readToken(tokenFilePath(env));
  if (token) proxyReq.setHeader(TOKEN_HEADER, token);
}

/** If the agent is not running there is nothing to forward to: answer 502 with a plain reason (the page shows "can't reach"). */
export function answerUnreachable(res) {
  if (res && typeof res.writeHead === "function" && !res.headersSent) {
    res.writeHead(502, { "Content-Type": "application/json", "Cache-Control": "no-store" });
    res.end('{"error":"agent_unreachable"}');
  }
}

/** The Vite `server.proxy` entry for /agent. */
export function agentProxyOptions(env = process.env) {
  const port = agentPort(env);
  return {
    target: `http://${AGENT_HOST}:${port}`,
    changeOrigin: true,
    rewrite: (p) => p.slice(PREFIX.length),
    bypass: (req) => (isAllowedRequest(req) ? undefined : false), // false = 404, nothing is forwarded
    configure: (proxy) => {
      proxy.on("proxyReq", (proxyReq) => prepareOutgoingRequest(proxyReq, { port, env }));
      proxy.on("error", (_error, _req, res) => answerUnreachable(res));
    },
  };
}
