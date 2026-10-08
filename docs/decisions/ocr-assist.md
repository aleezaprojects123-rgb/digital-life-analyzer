# Decision: OCR Assist

Status: **decided, not built.** Only the `ocr_audit_log` table is created in Phase 0 (Step 5). The capture and OCR code comes in a later phase (Phase 4). The consent flow (just-in-time prompt) was decided on 2026-10-09.
Owner: Aleeza (agent). Spec: FR-30, "Efficient OCR Assist", "Privacy and Scope", Open Question "Which OCR languages ship at launch".

## What the spec requires

- Optional, **off by default**, **per-app opt-in**.
- Reads on-screen text only when the window title is unclear (games, IDEs, custom apps) **and** the app is opted in.
- **Active window only**, **on-device**, screenshot **deleted within 5 seconds**, only a **safe text label** is kept.
- **Every capture** appears in a user-visible **audit log**.
- OCR output and screenshots are **never uploaded** and never stored. Works with the internet unplugged.

## Decision

1. **Engine: the Windows built-in OCR (`Windows.Media.Ocr`).**
   - It runs on-device, ships with Windows 10/11, and needs no model download or extra memory.
   - This keeps the agent inside the 150 MB budget (250 MB with the categorization model).
   - Languages: **launch with English (en-US) only**; see "Launch language decision" below.
   - Rejected: Tesseract (extra binaries and language data, larger and slower), cloud OCR (violates the spec), an ONNX OCR model (more RAM, more work for no gain on Windows).
2. **The screenshot only ever exists in memory.**
   - Capture the **active window only** with `PrintWindow` (no capture border or prompt), downscale if large, run OCR, then dispose the bitmap and zero its buffer.
   - It is **never written to disk**, which is stronger than "deleted within 5 s". A hard 5-second timeout cancels the OCR and destroys the image.
   - Windows that cannot be captured (some GPU-rendered or protected content) simply produce no label.
3. **Only a safe label is kept.**
   - The raw OCR text is processed in memory by the same on-device categorizer (rules first, then the model) and then discarded.
   - What is kept: a category label and a confidence. The text itself is never stored, logged or uploaded.
4. **When it may run:** see "Trigger policy" below. It is a strict allow-list: every condition must hold, and any "never" case blocks it.
5. **Audit log.** Every capture adds a row to `ocr_audit_log`: time, app, window title (as masked by the sensitive-title rules), trigger reason, number of characters read (a count only), resulting label, confidence, and how many milliseconds the image lived. No text and no image columns exist. It stays on the PC and is never uploaded. **Every consent answer and every change to an answer is logged too** (see "Consent flow"). That needs a schema change that is **not built yet**: the Phase 0 table has one row per capture with capture-only columns that cannot be empty.
6. **Where the user sees the audit log and the OCR settings.** The tray menu is fixed at Pause/Resume, Open dashboard and Consent and privacy. The audit log and the OCR settings are shown inside the agent's Consent and privacy window. They are **not** on the web dashboard's settings page, because OCR settings are local-only (OQ-C4) and the dashboard cannot read them. The exact screen is decided when OCR is built.
7. **Consent.** OCR needs a separate, plain-language, per-app answer from the user, collected **just in time**: a small prompt the first time OCR would help in an app. It is not collected only in Settings. See "Consent flow" below.

## Trigger policy

**OCR runs only when ALL of these are true:**

1. Consent is given and recording is not paused, and OCR is not switched off globally.
2. The user has allowed OCR for **that app**: they answered **Always** for it (OCR is off by default and per-app, as in the spec), or they chose **Just this time** for this one capture. See "Consent flow".
3. The window is in the **foreground** and the user is **not idle**.
4. The title is **unclear**, meaning any of:
   - the model's confidence from the title alone is under 60%;
   - the title is empty;
   - the title is generic, such as "Document1" or "Untitled";
   - the title is the same as the app name.

**OCR NEVER runs for:**

- apps the user has answered **Never** for, or has not answered for yet (nothing is captured until they answer);
- hidden or excluded apps or sites;
- anything on the **fixed built-in blocklist** (password managers, banking and payment sites, private or incognito windows, the lock screen), which the user cannot enable;
- background windows (only the active window is ever captured).

