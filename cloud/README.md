# Cloud side (Fatima): backend, database and dashboard

Everything here runs on a server or in a web browser, never on the user's PC. It receives the daily **summaries** from the Windows agent (Contract 2, [contracts/summary-format.md](../contracts/summary-format.md)), stores them encrypted, and shows reports on a dashboard. It never receives raw events, URLs, window titles or page text.

## Current state

| Part | What works today | What does not exist yet |
|---|---|---|
| [backend/](backend/README.md) | Register and log in (F9) with Argon2 password hashes and signed tokens, `/me`, health routes, database migrations, tests | Log out, password reset, summary upload, reports, score, forecast, Stripe |
| Database | Tables `users` and `daily_summaries` (migration `0001`) | Password reset, goals, payments (each gets its own migration when built) |
| [frontend/](frontend/README.md) | React page with a status panel that asks the backend if it and its database are up | Register and login pages, reports, charts |
| [mock/](mock/README.md) | A made-up day in Contract 2 format, checked by tests | More days (needed for the weekly report and the forecast) |

## Folder layout

```
cloud/
  backend/     FastAPI + PostgreSQL (Python)
    app/         the API code
    migrations/  database changes, one file per change (Alembic)
    tests/       automatic tests
  frontend/    React dashboard (Vite)
  mock/        made-up summaries that follow Contract 2
```

## What you need installed (once)

| Tool | Used for | Check it with |
|---|---|---|
| Python 3.12 or newer (tested with 3.14) | the backend | `python --version` |
| PostgreSQL 16 or newer (tested with 18) | the database | `& "C:\Program Files\PostgreSQL\18\bin\psql.exe" --version` |
| Node.js 20 or newer (tested with 24) | the dashboard | `node --version` |
| Git | version control | `git --version` |

If a command is "not recognized" right after installing, close and reopen the terminal.

## First-time setup: run these once, in order

All commands are PowerShell. **You never type a password into a chat or into a command line: PostgreSQL asks for it itself, and the one place you type it in a file is your own `.env`.** Start in the repository root (the folder that contains `cloud/`).

### 1. Create your own database

```
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -h localhost
```

It asks `Password for user postgres:`. Type the password you chose when you installed PostgreSQL (nothing appears while you type) and press Enter. The prompt changes to `postgres=#`. Now type these lines one at a time:

```
CREATE ROLE dla_app LOGIN;
\password dla_app
CREATE DATABASE dla OWNER dla_app;
CREATE DATABASE dla_test OWNER dla_app;
\q
```

`\password dla_app` asks you twice for a **new** password for the project's own database user. Choose one, **letters and digits only** (no `@ : / % #`), and remember it for step 2. Nothing is shown while you type.

`dla` is the real database. `dla_test` is for the automatic tests, which **wipe it every time**, which is why its name must end with `_test`.

### 2. Create your `.env`

The repository is public. Passwords and keys go only in a local `.env` file, which git ignores.

```
Copy-Item .env.example .env
notepad .env
```

In Notepad, fill in these two lines, replacing `YOUR_PASSWORD` with the password from step 1, then save and close:

```
DATABASE_URL=postgresql://dla_app:YOUR_PASSWORD@localhost:5432/dla
TEST_DATABASE_URL=postgresql://dla_app:YOUR_PASSWORD@localhost:5432/dla_test
```

(`TEST_DATABASE_URL` is read by the tests; it is not in `.env.example` yet.) **Never commit `.env`.**

### 3. Create the login secret (never shown on screen)

This writes a random signing secret straight into `.env`. Run it as is:

```
python -m venv "$env:USERPROFILE\.venvs\dla-backend"
$py = "$env:USERPROFILE\.venvs\dla-backend\Scripts\python.exe"
$secret = & $py -c "import secrets; print(secrets.token_urlsafe(48))"
(Get-Content .env) -replace '^JWT_SECRET=.*$', "JWT_SECRET=$secret" | Set-Content .env
```

Without this secret, login refuses to work. `ENCRYPTION_KEY` is needed later (summary upload), not now.

### 4. Install and create the tables

```
cd cloud\backend
& $py -m pip install -r requirements-dev.txt
& $py -m alembic upgrade head
& $py -m pytest
```

`pytest` should end with `131 passed` (about 30 seconds). If it says some tests were **skipped**, `TEST_DATABASE_URL` is missing from `.env`.

### 5. Install the dashboard packages

```
cd ..\frontend
npm install
```

## Every time: run the dashboard in your browser

Two terminals.

**Terminal 1, the backend** (in `cloud\backend`):

```
$py = "$env:USERPROFILE\.venvs\dla-backend\Scripts\python.exe"
& $py -m uvicorn app.main:app --reload --port 8000
```

**Terminal 2, the dashboard** (in `cloud\frontend`):

```
npm run dev
```

Open **http://localhost:5173**. You should see "Digital Life Analyzer", a "Your reports" card, and a "System status" card with **Backend API: Online** and **Database: Online**. Stop either terminal with `Ctrl+C`.

To try register and login before the dashboard has those pages, open **http://127.0.0.1:8000/docs**: open `POST /api/v1/auth/register`, click **Try it out**, type an email and a password **in the browser**, and press **Execute**. See [backend/README.md](backend/README.md).

## A note about OneDrive

This repository is inside a OneDrive folder. OneDrive will try to upload `frontend/node_modules` (thousands of small files) and slow your PC down. The Python virtual environment is created **outside** the repository for this reason. For `node_modules`, either pause OneDrive sync while running `npm install`, or move the repository out of OneDrive (for example to `C:\dev\`).
