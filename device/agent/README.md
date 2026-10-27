# DLA Windows agent (C# / .NET 10)

## Current state: consent window, tray and status endpoint, no tracking yet

What the program does today:

1. **Only one copy runs.** A second copy that starts exits quietly, so two tray icons (or two consent windows) can never appear.
2. **First start (nothing saved):** it shows the **consent window**: a title, the scrollable terms and privacy text, an **unticked** checkbox "I have read and agree", and two buttons. **Accept stays disabled until the box is ticked.** There is no default button, so pressing Enter never accepts.
3. **Decline, the X, Alt+F4 or Esc:** counts as **Decline**. The program exits with **no tray icon**, no process left, and writes **nothing**: no file, no folder, no log line.
4. **Accept:** saves your consent and then **creates the tray icon** and keeps running in the tray with no window.
5. **Next start with a valid saved consent:** no consent window; the tray icon is created straight away.

The tray icon is created **only by `AgentHost`, and only after the consent gate says yes**. It is never created at start-up.

**Tray menu** (right-click the icon; Windows may keep new icons in the hidden-icons panel behind the `^` arrow):

| Item | What it does today |
|---|---|
| **Tracking: ON** / **Tracking: OFF** | Toggles the agent's own pause state, and the label and icon colour show it (default ON after consent). **It only changes that value.** Nothing is tracked yet, and the tooltip says so. |
| **Open dashboard (coming later)** | Disabled until the dashboard exists. |
| **Quit** | Removes the icon and ends the program cleanly. |

The pause state lives in memory only, so it is ON again after a restart. Pausing never changes consent.

**Not done yet** (do not assume any of these work): tracking of any kind, any transfer of events from the extension, watching for consent being withdrawn while the agent runs (a deleted consent file takes effect at the next start), an `.exe` package or installer, starting with Windows.

## Local status endpoint (status only)

After consent, `AgentHost` starts a tiny local web endpoint so the browser extension can ask one question: "may I record right now?". **It is status only. No events, no page data, nothing about what you did is sent or received.**

```
GET http://127.0.0.1:49721/status
-> {"consented":true,"trackingEnabled":true,"consentVersion":1}
```

- **Never before consent.** Nothing listens until the consent gate says yes (it is created by `AgentHost`, not at start-up). Quit closes the port.
- **This PC only.** It binds to `127.0.0.1` (a plain loopback socket), so other computers cannot reach it.
- **One endpoint.** Any other path or method, a wrong `Host` header, a malformed or repeated header, or an oversized request gets `403` (or a hang-up).
- **Origin allowlist.** Only a request whose `Origin` is exactly `chrome-extension://<id>` (Chrome and Edge) or `moz-extension://<uuid>` **and** whose ID is in the allowlist gets `200`. Web pages (`https://...`, `http://localhost`, `null`) and requests with no Origin get `403`. CORS headers (`Access-Control-Allow-Origin`) are sent **only** to an allowed origin, and only echo that origin.
- **Extensions that send no Origin.** Tested in Edge: a `GET` from an extension that has the host permission carries **no `Origin` header** (the browser treats it like a same-origin request). Such a request is answered with `200` only if it has **no Origin** and an `X-DLA-Extension-Id` header whose value is an allowlisted ID, and the answer has no CORS header. A request that has an Origin is judged by the Origin alone (a good ID header does not rescue a bad Origin). A web page cannot send a custom header without a CORS preflight, and the preflight (`OPTIONS`) gets `403`. Like the Origin, the ID header is self-declared, so it keeps web pages out but not a program on this PC.
- **`consented` is `false` while tracking is paused** (tray: Tracking OFF), and also without consent, so an extension that follows it stays silent. `trackingEnabled` is never `true` without consent. `consentVersion` is `0` without consent.
- **If the port is taken**, the agent logs one line and keeps running (tray works, extension sees "agent unreachable" and records nothing).
- The port is the constant `AgentLinkConfig.DefaultPort` (49721) in `Link/AgentLinkConfig.cs`, the one place for the port and the allowlist. The link sits behind the `IAgentLink` interface so it can be swapped for Native Messaging later.

### The allowlist file

