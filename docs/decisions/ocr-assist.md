# Decision: OCR Assist

Status: **decided, not built.** Only the `ocr_audit_log` table is created in Phase 0 (Step 5). The capture and OCR code comes in a later phase.
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
5. **Audit log.** Every capture adds a row to `ocr_audit_log`: time, app, window title (as masked by the sensitive-title rules), trigger reason, number of characters read (a count only), resulting label, confidence, and how many milliseconds the image lived. No text and no image columns exist. It stays on the PC and is never uploaded.
6. **Where the user sees the audit log.** The tray menu is fixed at Pause/Resume, Open dashboard and Consent and privacy. The audit log is shown inside the Consent and privacy window (and later the dashboard settings page). The exact screen is decided when OCR is built.
7. **Consent.** OCR needs a separate, plain-language opt-in per app, shown when the user enables it. It is not covered by the first-run consent text; adding OCR changes what is read, so the consent text version is bumped when the feature ships.

## Trigger policy

**OCR runs only when ALL of these are true:**

1. Consent is given and recording is not paused.
2. OCR is enabled for **that app** (off by default; per-app opt-in).
3. The window is in the **foreground** and the user is **not idle**.
4. The title is **unclear**, meaning any of:
   - the model's confidence from the title alone is under 60%;
   - the title is empty;
   - the title is generic, such as "Document1" or "Untitled";
   - the title is the same as the app name.

**OCR NEVER runs for:**

- apps that are not opted in;
- hidden or excluded apps or sites;
- private windows;
- password-manager or banking-type windows;
- background windows (only the active window is ever captured).

**Capture handling, always:**

- Only the active window is captured.
- The screenshot is deleted within 5 seconds, **even if OCR fails or times out**.
- **Every capture** is written to the audit log, including failed ones.

**Examples**

| Situation | Title clear? | OCR? |
|---|---|---|
| VS Code showing `Program.cs - dla - Visual Studio Code` | Yes: the model is confident | No |
| Word showing `Document1` | No: generic title | Yes, if Word is opted in |
| A game window, title is the game name or empty | No: the title says nothing about the activity | Yes, if the game is opted in |
| Word showing `Document1`, but Word is not opted in | No | No: the app is not opted in |
| A password manager window | Not relevant | No: never |

**Assumption, NOT in the spec:** at most one capture per window per N minutes, to limit load and intrusion. N is to be tested in Phase 4. No value is fixed until then.

**Assumption, NOT in the spec:** "password-manager or banking-type" windows are recognised by a built-in deny list (known password-manager executables, banking words in the title or site) plus the user's exclusions. The list and its limits are defined when OCR is built.

**Decision to revisit in Phase 4:** only a **category label and a confidence** are kept from each capture (the default accepted for now). Revisit whether this is enough to debug wrong labels without keeping any text.

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
- A missing Windows OCR language pack means no OCR for that language; the settings screen must say so.

## Open items

- OCR launch languages: **decided** (English only; see above). Later languages (fr, de, es via Windows packs; Urdu via another engine) are roadmap.
- The per-window capture interval N (Phase 4) and which apps are suggested for opt-in.
- Revisit "label and confidence only" in Phase 4.
- Whether the audit log also appears in the web dashboard (it must not upload the log).
