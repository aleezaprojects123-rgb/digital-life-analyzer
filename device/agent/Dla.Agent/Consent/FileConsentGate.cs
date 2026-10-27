namespace Dla.Agent.Consent;

/// <summary>
/// The real consent gate. It says yes only when the saved consent file is valid for the current text version.
/// It re-reads the file every time it is asked, so withdrawing consent (deleting the file) takes effect at once.
/// A missing file means no consent, which is the default.
/// </summary>
public sealed class FileConsentGate : IConsentGate
{
    private readonly ConsentStore _store;

    public FileConsentGate(ConsentStore store) => _store = store;

    public bool HasConsent => _store.HasValidConsent();
}
