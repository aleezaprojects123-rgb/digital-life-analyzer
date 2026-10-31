// @vitest-environment node
// =====================================================================================================================
// TEST DATA and a TEST DOUBLE. The "agent" below is a stand-in written for these tests: it answers like the real agent's
// request check does (Host must be exactly 127.0.0.1:<port>; the X-DLA-UI-Token must match; no Origin next to the token;
// no repeated header; GET of the five report routes only) and returns made-up JSON. The token is a made-up string.
// The real agent was not used here. Real use is a manual step (see cloud/frontend/README.md).
// =====================================================================================================================
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { createServer } from "vite";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ALLOWED_PATHS, agentPort, agentProxyOptions, DEFAULT_AGENT_PORT, isAllowedRequest, PREFIX, prepareOutgoingRequest, readToken,
  tokenFilePath, TOKEN_HEADER,
} from "./agentBridge.js";

const TEST_TOKEN = "TEST-ONLY-token_0123456789abcdefghijklmnopqrstuvwxyz";
const OTHER_TEST_TOKEN = "TEST-ONLY-token_ROTATED-9876543210zyxwvutsrqponmlkj";

let dir;
const tokenFile = () => path.join(dir, "DigitalLifeAnalyzer", "ui-access.token");
const writeToken = (text) => {
  fs.mkdirSync(path.dirname(tokenFile()), { recursive: true });
  fs.writeFileSync(tokenFile(), text, "utf8");
};

