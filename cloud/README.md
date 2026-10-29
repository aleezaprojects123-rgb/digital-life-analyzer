# Cloud side (Fatima): backend, database and dashboard

Everything here runs on a server or in a web browser, never on the user's PC. It receives the daily **summaries** from the Windows agent (Contract 2, [contracts/summary-format.md](../contracts/summary-format.md)), stores them encrypted, and shows reports on a dashboard. It never receives raw events, URLs, window titles or page text.

## Current state: shells only

| Part | What works today | What does not exist yet |
|---|---|---|
| [backend/](backend/README.md) | FastAPI app with `/api/v1/health` and `/api/v1/health/db`, database migrations, tests | Register and login, summary upload, reports, score, forecast, Stripe |
| Database | Tables `users` and `daily_summaries` (migration `0001`) | Password reset, goals, payments (each gets its own migration when built) |
| [frontend/](frontend/README.md) | React page with a status panel that asks the backend if it and its database are up | Login, reports, charts |

## Folder layout

```
cloud/
  backend/     FastAPI + PostgreSQL (Python)
    app/         the API code
    migrations/  database changes, one file per change (Alembic)
    tests/       automatic tests
  frontend/    React dashboard (Vite)
```

## What you need installed (once)

| Tool | Used for | Check it with |
|---|---|---|
| Python 3.12 or newer (tested with 3.14) | the backend | `python --version` |
| PostgreSQL 16 or newer (tested with 18) | the database | `& "C:\Program Files\PostgreSQL\18\bin\psql.exe" --version` |
| Node.js 20 or newer (tested with 24) | the dashboard | `node --version` |
| Git | version control | `git --version` |

If a command is "not recognized" right after installing, close and reopen the terminal.

## Set up the database (once)

Open a terminal and connect as the `postgres` user (the password you chose when installing PostgreSQL):

```
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -h localhost
```

Then type these three lines (replace `choose-a-password` with your own; use only letters and digits to keep the address simple):

```sql
CREATE ROLE dla_app LOGIN PASSWORD 'choose-a-password';
CREATE DATABASE dla OWNER dla_app;
CREATE DATABASE dla_test OWNER dla_app;
```

`dla` is the real database. `dla_test` is for the automatic tests, which **wipe it every time**: that is why its name must end with `_test`.

## Set up your `.env` (once)

The repository is public. Passwords and keys go only in a local `.env` file, which git ignores.

1. Copy `.env.example` (in the repository root) to `.env` in the same folder.
2. Fill in:

```
DATABASE_URL=postgresql://dla_app:choose-a-password@localhost:5432/dla
TEST_DATABASE_URL=postgresql://dla_app:choose-a-password@localhost:5432/dla_test
```

`TEST_DATABASE_URL` is read by the tests; it is not in `.env.example` yet. `JWT_SECRET` and `ENCRYPTION_KEY` are needed from the next steps (login, summaries), not for the shells. **Never commit `.env`.**

Optional: `CORS_ORIGINS` (default `http://localhost:5173,http://127.0.0.1:5173`) lists the web addresses allowed to call the API from a browser.

## Run everything

Three terminals, from the repository root. Details and every command are in the two READMEs.

1. **Backend:** see [backend/README.md](backend/README.md). It listens on `http://127.0.0.1:8000`.
2. **Dashboard:** see [frontend/README.md](frontend/README.md). It opens on `http://localhost:5173`.
3. **Tests:** `pytest` in `backend/`, `npm test` in `frontend/`.

## A note about OneDrive

This repository is inside a OneDrive folder. OneDrive will try to upload `frontend/node_modules` (thousands of small files) and slow your PC down. The Python virtual environment is created **outside** the repository for this reason. For `node_modules`, either pause OneDrive sync while running `npm install`, or move the repository out of OneDrive (for example to `C:\dev\`).