`%LocalAppData%\DigitalLifeAnalyzer\agent-link.json`. **Default: the file does not exist, so no extension is allowed.** The agent never creates it. Format:

```json
{ "allowedExtensionIds": [ "abcdefghijklmnopabcdefghijklmnop" ] }
```

(That ID is made up. Use your own.) A missing, corrupt or oversized file allows nothing; entries that do not look like a real ID are dropped. The file is read when the agent starts, so restart after editing it.

**Developer only: find your unpacked extension's ID** at `chrome://extensions` or `edge://extensions` (turn on Developer mode; the ID is under the extension name). An unpacked extension's ID depends on its folder path on that PC, so each developer adds their own. Production builds ship with an empty list; real store IDs are added later, once they exist. Do not commit real IDs or your `agent-link.json`.

### Security notes

- The Origin check keeps ordinary **web pages** out. It is not a defence against another program on this PC, which can send any Origin header it likes. That is why the endpoint exposes only status, nothing sensitive.
- **A token cannot be shared safely with an extension** (an extension has no secret store the agent can trust), so a token on this endpoint would give false security. **Event transfer from the extension will use a different, stronger design, decided on Day 3.** Nothing in this endpoint accepts or returns events.

## Where consent is saved

`%LocalAppData%\DigitalLifeAnalyzer\consent.json`, created **only after Accept**:

```json
{ "consentVersion": 1, "acceptedAtUtc": "2026-10-28T05:15:00Z" }
```

The file counts as consent only if it is well formed **and** its `consentVersion` equals the current terms version in `Consent/ConsentText.cs`. A missing, empty, corrupt, older or newer file means **not consented**, and the consent window is shown again. The same folder holds `agent.log`, with only the agent's own status lines, never anything you did.

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

- `dotnet test` runs the automatic tests. They use a throwaway folder, never your real `%LocalAppData%`. They never show the tray icon.
- `dotnet run --project Dla.Agent` opens the real consent window, and after Accept it **leaves a tray icon running until you choose Quit**. **Accepting writes your real consent file.** To try it without touching your real consent, point the program at a temporary folder first (PowerShell):

  ```
  $env:LOCALAPPDATA = "$env:TEMP\dla-try"; New-Item -ItemType Directory $env:LOCALAPPDATA -Force | Out-Null
  dotnet run --project Dla.Agent
  ```

  To start fresh, delete that folder (or just `consent.json`). Always end the agent with **Quit** in the tray menu. Ending it from Task Manager can leave a ghost icon in the tray until you hover over it.
- Rebuilding fails while the agent is running (the `.exe` is locked). Choose Quit first.

## How the code is organized

| Piece | Role |
|---|---|
| `Program.cs` | Wires the pieces together. A no-console Windows program. First line of defence: `SingleInstance`. |
| `SingleInstance.cs` | One copy at a time (a named mutex). |
| `AgentLauncher.cs` | The start-up in order: onboarding, then (only with consent) the tray, then run until Quit. |
| `OnboardingFlow.cs` | Valid consent, else ask, then save on Accept only. |
| `Consent/*` | The terms text and version, the strict consent file reader and writer, and the gate. |
| `Ui/ConsentForm.cs` | The consent window. It only reports the decision; it saves nothing. |
| `AgentHost.cs` | Starts the tray and the status link only if the consent gate says yes (it checks again itself). |
| `Link/*` | The status endpoint: `LocalStatusLink` (loopback socket, request check), `AgentLinkConfig` (port and allowlist), `AgentStatus` (the JSON), `IAgentLink`. |
| `AgentApplicationContext.cs` | The tray-only lifecycle: no window, ends when Quit is chosen. It refuses to exist unless the host started. |
| `Tray/TrayController.cs`, `TrayIconFactory.cs` | The tray icon, its menu and the placeholder icon drawn in code. |
| `AgentState.cs` | The agent's "tracking enabled" value (the pause state). Not consent. |
| `AgentLog.cs` | The small status log, written only after consent. |

## What comes later

1. Event transfer from the extension (design decided Day 3).
2. Tracking (foreground app and idle or presence), following `contracts/event-schema.md`.
3. Packaging as a normal `.exe`, and starting with Windows.
