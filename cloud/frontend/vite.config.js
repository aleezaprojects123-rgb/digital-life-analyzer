import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { agentProxyOptions, PREFIX } from "./dev/agentBridge.js";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // In development, calls to /api go to the FastAPI backend, so the browser sees one address.
      "/api": "http://127.0.0.1:8000",
      // DEVELOPMENT ONLY: calls to /agent go to the Digital Life Analyzer agent on this PC, with its access token added
      // by the dev server (see dev/agentBridge.js). The token never reaches the page.
      [PREFIX]: agentProxyOptions(),
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: "./src/test/setup.js",
  },
});
