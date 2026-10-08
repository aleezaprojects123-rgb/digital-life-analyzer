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
   - Languages are the OCR languages installed in Windows for the user. English first; a second launch language stays an open question in the spec (Windows supports many through language packs).
   - Rejected: Tesseract (extra binaries and language data, larger and slower), cloud OCR (violates the spec), an ONNX OCR model (more RAM, more work for no gain on Windows).
2. **The screenshot only ever exists in memory.**
   - Capture the **active window only** with `PrintWindow` (no capture border or prompt), downscale if large, run OCR, then dispose the bitmap and zero its buffer.
   - It is **never written to disk**, which is stronger than "deleted within 5 s". A hard 5-second timeout cancels the OCR and destroys the image.
   - Windows that cannot be captured (some GPU-rendered or protected content) simply produce no label.
3. **Only a safe label is kept.**
   - The raw OCR text is processed in memory by the same on-device categorizer (rules first, then the model) and then discarded.
   - What is kept: a category label and a confidence. The text itself is never stored, logged or uploaded.
4. **When it may run.** All of these must be true:
   - OCR is switched on in settings (default off).
   - The app is on the user's OCR opt-in list.
   - Recording is allowed (consent given, not paused).
   - The window is not a private window and the app is not on the exclusion list.
   - The title is unclear (rules and model gave low confidence from the title alone).
   - Rate limit (assumption): at most one capture per window per 60 seconds.
5. **Audit log.** Every capture adds a row to `ocr_audit_log`: time, app, window title (as masked by the sensitive-title rules), trigger reason, number of characters read (a count only), resulting label, confidence, and how many milliseconds the image lived. No text and no image columns exist. It stays on the PC and is never uploaded.
6. **Where the user sees the audit log.** The tray menu is fixed at Pause/Resume, Open dashboard and Consent and privacy. The audit log is shown inside the Consent and privacy window (and later the dashboard settings page). The exact screen is decided when OCR is built.
7. **Consent.** OCR needs a separate, plain-language opt-in per app, shown when the user enables it. It is not covered by the first-run consent text; adding OCR changes what is read, so the consent text version is bumped when the feature ships.

## Risks

- Accuracy on games and custom UI may be modest; a failed or empty read is stored as no label, never guessed.
- Users may feel "screen reading" is invasive; mitigated by off-by-default, per-app opt-in, no storage, and a visible audit log.
- A missing Windows OCR language pack means no OCR for that language; the settings screen must say so.

## Open items

- Second launch language (spec open question).
- Final rate limit and which apps are suggested for opt-in.
- Whether the audit log also appears in the web dashboard (it must not upload the log).
