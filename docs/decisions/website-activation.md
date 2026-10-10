# Decision: website-based activation and consent

Status: **approved in principle, not built.** Implementation waits for the backend (Fatima: Interface C settings, Interface D login).
Decided: 2026-10-09. Owner: Aleeza (agent and extension).

## Context

The intended user flow is: the user opens the DLA website, creates an account and agrees to the terms and conditions; then the desktop agent and the browser extension start and keep running until the user chooses to stop them or the PC shuts down.

The spec (docs/) requires local-first privacy, nothing recorded before consent, raw data never leaving the PC, and offline operation after setup.

## What is and is not technically possible

| Idea | Verdict |
|---|---|
| A website runs a local program directly | No. It can open a custom URL scheme (`dla://...`); Windows passes it to the registered handler after the browser asks the user to confirm. |
| A website installs the browser extension silently | No. Chrome and Edge require the user to click "Add" in the store. Silent install exists only through enterprise policy. |
| The extension runs when the browser starts | Yes, once installed and enabled. The user can disable it; we never force-install or re-enable it. |
| The agent runs after login | Yes. HKCU Run key (already built in Phase 0), no admin rights. |
| Extension to agent link | Yes. Chrome native messaging (per-user HKCU registration) or a localhost link; chosen in Phase 1. |

## Decision

1. **Website terms cover the account only.** The user creates an account and accepts the website terms (version and timestamp stored by the backend).
2. **Local consent stays the source of truth for recording.** The agent keeps its own consent record (state, timestamp, consent-text version) even though the website also records the terms. Nothing is recorded until the local consent is Accepted. Withdrawing locally stops recording and removes auto-start.
3. **Activation link `dla://activate?code=<one-time code>`.**
   - Registered per user under `HKCU\Software\Classes\dla` (no admin rights).
   - The code is single-use, short-lived and carries no personal data.
   - The agent exchanges the code with the backend for a revocable per-device token, then shows the existing local consent screen. Recording starts only after the user presses Accept.
4. **Simplest end-to-end flow:**
   1. Website: sign up, accept terms, download the signed per-user installer.
   2. Installer: installs the agent, registers `dla://` in HKCU, adds the Run entry only after consent (as built).
   3. `dla://activate` (button on the website) forwards the code to the single running agent (second launch passes it over a named pipe, then exits).
   4. Agent shows the local consent screen; the user accepts.
   5. Website links to the Chrome/Edge store page; the user clicks "Add". The agent tells the extension whether consent was given (extension stays inactive without it).
   6. After setup everything works offline; the internet is needed only to sync encrypted summaries.
5. **No Quit.** The user controls the agent through Pause (process stays, records nothing) and Withdraw consent (stops recording and removes auto-start). A crash is restarted by the watchdog; only a Windows end-session is a clean stop. This keeps the spec's visible-tray-at-all-times rule. Uninstall is through Windows Settings > Apps.
   - If a stop option is ever required, add "Stop DLA until next login": confirmation, a "user stopped" marker, the supervisor does not restart it, and it returns at the next login. Not planned for v1.

## Contract changes (for Fatima)

- **Interface D (login):** add the activation flow: the website issues a one-time code; the agent exchanges it for a revocable device token; add endpoints to revoke a device.
- **Interface C (settings):** add the website terms version and accepted-at timestamp so the agent can detect changed terms; add a device list.
- **Website to build:** sign-up and terms checkbox (versioned), installer download page, `dla://activate` button, extension store link, activation-code endpoint, device list with revoke.

## Agent changes (later, not in Phase 0)

- Register `dla://` in HKCU and handle `--activate <url>`, forwarding it to the running instance (single instance is already enforced).
- Store `account_linked` and `terms_version` in settings; keep the consent screen unchanged, shown after activation.
- Validate the code with the backend before storing anything. Never put the code or token in logs or the repo.

## Open items

- Native messaging vs localhost for the extension link (Phase 1).
- Behaviour when the website terms change (re-consent prompt on the website; local consent is re-asked only when the consent-text version changes).

## Update 2026-10-10: consent only on the website (agent side built; website side not built)

Aleeza's requirement: consent and terms are shown and accepted **only on the official website**. The agent must not
record, and must not show a green running icon, until that consent is verified; it stops on withdrawal and never
restarts monitoring without fresh consent. The agent itself shows no consent text.

**Built in the agent (2026-10-10):**
- No consent window exists in the agent any more. The tray icon opens nothing on click or double-click.
- The tray menu is exactly Pause/Resume and Open Dashboard.
- At start the agent checks its consent record. Unless it is Accepted, for the current text version (3), **and**
  marked as given on the website, the agent shows nothing, records nothing, removes any start-at-login entry and
  exits. So no tray icon and no green icon appear until website consent is on record, and a PC restart after a
  decline or no consent starts nothing.
- A consent record written by an earlier build (no source) is not valid, so an old local acceptance no longer
  activates the agent.
- Withdraw and decline keep the record invalid.

**Not built, and why:** there is no website or backend in this repository, and the agent has no network, `dla://`
or token code. The only function that can mark consent as given on the website is internal and nothing calls it
yet. The handshake (Interface D) must: open via `dla://activate`, exchange the one-time code, receive a
**server-signed consent assertion** (consent version, time, device), verify its signature, and only then record it.
Until the signature check exists, `consent.json` can still be written by hand by the user or by malware running as
the user; the tests pin this limit. Needs Fatima: G1, G3, A1, D1, D3 plus a decision on the signing key.

**Consequence:** until the website exists the agent cannot be activated, so it cannot be demonstrated end to end.
Phase 1 recording work will be tested through unit tests, not through a running tray agent.
