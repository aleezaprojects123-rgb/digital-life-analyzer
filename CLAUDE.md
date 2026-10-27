# Digital Life Analyzer: project rules

University team project (Windows only). Two people: Aleeza owns `device/`, Fatima owns `cloud/`. `contracts/` is shared and needs both approvals.

## What we are building
- A background Windows agent written in **C# (.NET 10)**, tray icon only, that logs the foreground app and idle time.
- A **Chrome/Edge extension (Manifest V3)** that logs the active website.
- Both write events using the schema in `contracts/event-schema.md` into a local SQLite database, kept 30 days, then merged into one timeline.
- An on-device AI model (ONNX Runtime) labels time as Study, Work, Entertainment, Social Media or Other, with a confidence score. Low confidence becomes "Other". A rule-based fallback works if the model is off.
- Only encrypted daily summaries (`contracts/summary-format.md`) go to the cloud. Never raw events, page text or window titles.
- Cloud side (Fatima): FastAPI + PostgreSQL, React dashboard, Stripe test mode.

## Hard rules
- No screenshots, no keystrokes, no typed text, private windows never recorded.
- Raw events stay on the PC. Everything must work offline.
- Hidden apps and sites are skipped before anything is read.
- Tracking must stay light: low CPU and memory.
- The agent must be packaged as a normal `.exe` that starts with Windows; users never run code.

## Working rules
- Aleeza and Fatima commit directly to main, only inside their own folder (device/ for Aleeza, cloud/ for Fatima), and run git pull before every push. Never force-push. Changes to contracts/ go through a pull request and need the other person's approval. Proposed by Aleeza on 27 Oct 2026; Fatima to confirm.
- Only edit your own folder. Contract changes go in `contracts/`.
- Do one task at a time, explain each file briefly, and say how to run and test it.

## Rules from the PRD (fixed)
1. Extension and agent events share one schema (`contracts/event-schema.md`) and merge in time order, with overlap rules (F5).
2. Events are stored only on the PC in SQLite, auto-deleted after 30 days, and survive restarts (F4).
3. Idle time is never counted as active (F3).
4. Every label has a category (Study, Work, Entertainment, Social Media, Other) and a confidence. Low confidence falls back to uncertain `Other`. No screenshots or OCR (F6).
5. No screenshots, keystrokes or typed text. Page text is read only on the device, never uploaded and not stored (only the label is kept).
6. Hidden sites and apps are skipped before anything is read (F7).
7. Only encrypted summaries leave the PC, never raw events or page text (F8).

## Team decisions (agreed 26 Oct 2026, not in the PRD)
1. **URL:** the extension stores a sanitized URL: scheme, domain and path only. Query string and fragment are always removed. Long random-looking path segments and known sensitive patterns (reset or login tokens, session ids, email or account ids) are masked with `*`. Cleaning happens inside the extension before anything is sent. The URL stays on the PC and is never synced. `domain` is kept too, and reports use `domain`.
2. **Idle and presence:** decided only by the Windows agent from system-wide inactivity plus passive-activity signals (foreground media or audio, calls) and context (reading apps), with states active, passive, uncertain and away. The extension reports only browser-specific signals (active tab, tab changes, tab audible, video playing) and never decides presence.
3. **Overlap:** an extension event counts only while the browser (`chrome.exe` or `msedge.exe`) is the foreground app. When the extension and the agent both cover the same moment, count it once and keep the extension event. If the extension has no event, keep the agent event with `domain` null. Two agent events never overlap.
4. **Other:** `Other` with `uncertain: true` is a last resort, used only when the activity cannot reasonably be identified from the available context.
5. **Categorization:** use all available context (app, domain, URL, permitted title, on-device page or media text, time of day) instead of basic guesses. If the model is off, rules fill the category with `label_source: "rules"`.
6. **Hidden apps and sites:** no event is recorded at all.
7. **Private/incognito windows:** not recorded.
8. **Privacy:** never read message bodies, inboxes, DMs, input fields or typed text. Mask sensitive titles.
9. **Extension distribution:** one shared extension codebase, released through the official stores only. Order: build and test unpacked in Edge and the agent; publish to Microsoft Edge Add-ons as Hidden; test the store build with the Windows agent (consent, communication, tracking); then make it public. Then prepare Chrome from the same code, pay the one-time Chrome Web Store fee, submit with icons, screenshots, description, privacy policy URL, permission justifications and data-use disclosures, and publish after approval. Users install the DLA Windows app, complete consent and setup, then install the extension from the official store. No unofficial install instructions for end users. Unpacked loading is developer-only. A hosted privacy policy page is required before submission. The store release is after the 15-day sprint; the demo uses the unpacked extension.
