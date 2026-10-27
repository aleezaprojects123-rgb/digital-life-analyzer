namespace Dla.Agent.Consent;

/// <summary>
/// The terms and privacy text the user must accept, and its version.
///
/// Bump <see cref="Version"/> whenever the meaning of the text changes. A saved acceptance of any other version
/// is treated as NOT consented, so the user is asked again.
///
/// Keep this text true. It must match CLAUDE.md and contracts/event-schema.md, and it must say honestly which
/// features are not active yet.
/// </summary>
public static class ConsentText
{
    public const int Version = 1;

    public const string WindowTitle = "Digital Life Analyzer: privacy and consent";

    public const string Heading = "Before anything is recorded, please read this";

    public const string AgreeLabel = "I have read and agree";

    public const string Body =
@"Digital Life Analyzer is optional. Nothing is recorded until you accept below. Opening the app is not consent.

WHAT IT RECORDS
  • Websites you visit in Chrome and Edge: the site's domain (for example youtube.com), a cleaned-up web address, how long you spend there, and the page title, except on sites where titles can be private (for example webmail).
  • The cleaned-up web address keeps only the scheme, domain and path. Everything after ""?"" or ""#"" is always removed, and parts that look like random ids or tokens are replaced with *.
  • Private (incognito) windows are never recorded.

NOT ACTIVE YET
  • The names of the apps you use, and whether you are away or idle, are planned features. They do not run today. If they are added, they will be covered by this same promise, and you will be told.

TEXT ON THE PAGE (NOT ACTIVE YET)
  • Later versions may read page or document text on this PC only to work out a category. That text is never stored and never uploaded; only the category label is kept. Each time it is read, a note that it was read (time and site or app, no text) is added to your audit log.

WHERE YOUR DATA STAYS
  • Your records stay on this PC and are deleted automatically after 30 days.
  • Nothing is sent anywhere unless you turn on sync (not available yet). Even then, only encrypted daily summaries leave this PC, never your individual records, web addresses, window titles or page text.
  • Honest limit: summaries are encrypted while they travel and while they are stored on the server, but this is not end-to-end encryption. The server can read the summaries.

WHAT IT NEVER RECORDS
  • No screenshots.
  • No keystrokes and no typed text.
  • It never reads your messages, your inbox or what you type into boxes.

YOUR CONTROL
  • Tracking is optional. You can pause or stop it from the tray icon.
  • If you decline or close this window, nothing is recorded and nothing is saved.";
}
