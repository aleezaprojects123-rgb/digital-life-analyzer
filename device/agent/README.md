# DLA Windows agent (C# / .NET 10)

## Current state: development shell, it does nothing yet

This is only the **foundation** of the agent. Today it:

- builds,
- shows **nothing**: no window and **no tray icon**,
- starts **no tracking**: it does not read keyboard or mouse activity, the foreground app, idle time or URLs, and it has no database, no background monitoring and no sync,
- prints one line and **exits immediately**, leaving no process running:

  `Development shell: no UI and no tracking started. Waiting for the future onboarding and consent screen.`

**Not done yet** (do not assume any of these work): the onboarding and consent screen, saving consent, the tray icon, tracking, packaging as an `.exe`, an installer, and starting with Windows.

## Consent comes first (how the code is built for it)

The intended flow is: the user opens Digital Life Analyzer, a real onboarding screen shows the terms and privacy information, the user **explicitly accepts**, and only then is the tray icon created and the consented features turned on. If the user declines, closes the screen or has not accepted, no tray icon and no tracking start. Opening the app is never consent, and consent is never assumed or on by default.

The code is already structured for this:

| Piece | Role |
|---|---|
| `IConsentGate` | One question: has the user explicitly accepted? |
| `DefaultConsentGate` | The only gate that exists today. It **always says no**. |
| `AgentHost` | Starts the agent **only if the gate says yes**. Without consent it creates nothing. |
| `TrayController` | The future tray icon (Tracking: ON, Open dashboard, Quit). **Intentionally not used yet.** Only `AgentHost` can create it, and only after consent. |
| `AgentApplicationContext` | The future tray-only lifecycle (no window; lives in the tray until Quit). **Intentionally not used yet.** It refuses to exist unless the agent was started with consent. |
| `Program.cs` | Creates the host with the default gate, starts nothing, prints the line above and exits. |

## For developers only

End users never run terminal commands. They will install a normal `.exe` that starts with Windows (a later day). The commands below are only for developers building and testing the code.

You need the .NET 10 SDK. In a terminal, in this folder (`device/agent/`):

```
dotnet build Dla.Agent.sln
dotnet test Dla.Agent.sln
dotnet run --project Dla.Agent
```

- `dotnet build` compiles the agent.
- `dotnet test` runs the automatic tests, including one that starts the real program and checks that it prints the line, exits on its own and leaves no process behind.
- `dotnet run --project Dla.Agent` runs the shell. You should see the one line and no tray icon.

## What comes later

1. The onboarding and consent screen (terms and privacy information, explicit Accept or Decline).
2. Saving the user's consent, and a real consent gate that uses it.
3. The tray icon, created only after consent.
4. Tracking (foreground app and idle or presence), following `contracts/event-schema.md`.
5. Packaging as a normal `.exe`.
6. Starting with Windows.

## Files

| File | Purpose |
|---|---|
| `Dla.Agent.sln` | The solution: the agent and its tests. |
| `Dla.Agent/Dla.Agent.csproj` | The agent project (C#, .NET 10, WinForms). A console program for now; it becomes a no-window program when the real UI and tray arrive. |
| `Dla.Agent/Program.cs` | The entry point described above. |
| `Dla.Agent/AgentHost.cs` | Starts the agent only with consent. |
| `Dla.Agent/AgentApplicationContext.cs` | The future tray-only lifecycle (not used yet). |
| `Dla.Agent/Consent/IConsentGate.cs`, `DefaultConsentGate.cs` | The consent question and the "always no" gate. |
| `Dla.Agent/Tray/ITray.cs`, `TrayController.cs` | The tray interface and the future tray icon (not used yet). |
| `Dla.Agent.Tests/` | Automatic tests. |
