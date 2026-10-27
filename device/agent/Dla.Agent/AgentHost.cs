using Dla.Agent.Consent;
using Dla.Agent.Link;
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
    private readonly Func<IAgentLink>? _createLink;
    private ITray? _tray;
    private IAgentLink? _link;

    /// <param name="consent">Says whether the user has explicitly accepted.</param>
    /// <param name="createTray">Creates the tray. It is called only after consent, never at startup.</param>
    /// <param name="createLink">Creates the local status link for the extension. Called only after consent, never at startup. Optional.</param>
    public AgentHost(IConsentGate consent, Func<ITray> createTray, Func<IAgentLink>? createLink = null)
    {
        _consent = consent;
        _createTray = createTray;
        _createLink = createLink;
    }

    public bool IsRunning => _tray is not null;

    /// <summary>The tray, once started. null until the user has consented.</summary>
    public ITray? Tray => _tray;

    /// <summary>The status link, once started. null until the user has consented (or when no link was supplied).</summary>
    public IAgentLink? Link => _link;

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
        StartLink();
        return true;
    }

    /// <summary>The link is optional: if it cannot start for any reason, the tray and the agent carry on.</summary>
    private void StartLink()
    {
        if (_createLink is null) return;
        IAgentLink? link = null;
        try
        {
            link = _createLink();
            link.Start();
            _link = link;
        }
        catch (Exception)
        {
            link?.Dispose();
            _link = null;
        }
    }

    public void Stop()
    {
        _link?.Dispose();
        _link = null;
        _tray?.Dispose();
        _tray = null;
    }

    public void Dispose() => Stop();
}
