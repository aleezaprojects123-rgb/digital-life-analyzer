# Dashboard (React + Vite)

Node.js 20 or newer. Commands are for PowerShell, run in this folder (`cloud/frontend/`).

## Install (once)

```
npm install
```

This creates `node_modules/` (ignored by git). See the OneDrive note in [../README.md](../README.md).

## Run it

Start the backend first ([../backend/README.md](../backend/README.md), step 3), then:

```
npm run dev
```

Open **http://localhost:5173**. You should see the title, a "Your reports" card that says there is nothing to show yet, and a "System status" card:

| Backend running and database reachable | Backend running, database not reachable | Backend not running |
|---|---|---|
| API **Online**, Database **Online** | API **Online**, Database **Not reachable** | API **Not reachable**, Database **Unknown** |

In development, the page calls `/api/...` on its own address and Vite forwards it to `http://127.0.0.1:8000`, so there is no browser security problem. Stop it with `Ctrl+C`.

## Tests and build

```
npm test
npm run build
```

- `npm test` runs the automatic tests (no browser and no backend needed; the backend is faked).
- `npm run build` makes the production files in `dist/` (ignored by git). `npm run preview` serves them locally.

## Files

| File | Purpose |
|---|---|
| `index.html`, `src/main.jsx` | The page and the entry point. |
| `src/App.jsx` | The shell page: title, reports placeholder, status panel. |
| `src/api.js` | Calls to the backend (only the two health routes so far). |
| `src/styles.css` | Styles, light and dark. |
| `src/App.test.jsx`, `src/test/setup.js` | Automatic tests (Vitest and Testing Library). |
| `vite.config.js` | Dev server on port 5173, forwarding `/api` to the backend, and the test setup. |
