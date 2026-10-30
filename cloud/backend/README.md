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

This applies every migration in `migrations/versions/` to the database in `DATABASE_URL`. It prints `Running upgrade -> 0001` and `Running upgrade 0001 -> 0002`. Running it again changes nothing. If you made your database before logout and reset existed, run this again: migration `0002` adds the new tables and does not touch your accounts.

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

- Without `TEST_DATABASE_URL` in your `.env`, the tests that need the database are **skipped** (the output says so), and the rest run (107 pass, 183 skipped).
- With it, all 290 run against `dla_test`. **They wipe that database.** They refuse to run if its name does not end with `_test`.
- All test data is made up (`ali@example.com`, `test-password-1`). No real hashes or keys.

## Accounts (F9)

All routes are under `/api/v1`. You can try them on the interactive page at http://127.0.0.1:8000/docs.

| Route | What it does |
|---|---|
| `POST /auth/register` | Body `{"email", "password"}`. Makes an account on the **free** plan and answers `201` with `id`, `email`, `plan`, `created_at`. `409` if the email is taken (letter case does not matter). `422` for a bad email, a password shorter than 8 or longer than 128 characters, or any extra field (nobody can pick a plan here). |
| `POST /auth/login` | Same body. Answers `200` with `access_token` (a signed token, valid 8 hours) and `expires_in`. A wrong password and an unknown email both get the **same** `401`, so login can not be used to find out who has an account. |
| `GET /me` | Header `Authorization: Bearer <access_token>`. Answers with the caller's own account. `401` without a token, with a token that is changed, expired, signed with another secret, logged out, made before the last password reset, or of an account that no longer exists. |
| `POST /auth/logout` | Header `Authorization: Bearer <access_token>`. Answers `204` and that token never works again. Her other logins (another browser) keep working. A second logout with the same token is a `401`. |
| `POST /auth/forgot-password` | Body `{"email"}`. Always answers `202` with the same message, whether or not the email has an account. If it has one, a reset link is made and handed to the **mock mailer** (see below). |
| `POST /auth/reset-password` | Body `{"token", "new_password"}` (the new password follows the same length rules as register). Answers `204` and every login she had before stops working. `400` with one message for a link that is wrong, expired or already used. `422` for a too short or too long password, and then the link is **not** used up. |

### Password reset and the mock mailer

There is **no real email yet**. The mock mailer (`app/mailer.py`) writes the message into the **server log**, in the window where you started `uvicorn`:

```
INFO:     dla.mailer: MOCK MAILER (no email is sent). Password reset link for ali@example.com, valid 30 minutes, works once: http://localhost:5173/reset-password#token=...
```

