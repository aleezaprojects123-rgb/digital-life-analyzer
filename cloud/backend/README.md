# Backend (FastAPI + PostgreSQL)

Python 3.12 or newer. Set up PostgreSQL and your `.env` first: see [../README.md](../README.md).

All commands below are for PowerShell, run in this folder (`cloud/backend/`). They call the virtual environment's Python directly, so you do not need to "activate" anything.

## 1. Create the virtual environment and install (once)

The environment lives **outside the repository** (OneDrive would otherwise sync it).

```
python -m venv "$env:USERPROFILE\.venvs\dla-backend"
$py = "$env:USERPROFILE\.venvs\dla-backend\Scripts\python.exe"
& $py -m pip install -r requirements-dev.txt
```

In every new terminal, set the shortcut again before the commands below:

```
$py = "$env:USERPROFILE\.venvs\dla-backend\Scripts\python.exe"
```

## 2. Create the tables

```
& $py -m alembic upgrade head
```

This applies every migration in `migrations/versions/` to the database in `DATABASE_URL`. It prints `Running upgrade -> 0001`. Running it again changes nothing.

Other useful commands:

| Command | What it does |
|---|---|
| `& $py -m alembic current` | Which migration the database is at. |
| `& $py -m alembic downgrade base` | Removes the tables again (**deletes the data**; fine for development). |
| `& $py -m alembic upgrade head --sql` | Prints the SQL without running it. |

## 3. Start the server

```
& $py -m uvicorn app.main:app --reload --port 8000
```

Then check it:

| Open | Expected |
|---|---|
| http://127.0.0.1:8000/api/v1/health | `{"status":"ok"}` |
| http://127.0.0.1:8000/api/v1/health/db | `{"database":"ok"}`, or status 503 `database unavailable` if the database cannot be reached |
| http://127.0.0.1:8000/docs | The interactive API page FastAPI makes for free |

Stop it with `Ctrl+C`.

## 4. Run the tests

```
& $py -m pytest
```

- Without `TEST_DATABASE_URL` in your `.env`, the tests that need the database are **skipped** (the output says so), and the rest run (67 pass, 64 skipped).
- With it, all 131 run against `dla_test`. **They wipe that database.** They refuse to run if its name does not end with `_test`.
- All test data is made up (`ali@example.com`, `test-password-1`). No real hashes or keys.

## Accounts (F9)

All routes are under `/api/v1`. You can try them on the interactive page at http://127.0.0.1:8000/docs.

| Route | What it does |
|---|---|
| `POST /auth/register` | Body `{"email", "password"}`. Makes an account on the **free** plan and answers `201` with `id`, `email`, `plan`, `created_at`. `409` if the email is taken (letter case does not matter). `422` for a bad email, a password shorter than 8 or longer than 128 characters, or any extra field (nobody can pick a plan here). |
| `POST /auth/login` | Same body. Answers `200` with `access_token` (a signed token, valid 8 hours) and `expires_in`. A wrong password and an unknown email both get the **same** `401`, so login can not be used to find out who has an account. |
| `GET /me` | Header `Authorization: Bearer <access_token>`. Answers with the caller's own account. `401` without a token, with a token that is changed, expired, signed with another secret, or of an account that no longer exists. |

How it is kept safe:

- **Passwords** are stored only as an Argon2id hash with a random salt (`app/security.py`). The password is never stored, logged or sent back, not even inside an error message.
- **Tokens** are signed with `JWT_SECRET` (at least 32 characters). If it is missing or short, login and `/me` answer `503` instead of making weak tokens. A token holds only the user id and its times.
- **One user can only reach her own data.** `/me` takes the user id from the verified token, never from the request. Any route added later that shows account data must depend on `current_user` (`app/auth.py`) and ask only for rows of that user.
- **Not done yet:** log out and password reset (Day 3), and limits on repeated wrong passwords. Register and login pages in the dashboard are also still to do.

To try it by hand in the browser (http://127.0.0.1:8000/docs): register, then `POST /auth/login`, copy the `access_token` from the answer, click **Authorize** (top right), paste it, and call `GET /me`. Type the password in that page only.

## Checking a summary against Contract 2

`app/summary_contract.py` checks a summary against the JSON Schema in `contracts/` and the seven "checks the server makes" from [contracts/summary-format.md](../../contracts/summary-format.md). `validate_summary(summary)` returns a list of problems; an empty list means valid. It is not connected to a route yet: the upload route (Day 4 and 8) will use it. Tests run it on the contract's samples and on [../mock/](../mock/README.md).

## The database today

| Table | Holds |
|---|---|
| `users` | `id`, `email` (always lowercase, unique), `password_hash` (never the password), `plan` (`free` or `pro`), `created_at` |
| `daily_summaries` | One row per user per day: `user_id`, `summary_date`, `schema_version`, `generated_at`, `received_at`, and `payload_encrypted` (the summary, encrypted with the server key). Deleting a user deletes her summaries. |

The summary columns hold no activity data on their own: everything is inside the encrypted payload.

## How to add a database change later

1. Change `app/models.py`.
2. Add a new file in `migrations/versions/` (copy the shape of `0001_...`, give it the next number and set `down_revision` to the previous revision).
3. Run `pytest`. The test `test_migration_and_models_agree` fails if the models and the migrations differ.

## Files

| File | Purpose |
|---|---|
| `app/main.py` | Builds the FastAPI app (CORS and routes). All routes are under `/api/v1`. |
| `app/config.py` | Settings from `.env`. Secrets have no default value. |
| `app/db.py` | The database connection, created on first use. |
| `app/models.py` | The tables. |
| `app/schemas.py` | What register and login accept and return (email tidied, password length limits, no extra fields). |
| `app/security.py` | Argon2id password hashing and signed tokens. |
| `app/auth.py` | `current_user`: reads the token and loads that user. |
| `app/summary_contract.py` | Checks a summary against Contract 2. |
| `app/routes/health.py` | The two health routes. |
| `app/routes/accounts.py` | `register`, `login`, `me`. |
| `alembic.ini`, `migrations/` | Database migrations (Alembic). The address is read from `DATABASE_URL`, never written in these files. |
| `tests/` | Automatic tests. |
| `requirements.txt`, `requirements-dev.txt` | Packages, pinned to the tested versions. |
