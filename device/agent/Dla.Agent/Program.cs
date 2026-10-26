using Dla.Agent;
using Dla.Agent.Consent;
using Dla.Agent.Tray;

// Development shell. It shows no window and no tray icon, collects nothing, prints one line and exits.
// The real onboarding and consent screen does not exist yet, so the default gate always says "not consented".
using var host = new AgentHost(new DefaultConsentGate(), () => new TrayController());

if (host.TryStart())
{
    // Cannot happen with the default gate. If it ever does, stop instead of running without a message loop.
    host.Stop();
    Console.WriteLine("Development shell: the consent gate unexpectedly allowed a start. Nothing was kept running.");
    return 1;
}

Console.WriteLine("Development shell: no UI and no tracking started. Waiting for the future onboarding and consent screen.");
return 0;
