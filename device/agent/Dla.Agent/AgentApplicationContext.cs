using System.Windows.Forms;

namespace Dla.Agent;

/// <summary>
/// The future tray-only lifecycle: a WinForms ApplicationContext has no window, so the program lives in the
/// system tray until the user chooses Quit.
///
/// INTENTIONALLY NOT USED YET. Today's Program never calls Application.Run. Later, after the user has
/// accepted on the onboarding screen and AgentHost.TryStart() returned true, the program will run
/// Application.Run(new AgentApplicationContext(host)).
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
