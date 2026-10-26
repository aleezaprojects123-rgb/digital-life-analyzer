using Dla.Agent.Consent;
using Dla.Agent.Tray;

namespace Dla.Agent;

/// <summary>
/// Starts the agent, but only if the consent gate says yes. Without consent it does nothing at all: it creates
/// no tray icon and starts nothing, because the tray is created only inside <see cref="TryStart"/>.
///
/// The future onboarding screen decides when to call TryStart (after the user explicitly accepts). Later days add
/// the consented features here, for example tracking, always inside the "consent is given" branch.
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly IConsentGate _consent;
    private readonly Func<ITray> _createTray;
    private ITray? _tray;

    /// <param name="consent">Says whether the user has explicitly accepted.</param>
    /// <param name="createTray">Creates the tray. It is called only after consent, never at startup.</param>
    public AgentHost(IConsentGate consent, Func<ITray> createTray)
    {
        _consent = consent;
        _createTray = createTray;
    }

    public bool IsRunning => _tray is not null;

    /// <summary>The tray, once started. null until the user has consented.</summary>
    public ITray? Tray => _tray;

    /// <returns>True if the agent is running. False if consent was not given, in which case nothing was created.</returns>
    public bool TryStart()
    {
        if (_tray is not null) return true;           // already running: never create a second tray
        if (!_consent.HasConsent) return false;       // no consent: do nothing

        var tray = _createTray();
        try
        {
            tray.Show();
        }
        catch
        {
            tray.Dispose();                           // do not leave a half-created icon behind
            throw;
        }
        _tray = tray;
        return true;
    }

    public void Stop()
    {
        _tray?.Dispose();
        _tray = null;
    }

    public void Dispose() => Stop();
}
