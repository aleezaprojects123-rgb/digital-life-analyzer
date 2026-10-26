namespace Dla.Agent.Consent;

/// <summary>
/// Answers one question: has the user explicitly accepted the terms and privacy information?
///
/// Everything that watches the user (the tray, tracking, later the database and sync) must be started only
/// after this says yes. Opening the app is never consent, and consent is never assumed or on by default.
/// </summary>
public interface IConsentGate
{
    bool HasConsent { get; }
}
