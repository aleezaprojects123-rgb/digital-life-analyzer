namespace Dla.Agent;

/// <summary>
/// The agent's own "tracking enabled" value: ON by default once the user has consented, OFF while paused from the
/// tray. It lives in memory only, so it is ON again after a restart.
///
/// Today it only changes this value and the tray label. Nothing is tracked yet: the next task exposes the value
/// to the extension, and tracking itself comes on later days. It is NOT consent: consent is a separate, saved
/// decision (see ConsentStore), and pausing never grants or removes it.
/// </summary>
public sealed class AgentState
{
    public bool TrackingEnabled { get; private set; } = true;

    /// <summary>Raised after the value changes.</summary>
    public event EventHandler? Changed;

    public void SetTrackingEnabled(bool enabled)
    {
        if (TrackingEnabled == enabled) return;
        TrackingEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Flips ON/OFF and returns the new value.</summary>
    public bool Toggle()
    {
        SetTrackingEnabled(!TrackingEnabled);
        return TrackingEnabled;
    }
}
