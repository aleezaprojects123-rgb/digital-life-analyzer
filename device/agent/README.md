# DLA Windows agent (C# / .NET 10)

## Current state: consent window only, no tray and no tracking yet

What the program does today:

1. **First start (nothing saved):** it shows the **consent window**: a title, the scrollable terms and privacy text, an **unticked** checkbox "I have read and agree", and two buttons. **Accept stays disabled until the box is ticked.** There is no default button, so pressing Enter never accepts.
2. **Decline, the X, Alt+F4 or Esc:** counts as **Decline**. The program exits and writes **nothing**: no file, no folder, no log line.
3. **Accept:** saves your consent, writes one line to a small status log, and exits. The tray is not built yet.
4. **Next start with a valid saved consent:** no window is shown. It writes one log line and exits.

It shows **no tray icon**, starts **no tracking** and has **no network, database or sync**. It does not read keyboard or mouse activity, the foreground app, idle time or URLs.

**Not done yet** (do not assume any of these work): the tray icon, tracking, the agent link to the extension, an `.exe` package or installer, starting with Windows.

## Where consent is saved

`%LocalAppData%\DigitalLifeAnalyzer\consent.json`, created **only after Accept**:

```json
{ "consentVersion": 1, "acceptedAtUtc": "2026-10-28T05:15:00Z" }
```

The file counts as consent only if it is well formed **and** its `consentVersion` equals the current terms version in `Consent/ConsentText.cs`. A missing, empty, corrupt, older or newer file means **not consented**, and the consent window is shown again. Deleting the file withdraws consent at the next start. The same folder holds `agent.log`, which holds only the agent's own status lines and never anything you did.

Honest limit: the file is plain text on your PC, so someone with access to your account could edit it. It records your own local choice; it is not a security boundary.

When the meaning of the terms changes, raise `ConsentText.Version`. Everyone is then asked again.

## For developers only

End users never run terminal commands. They will install a normal `.exe` that starts with Windows (a later day). The commands below are only for developers.

You need the .NET 10 SDK. In a terminal, in this folder (`device/agent/`):

```
dotnet build Dla.Agent.sln
dotnet test Dla.Agent.sln
dotnet run --project Dla.Agent
```

- `dotnet test` runs the automatic tests. They use a throwaway folder, never your real `%LocalAppData%`, and one test starts the real program with a saved consent and checks that it shows no window and exits.
- `dotnet run --project Dla.Agent` opens the real consent window. **Accepting writes your real consent file.** To try it without touching your real consent, point the program at a temporary folder first (PowerShell):

  ```
  $env:LOCALAPPDATA = "$env:TEMP\dla-try"; New-Item -ItemType Directory $env:LOCALAPPDATA -Force | Out-Null
  dotnet run --project Dla.Agent
  ```

  To start fresh, delete the folder (or just `consent.json`).

## How the code is organized for consent

| Piece | Role |
|---|---|
| `Consent/ConsentText.cs` | The terms text and the version constant. |
| `Consent/ConsentStore.cs` | Reads and writes `consent.json`. Strict: anything unexpected is "not consented". |
| `Consent/IConsentGate.cs`, `FileConsentGate.cs` | The consent question, and the real gate that reads the file. |
| `Consent/DefaultConsentGate.cs` | An "always no" gate, kept for tests. |
| `Ui/ConsentForm.cs` | The consent window. It only reports the decision; it saves nothing. |
| `OnboardingFlow.cs` | The start-up logic: valid consent, else ask, then save on Accept only. |
| `AgentLog.cs` | The small status log, written only after consent. |
| `Program.cs` | Wires the pieces together. A no-console Windows program. |
| `AgentHost.cs`, `Tray/*`, `AgentApplicationContext.cs` | The future tray, created only after consent. **Intentionally not used yet.** |

## What comes later

1. The tray, created only after consent (next task).
2. The local status link for the extension.
3. Tracking (foreground app and idle or presence), following `contracts/event-schema.md`.
4. Packaging as a normal `.exe`, and starting with Windows.
