using Dla.Agent.Consent;

namespace Dla.Agent;

public enum OnboardingOutcome
{
    /// <summary>A valid consent was already saved. The consent window was not shown.</summary>
    ConsentOnRecord,

    /// <summary>The user pressed Accept and the choice was saved.</summary>
    Accepted,

    /// <summary>The user pressed Decline or closed the window. Nothing was saved.</summary>
    Declined,

    /// <summary>The user pressed Accept but saving failed. Treated as NOT consented.</summary>
    SaveFailed,
}

/// <summary>
/// What happens when the program starts, before anything can be recorded:
///   valid consent already saved  -> carry on (no window),
///   otherwise                    -> show the consent window,
///   Accept                       -> save the consent,
///   Decline or close             -> write nothing, no log line, no file, no folder.
/// Opening the app is never consent. Consent exists only after an explicit Accept.
/// </summary>
public sealed class OnboardingFlow
{
    private readonly IConsentGate _gate;
    private readonly ConsentStore _store;
    private readonly Func<ConsentDecision> _askUser;
    private readonly IAgentLog _log;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <param name="askUser">Shows the consent window and returns the choice. Called only when there is no valid consent.</param>
    public OnboardingFlow(IConsentGate gate, ConsentStore store, Func<ConsentDecision> askUser, IAgentLog log, Func<DateTimeOffset> utcNow)
    {
        _gate = gate;
        _store = store;
        _askUser = askUser;
        _log = log;
        _utcNow = utcNow;
    }

    public OnboardingOutcome Run()
    {
        if (_gate.HasConsent)
        {
            _log.Write($"Consent version {ConsentText.Version} is on record. The tray is not built yet in this version, so the program exits.");
            return OnboardingOutcome.ConsentOnRecord;
        }

        if (_askUser() != ConsentDecision.Accepted)
            return OnboardingOutcome.Declined;   // nothing is written anywhere

        try
        {
            _store.SaveAcceptance(_utcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OnboardingOutcome.SaveFailed;
        }

        _log.Write($"Consent version {ConsentText.Version} accepted. The tray is not built yet in this version, so the program exits.");
        return OnboardingOutcome.Accepted;
    }
}