**Capture handling, always:**

- Only the active window is captured.
- The screenshot is deleted within 5 seconds, **even if OCR fails or times out**.
- **Every capture** is written to the audit log, including failed ones.

**Examples**

| Situation | Title clear? | OCR? |
|---|---|---|
| VS Code showing `Program.cs - dla - Visual Studio Code` | Yes: the model is confident | No |
| Word showing `Document1` | No: generic title | Yes, once the user has allowed Word (Always, or Just this time) |
| A game window, title is the game name or empty | No: the title says nothing about the activity | Yes, once the user has allowed the game (Always, or Just this time) |
| Word showing `Document1`, first time, no answer yet | No | Not yet: the prompt appears; nothing is captured until the user answers; the agent uses app name + title only |
| Word showing `Document1`, user answered Never for Word | No | No |
| A password manager window | Not relevant | No: blocklist, and no prompt appears |

**Assumption, NOT in the spec:** at most one capture per window per N minutes, to limit load and intrusion. N is to be tested in Phase 4. No value is fixed until then.

**Assumption, NOT in the spec:** the blocklist (see "Consent flow") is recognised by a built-in list of known executables, domains and title words, plus the lock-screen state, together with the user's own exclusions. Such a list is a heuristic and can miss a window; the user's exclusions and the fixed list together are the defence, and the limits are stated in the privacy text. The exact list is defined when OCR is built and ships with agent updates.

**Decision to revisit in Phase 4:** only a **category label and a confidence** are kept from each capture (the default accepted for now). Revisit whether this is enough to debug wrong labels without keeping any text.

## Consent flow

**Decided by Aleeza, 2026-10-09 (Option A, just-in-time prompt).** The spec's rule is unchanged: OCR is off by default and per-app. Only **how** the per-app consent is collected changes: at the moment OCR would help, not by hunting through Settings first.

**1. Until the user answers, nothing is captured.** OCR stays off for an app until the user allows it. While there is no answer, the agent uses **app name + window title only** and captures nothing.

**2. When the prompt appears.** The first time OCR would help in an app: the window is the foreground window, the user is not idle, OCR is not switched off globally, the app and window are not on the blocklist, and the title is unclear (model confidence under 60%, empty, generic such as "Document1", or the same as the app name).

**3. The prompt (exact wording).**

> Digital Life Analyzer can read the window text to understand what you're doing in **\<App\>**. The picture is never saved and is deleted within 5 seconds. Allow?
>
> **[Always for \<App\>]**  **[Just this time]**  **[Never]**

- `<App>` is the app's display name (for example "Microsoft Word"). The prompt never shows the window title, which can be private.
- Closing the prompt without pressing a button is **not an answer**: nothing is captured, and the prompt counts as shown for the nagging rule below.
- Proposal: no button is the default, and Enter or Space do not answer, so a keystroke meant for another window cannot grant consent by accident. The prompt should not take keyboard focus while the user is typing elsewhere. (How it looks, a small window or a toast with buttons, is decided when built.)

**4. What each answer does.**

| Answer | Effect | Remembered? |
|---|---|---|
| **Always for \<App\>** | OCR may run in this app whenever the trigger policy is met | Yes, per app |
| **Just this time** | Allows exactly **one capture now**, of the current window | **No** |
| **Never** | OCR never runs in this app, and the prompt never appears again for it | Yes, per app |

**5. Stopping the prompt from nagging (proposal, numbers to be tested in Phase 4, NOT in the spec).**
- **At most one prompt per app per local calendar day**, unless the user answered Always or Never (those end the question).
- **At most 3 prompts per day across all apps.**
- If the same app has been prompted on **3 days in a row** and the user only chose "Just this time" or dismissed it, treat it as **Never** until the user changes it in Settings. This parallels the nudge rule in FR-21 (off by default after 2 ignores).
- While a prompt is waiting for an answer, no second prompt is shown.