- The link holds a random one-time token (256 bits). It works **once** and **expires after 30 minutes** (`RESET_TOKEN_MINUTES` in `.env` changes this). Asking for a new link cancels the older ones. Using a link also cancels all other links of that account.
- Only a SHA-256 hash of the token is stored, so a copy of the database holds no working links. The token is after the `#` in the link, so a browser never sends it to any server.
- The link starts with `APP_BASE_URL` from `.env` (the dashboard's address), or `http://localhost:5173` if that is empty. The dashboard page that opens it comes in step 4 of Day 3.
- **The log holds a working link. That is fine for development only.** Before a real launch, replace `LogMailer` with a real mailer; the routes only call `send_password_reset`, so nothing else changes. Never turn this mock on for a server other people use.

### Logging out

A login token is a signed JWT, so the server can not "forget" it. Each token therefore carries its own id (`jti`). Logging out writes that id into `revoked_tokens` (only the id and the token's expiry date, nothing about the user), and every request checks that table. Rows are deleted once the token would have expired anyway. A password reset also raises the user's `token_version`, which ends all her older tokens at once.

How it is kept safe:

- **Passwords** are stored only as an Argon2id hash with a random salt (`app/security.py`). The password is never stored, logged or sent back, not even inside an error message.
- **Tokens** are signed with `JWT_SECRET` (at least 32 characters). If it is missing or short, login and `/me` answer `503` instead of making weak tokens. A token holds only the user id and its times.
- **One user can only reach her own data.** `/me` takes the user id from the verified token, never from the request. Any route added later that shows account data must depend on `current_user` (`app/auth.py`) and ask only for rows of that user.
- **Not done yet:** limits on repeated wrong passwords and on how often a reset link can be requested (anyone can ask for many links for an email; each new one cancels the older ones). Register, login and reset pages in the dashboard are also still to do.

To try it by hand in the browser (http://127.0.0.1:8000/docs): register, then `POST /auth/login`, copy the `access_token` from the answer, click **Authorize** (top right), paste it, and call `GET /me`. Type the password in that page only.

## Daily summaries (F8)

Both routes need `Authorization: Bearer <access_token>` and `ENCRYPTION_KEY` in `.env` (see [../README.md](../README.md)). A user only ever reaches her own rows: the user id comes from the verified login, never from the address or the body.

| Route | What it does |
|---|---|
| `PUT /summaries/{date}` | Body: the summary JSON of [Contract 2](../../contracts/summary-format.md), nothing added. `{date}` is `YYYY-MM-DD` and must equal `date` in the body. Answers `200` with `{"date", "result", "generated_at"}`. `result` is `created` (first summary for that day), `replaced` (the upload was newer than the stored one) or `ignored` (the stored one is as new or newer and stays). So sending the same summary twice changes nothing. |
| `GET /summaries/{date}` | Your own summary for that day, decrypted, exactly as uploaded. `404` if you have none for that day. |

A bad summary is **not stored** and gets `422` with `{"detail": {"message", "problems", "problems_not_shown"}}`. `problems` lists up to 20 lines such as `$.top_apps[0].name: 'Chapter 4 - Thermodynamics.pdf' does not match ...`; they come from the JSON Schema and the seven number checks of the contract (`app/summary_contract.py`). Other answers: `400` not JSON, `413` larger than 100,000 bytes, `401` not logged in (checked before the body is read), `503` no encryption key.

Rules of this server **beyond the contract**, as sanity checks: `date` may be at most one day ahead of today (UTC), and `generated_at` at most one day ahead of now, because a far-future build time would stop later uploads from replacing the summary.

**"The same day replaces the old one" follows the contract:** a *newer* `generated_at` replaces, an older or equal one is ignored. If you want every upload to replace, say so and Contract 2 must change too.

### Encrypted at rest

`app/crypto.py` encrypts each summary with **AES-256-GCM**, a fresh random nonce each time, using the key in `ENCRYPTION_KEY`. The user id and the date are part of what the encryption protects, so if someone with database access copies an encrypted summary into another user's row or another day, it fails to decrypt (the read answers `500`, with no data) instead of showing up there. The table holds no activity in readable form: only `user_id`, the date, the schema version, two times and the encrypted bytes. The tests check the stored bytes for readable text.

This is **encryption at rest with a server key, not end-to-end encryption**: the server can read summaries (it must, to compute the score and forecast). That is the 27 Oct decision in Contract 1. Not done: key rotation, and a rule for what happens to old days (the 30-day deletion is for raw events on the PC, not for summaries).

## Checking a summary against Contract 2

`app/summary_contract.py` checks a summary against the JSON Schema in `contracts/` and the seven "checks the server makes" from [contracts/summary-format.md](../../contracts/summary-format.md). `validate_summary(summary)` returns a list of problems; an empty list means valid. The upload route uses it. Tests run it on the contract's samples and on [../mock/](../mock/README.md).

## The database today

| Table | Holds |
|---|---|
| `users` | `id`, `email` (always lowercase, unique), `password_hash` (never the password), `plan` (`free` or `pro`), `created_at`, `token_version` (raised by a password reset) |
| `revoked_tokens` | Logged-out login tokens: `jti` (the token's id) and `expires_at`. Nothing else. |
| `password_reset_tokens` | One row per reset link: `user_id`, `token_hash` (SHA-256, never the token), `expires_at`, `used_at`. Deleting a user deletes hers. |
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
| `app/schemas.py` | What the account routes accept and return (email tidied, password length limits, no extra fields). |
| `app/security.py` | Argon2id password hashing, signed login tokens and reset-link tokens. |
| `app/auth.py` | `current_user`: reads the token, refuses logged-out or outdated ones, and loads that user. |
| `app/mailer.py` | The mock mailer: prints the reset link in the server log. |
| `app/crypto.py` | AES-256-GCM encryption of stored summaries. |
| `app/summary_contract.py` | Checks a summary against Contract 2. |
| `app/routes/health.py` | The two health routes. |
| `app/routes/accounts.py` | `register`, `login`, `logout`, `forgot-password`, `reset-password`, `me`. |
| `app/routes/summaries.py` | `PUT` and `GET /summaries/{date}`. |
| `alembic.ini`, `migrations/` | Database migrations (Alembic). The address is read from `DATABASE_URL`, never written in these files. |
| `tests/` | Automatic tests. |
| `requirements.txt`, `requirements-dev.txt` | Packages, pinned to the tested versions. |
