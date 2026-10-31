using System.Windows.Forms;

namespace Dla.Agent;

/// <summary>
/// The tray-only lifecycle: a WinForms ApplicationContext has no window, so the program lives in the system tray
/// until the user chooses Quit. Program runs it with Application.Run only after the user has consented and
/// AgentHost.TryStart() returned true. Disposing it stops the host, which removes the tray icon.
/// </summary>
public sealed class AgentApplicationContext : ApplicationContext
{
    private readonly AgentHost _host;

    public AgentApplicationContext(AgentHost host)
    {
        // A tray-only context may only exist for an agent that was already started with consent.
        if (!host.IsRunning || host.Tray is null)
            throw new InvalidOperationException("The agent has not been started. It starts only after the user has consented.");

        _host = host;
        host.Tray.QuitRequested += (_, _) => ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _host.Stop();
        base.Dispose(disposing);
    }
}