**6. Changing answers later.** In the OCR settings (inside the agent's Consent and privacy window, see Decision item 6), the user can: see every app they answered for, change any answer (Always / Never) or remove it so they are asked again, and **switch OCR off globally**. With OCR off globally, no prompt appears and nothing is captured.

**7. Fixed built-in blocklist (the user cannot enable these).** OCR never runs, and the prompt never appears, for:
- password managers;
- banking and payment sites;
- private or incognito windows;
- the lock screen.

These are a product decision by Aleeza. They do not depend on the user's answers: even an earlier "Always" for a browser does not cover a blocklisted site or window. For browsers the blocklist is matched on the site, so "Always for Chrome" still never reads a banking page.

**8. Audit log.** Every answer (Always, Just this time, Never, and a dismissed prompt), every later change in Settings, and every OCR use goes to the audit log, with the time and the app. The audit log never holds window text or images. The Phase 0 `ocr_audit_log` table has one row per capture and cannot hold an answer, so Phase 4 needs a new migration (for example an event type plus a separate table for the remembered per-app answers). **Not built; no code or schema changed by this decision.**

**9. Local-only.** The remembered answers, the global switch and the audit log stay on the PC and are never synced (consistent with OQ-C4, Interface C has no OCR field).

**10. Language.** English (en-US) only at launch (see below).

**What this does not change:** the trigger policy, the 5-second rule, "label and confidence only", or the audit log requirement. The only code touched is the first-run consent text, which now has one sentence about this prompt (consent text version 2).

## Launch language decision

**Decided by Aleeza, 2026-10-09: launch with English (en-US) only, using the built-in Windows OCR.** This closes the spec's open question "Which OCR languages ship at launch".

- **Evidence (reported by Aleeza):** on the dev PC (Windows 10 Pro), the output of `Get-WindowsCapability -Online | Where Name -Like 'Language.OCR*'` lists no `ur-PK` entry, so Windows offers no Urdu OCR pack. (That command needs an administrator PowerShell; it was run by Aleeza, not re-run in the Claude session. For reference, the same PC's installed recognizers are `en-US` only, and its Windows language list is `en-US` and `ur-PK`.)
- **Other Windows-supported languages** (for example French, German, Spanish) can be added later by installing the OCR pack on the user's PC. That needs no new engine, but each pack is a separate Windows install on every user PC and is not part of launch.
- **Urdu** would need a different OCR engine. It is **future roadmap, not launch**.
- **Fallback for unreadable text:** when OCR is unavailable (language pack missing, window cannot be captured, or text unreadable), the agent uses **app name + window title only** and records no OCR label. This is also what happens when the user has OCR off, so the fallback needs no extra consent.
- **Consequence for the settings screen:** it must say that OCR reads English only at launch, and that other languages need their Windows OCR pack installed.

## Risks

- Accuracy on games and custom UI may be modest; a failed or empty read is stored as no label, never guessed.
- Users may feel "screen reading" is invasive; mitigated by off-by-default, per-app opt-in, no storage, and a visible audit log.
- The prompt itself interrupts the user and some will click Allow without reading; mitigated by plain wording, no default button, the nagging limits above, and the fixed blocklist that no answer can override.
- A heuristic blocklist can miss a sensitive window; mitigated by also honoring the user's exclusions, by prompting only per app, and by being honest about the limit in the privacy text.
- A missing Windows OCR language pack means no OCR for that language; the settings screen must say so.

## Open items

- OCR launch languages: **decided** (English only; see above). Later languages (fr, de, es via Windows packs; Urdu via another engine) are roadmap.
- The per-window capture interval N and the prompt-limit numbers (once per app per day, 3 per day, 3 days in a row) (Phase 4).
- The audit-log and per-app-answer schema (new migration) and the exact OCR settings screen (Phase 4).
- The exact blocklist contents and how it is updated (Phase 4).
- ~~Whether the first-run consent text should mention the optional OCR prompt.~~ **Done:** the first-run consent text (version 2) now has one sentence saying that, if a window title is unclear, DLA may ask to read that window's text on this PC (never saved, deleted within 5 seconds), and that the user can answer Always, Just this time or Never and change it later. Everyone who accepted version 1 is asked again.
- Revisit "label and confidence only" in Phase 4.
- The audit log and the OCR settings are local-only, so they cannot appear in the web dashboard (OQ-C4, and the log is never uploaded).
