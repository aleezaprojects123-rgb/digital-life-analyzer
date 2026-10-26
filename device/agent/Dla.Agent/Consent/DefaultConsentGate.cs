namespace Dla.Agent.Consent;

/// <summary>
/// The only gate that exists today. It ALWAYS says "not consented".
///
/// There is no onboarding or consent screen yet, and nothing is saved, so there is nothing that could ever
/// say yes. The future real consent screen will provide its own IConsentGate that returns true only after the
/// user has explicitly accepted. Until then the agent starts nothing.
/// </summary>
public sealed class DefaultConsentGate : IConsentGate
{
    public bool HasConsent => false;
}
