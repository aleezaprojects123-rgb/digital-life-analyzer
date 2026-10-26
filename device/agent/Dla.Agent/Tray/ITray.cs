namespace Dla.Agent.Tray;

/// <summary>
/// What the agent needs from a tray icon. It exists so AgentHost can be tested without creating a real
/// icon, and so only AgentHost decides when a tray is created.
/// </summary>
public interface ITray : IDisposable
{
    /// <summary>Raised when the user chooses Quit in the tray menu.</summary>
    event EventHandler? QuitRequested;

    /// <summary>Makes the icon visible in the Windows system tray.</summary>
    void Show();
}