beforeAll(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "dla-bridge-test-"));
});
afterAll(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

// ---------------------------------------------------------------------------------------------------- unit tests

describe("the agent's port", () => {
  it("is the agent's constant 49721 unless a valid override is set", () => {
    expect(DEFAULT_AGENT_PORT).toBe(49721);
    expect(agentPort({})).toBe(49721);
    expect(agentPort({ DLA_AGENT_PORT: "50000" })).toBe(50000);
  });

  it.each(["abc", "0", "-1", "65536", "70000", "12.5", "", " "])("ignores the bad override %j", (bad) => {
    expect(agentPort({ DLA_AGENT_PORT: bad })).toBe(49721);
  });
});

describe("the token file", () => {
  it("is %LOCALAPPDATA%\\DigitalLifeAnalyzer\\ui-access.token", () => {
    expect(tokenFilePath({ LOCALAPPDATA: "C:\\Users\\someone\\AppData\\Local" })).toBe(
      path.join("C:\\Users\\someone\\AppData\\Local", "DigitalLifeAnalyzer", "ui-access.token"),
    );
  });

  it("is read without its trailing newline", () => {
    writeToken(`${TEST_TOKEN}\r\n`);
    expect(readToken(tokenFile())).toBe(TEST_TOKEN);
  });

  it("is null when the file is missing or empty", () => {
    expect(readToken(path.join(dir, "no-such-file"))).toBeNull();
    writeToken("");
    expect(readToken(tokenFile())).toBeNull();
  });

  it.each([
    ["a header-injection attempt", "abcdefghijklmnopqrstuvwxyz\r\nX-Evil: 1"],
    ["a space inside", "abcdefghijklmnop qrstuvwxyz"],
    ["too short", "short"],
    ["too long", "a".repeat(129)],
    ["a non-ASCII character", "abcdefghijklmnopqrstuvwxyzé"],
  ])("is refused when it is %s", (_name, text) => {
    writeToken(text);
    expect(readToken(tokenFile())).toBeNull();
  });
});

describe("which requests are forwarded", () => {
  const request = (url, extra = {}) => ({ method: "GET", url, headers: {}, ...extra });

  it.each([...ALLOWED_PATHS])("allows GET %s", (route) => {
    expect(isAllowedRequest(request(PREFIX + route))).toBe(true);
  });

  it("allows exactly the five report routes", () => {
    expect([...ALLOWED_PATHS].sort()).toEqual([
      "/dashboard/today", "/reports/monthly/current", "/reports/monthly/previous", "/reports/weekly/current", "/reports/weekly/previous",
    ]);
  });

  it.each(["POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD"])("refuses %s", (method) => {
    expect(isAllowedRequest(request(`${PREFIX}/dashboard/today`, { method }))).toBe(false);
  });

  it.each([
    "/agent/status",
    "/agent/events",
    "/agent/",
    "/agent",
    "/agent/dashboard/today?x=1",
    "/agent/dashboard/today/",
    "/agent/dashboard/TODAY",
    "/agent/reports/daily/current",
    "/agent/reports/weekly/current/../../status",
    "/agent/../status",
    "/agentx/dashboard/today",
    "/agent//dashboard/today",
    "/dashboard/today",
    "/api/v1/health",
  ])("refuses the path %s", (url) => {
    expect(isAllowedRequest(request(url))).toBe(false);
  });

  it("refuses a request that comes from another website", () => {
    const url = `${PREFIX}/dashboard/today`;
    expect(isAllowedRequest(request(url, { headers: { origin: "https://evil.example" } }))).toBe(false);
    expect(isAllowedRequest(request(url, { headers: { origin: "http://localhost.evil.example:5173" } }))).toBe(false);
    expect(isAllowedRequest(request(url, { headers: { origin: "http://localhost:5173/" } }))).toBe(false);
    expect(isAllowedRequest(request(url, { headers: { origin: "null" } }))).toBe(false);
    expect(isAllowedRequest(request(url, { headers: { "sec-fetch-site": "cross-site" } }))).toBe(false);
    expect(isAllowedRequest(request(url, { headers: { "sec-fetch-site": "same-site" } }))).toBe(false);
  });

  it("allows this page itself (its Origin is removed before forwarding)", () => {
    const url = `${PREFIX}/dashboard/today`;
    expect(isAllowedRequest(request(url, { headers: { origin: "http://localhost:5173" } }))).toBe(true);
    expect(isAllowedRequest(request(url, { headers: { origin: "http://127.0.0.1:5173" } }))).toBe(true);
    expect(isAllowedRequest(request(url, { headers: { "sec-fetch-site": "same-origin" } }))).toBe(true);
    expect(isAllowedRequest(request(url, { headers: { "sec-fetch-site": "none" } }))).toBe(true);
  });
});

describe("the outgoing request", () => {
  function fakeOutgoing(headers) {
    const map = new Map(Object.entries(headers));
    return {
      getHeaderNames: () => [...map.keys()],
      removeHeader: (n) => map.delete(n),
      setHeader: (n, v) => map.set(n.toLowerCase(), v),
      all: () => Object.fromEntries(map),
    };
  }
  const env = () => ({ LOCALAPPDATA: dir });

  it("drops Origin and everything else except host, accept and user-agent, then adds Host and the token", () => {
    writeToken(TEST_TOKEN);
    const out = fakeOutgoing({
      host: "127.0.0.1:49721", origin: "http://localhost:5173", referer: "http://localhost:5173/", cookie: "a=b", accept: "application/json",
      "user-agent": "test", "accept-encoding": "gzip", "sec-fetch-site": "same-origin", "sec-ch-ua": "x", "x-dla-extension-id": "a".repeat(32),
      "x-dla-ui-token": "browser-supplied-value", "content-length": "5",
    });
    prepareOutgoingRequest(out, { port: 49721, env: env() });
    expect(out.all()).toEqual({ host: "127.0.0.1:49721", accept: "application/json", "user-agent": "test", "x-dla-ui-token": TEST_TOKEN });
  });

  it("never lets the browser choose the token", () => {
    writeToken(TEST_TOKEN);
    const out = fakeOutgoing({ "x-dla-ui-token": "from-the-browser" });
    prepareOutgoingRequest(out, { port: 1234, env: env() });
    expect(out.all()["x-dla-ui-token"]).toBe(TEST_TOKEN);
  });

  it("sends no token when there is no usable token file (the agent then refuses with 403)", () => {
    fs.rmSync(tokenFile(), { force: true });
    const out = fakeOutgoing({ "x-dla-ui-token": "from-the-browser", origin: "http://localhost:5173" });
    prepareOutgoingRequest(out, { port: 1234, env: env() });
    expect(out.all()).toEqual({ host: "127.0.0.1:1234" });
  });
});

describe("the proxy settings", () => {
  it("forward /agent/... to the agent's address on this PC, and nowhere else", () => {
    const options = agentProxyOptions({ DLA_AGENT_PORT: "50001" });
    expect(options.target).toBe("http://127.0.0.1:50001");
    expect(options.changeOrigin).toBe(true);
    expect(options.rewrite("/agent/reports/weekly/current")).toBe("/reports/weekly/current");
    expect(options.bypass({ method: "GET", url: "/agent/dashboard/today", headers: {} })).toBeUndefined();
    expect(options.bypass({ method: "POST", url: "/agent/dashboard/today", headers: {} })).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------------- the real Vite proxy

describe("through a real Vite dev server to a stand-in agent", () => {
  let agent, agentSeen, agentToken, vite, vitePort, agentPortNumber;

  const startAgent = () =>
    new Promise((resolve) => {
      agent = http.createServer((req, res) => {
        const raw = req.rawHeaders.filter((_, i) => i % 2 === 0).map((n) => n.toLowerCase());
        agentSeen.push({ method: req.method, url: req.url, headers: { ...req.headers }, duplicateHeader: new Set(raw).size !== raw.length });
        const refuse = () => { res.writeHead(403, { "Content-Length": 0 }); res.end(); };
        if (new Set(raw).size !== raw.length) return refuse();                                // a repeated header
        if (req.headers.host !== `127.0.0.1:${agentPortNumber}`) return refuse();             // Host must be exactly this
        if (req.headers["x-dla-ui-token"] === undefined) return refuse();                     // not the extension, not the UI
        if (req.headers.origin !== undefined) return refuse();                                // an Origin next to the token
        if (req.headers["x-dla-ui-token"] !== agentToken) return refuse();                    // wrong or old token
        if (req.method !== "GET" || !ALLOWED_PATHS.has(req.url)) return refuse();
        const body = JSON.stringify({ stand_in: true, path: req.url });
        res.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
        res.end(body);
      });
      agent.listen(0, "127.0.0.1", () => {
        agentPortNumber = agent.address().port;
        resolve();
      });
    });

  beforeAll(async () => {
    agentSeen = [];
    agentToken = TEST_TOKEN;
    writeToken(TEST_TOKEN);
    await startAgent();
    vite = await createServer({
      configFile: false,
      root: dir,
      logLevel: "silent",
      appType: "custom",
      optimizeDeps: { noDiscovery: true },
      server: {
        host: "127.0.0.1",
        port: 0,
        hmr: false,
        ws: false,
        watch: null,
        proxy: { [PREFIX]: agentProxyOptions({ LOCALAPPDATA: dir, DLA_AGENT_PORT: String(agentPortNumber) }) },
      },
    });
    await vite.listen();
    vitePort = vite.httpServer.address().port;
  });

  afterAll(async () => {
    await vite?.close();
    await new Promise((resolve) => (agent.listening ? agent.close(resolve) : resolve()));
  });

  beforeEach(() => {
    agentSeen.length = 0;
    agentToken = TEST_TOKEN;
    writeToken(TEST_TOKEN);
  });

  // A browser-like request to the dev server; returns status, headers and body text.
  function ask(url, { method = "GET", headers = {} } = {}) {
    return new Promise((resolve, reject) => {
      const req = http.request({ host: "127.0.0.1", port: vitePort, path: url, method, headers: { accept: "application/json", ...headers } }, (res) => {
        let body = "";
        res.on("data", (chunk) => (body += chunk));
        res.on("end", () => resolve({ status: res.statusCode, headers: res.headers, body }));
      });
      req.on("error", reject);
      req.end();
    });
  }

  const BROWSER_HEADERS = {
    origin: "http://localhost:5173",
    referer: "http://localhost:5173/",
    cookie: "session=test-cookie",
    "accept-encoding": "gzip, deflate",
    "sec-fetch-site": "same-origin",
    "sec-fetch-mode": "cors",
    "x-dla-extension-id": "a".repeat(32),
    "x-dla-ui-token": "a-value-the-browser-made-up",
  };

  it.each([...ALLOWED_PATHS])("reaches the agent for %s, with the token and without the Origin", async (route) => {
    const answer = await ask(PREFIX + route, { headers: BROWSER_HEADERS });
    expect(answer.status).toBe(200);
    expect(JSON.parse(answer.body)).toEqual({ stand_in: true, path: route });
    expect(agentSeen).toHaveLength(1);
    const seen = agentSeen[0];
    expect(seen.method).toBe("GET");
    expect(seen.url).toBe(route); // the /agent prefix is gone
    expect(seen.headers.host).toBe(`127.0.0.1:${agentPortNumber}`);
    expect(seen.headers[TOKEN_HEADER.toLowerCase()]).toBe(TEST_TOKEN);
    expect(seen.duplicateHeader).toBe(false);
    for (const dropped of ["origin", "referer", "cookie", "accept-encoding", "sec-fetch-site", "sec-fetch-mode", "x-dla-extension-id"]) {
      expect(seen.headers, dropped).not.toHaveProperty(dropped);
    }
    expect(Object.keys(seen.headers).every((n) => ["host", "accept", "user-agent", "x-dla-ui-token", "connection"].includes(n))).toBe(true);
  });

  it("never sends the token back to the page", async () => {
    const answer = await ask(`${PREFIX}/dashboard/today`, { headers: BROWSER_HEADERS });
    expect(answer.status).toBe(200);
    expect(answer.body).not.toContain(TEST_TOKEN);
    expect(JSON.stringify(answer.headers)).not.toContain(TEST_TOKEN);
  });

  it("never writes the token to the console or the terminal", async () => {
    const spies = [
      vi.spyOn(console, "log"), vi.spyOn(console, "info"), vi.spyOn(console, "warn"), vi.spyOn(console, "error"),
      vi.spyOn(process.stdout, "write"), vi.spyOn(process.stderr, "write"),
    ];
    await ask(`${PREFIX}/dashboard/today`, { headers: BROWSER_HEADERS });
    await ask(`${PREFIX}/reports/weekly/current`, { headers: BROWSER_HEADERS });
    await ask(`${PREFIX}/status`, { headers: BROWSER_HEADERS }); // refused, also must not log the token
    const written = spies.flatMap((spy) => spy.mock.calls.map((call) => call.map(String).join(" "))).join("\n");
    spies.forEach((spy) => spy.mockRestore());
    expect(written).not.toContain(TEST_TOKEN);
  });

  it("uses the new token after the agent restarts (the file is read for every request)", async () => {
    expect((await ask(`${PREFIX}/dashboard/today`)).status).toBe(200);
    agentToken = OTHER_TEST_TOKEN; // the agent restarted and wrote a new secret
    expect((await ask(`${PREFIX}/dashboard/today`)).status).toBe(403); // the file still has the old one
    writeToken(OTHER_TEST_TOKEN);
    expect((await ask(`${PREFIX}/dashboard/today`)).status).toBe(200);
  });

  it("passes the agent's 403 on when there is no token file", async () => {
    fs.rmSync(tokenFile(), { force: true });
    const answer = await ask(`${PREFIX}/dashboard/today`, { headers: BROWSER_HEADERS });
    expect(answer.status).toBe(403);
    expect(agentSeen[0].headers).not.toHaveProperty("x-dla-ui-token");
  });

  it.each([
    ["a POST", `${PREFIX}/dashboard/today`, { method: "POST" }],
    ["another route", `${PREFIX}/status`, {}],
    ["the events route", `${PREFIX}/events`, { method: "POST" }],
    ["a query string", `${PREFIX}/dashboard/today?a=1`, {}],
    ["a path trick", `${PREFIX}/reports/weekly/current/../../status`, {}],
    ["another website's Origin", `${PREFIX}/dashboard/today`, { headers: { origin: "https://evil.example" } }],
    ["a cross-site fetch", `${PREFIX}/dashboard/today`, { headers: { "sec-fetch-site": "cross-site" } }],
  ])("does not forward %s", async (_name, url, options) => {
    const answer = await ask(url, options);
    expect(answer.status).toBe(404);
    expect(agentSeen).toHaveLength(0);
  });

  it("answers 502 when the agent is not running, and keeps working when it comes back", async () => {
    await new Promise((resolve) => agent.close(resolve));
    const answer = await ask(`${PREFIX}/dashboard/today`);
    expect(answer.status).toBe(502);
    expect(JSON.parse(answer.body)).toEqual({ error: "agent_unreachable" });
    await new Promise((resolve) => agent.listen(agentPortNumber, "127.0.0.1", resolve));
    expect((await ask(`${PREFIX}/dashboard/today`)).status).toBe(200);
  });
});
