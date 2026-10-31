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
    // 2: app names are now active (they were "not active yet" in version 1), media titles are described, and starting with Windows is disclosed.
    // Idle tracking and the desktop program tracker were removed afterwards and their sentences deleted. The version stays 2 on purpose:
    // the text now promises LESS than before, so a consent given to version 2 still covers everything the program does.
    public const int Version = 2;

    public const string WindowTitle = "Digital Life Analyzer: privacy and consent";

    public const string Heading = "Before anything is recorded, please read this";

    public const string AgreeLabel = "I have read and agree";

    public const string Body =
@"Digital Life Analyzer is optional. Nothing is recorded until you accept below. Opening the app is not consent.

WHAT IT RECORDS
  • Websites you visit in Chrome and Edge: the site's domain (for example youtube.com), a cleaned-up web address, how long you spend there, and the page title, except on sites where titles can be private (for example webmail).
  • The cleaned-up web address keeps only the scheme, domain and path. Everything after ""?"" or ""#"" is always removed, and parts that look like random ids or tokens are replaced with *.
  • Private (incognito) windows are never recorded.
  • Nothing about the other programs on this PC is recorded: only websites in Chrome and Edge.
  • While something is playing in Chrome or Edge, the title, artist and album that Windows media controls show may be saved with the matching website visit.

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
  • After you accept, Digital Life Analyzer can start by itself when you sign in to Windows and runs in the tray (the notification area). Quit stops it until your next sign-in. You can turn this off in Windows Settings > Apps > Startup. If you do not accept, or this consent is no longer on record, it does not start with Windows and nothing is recorded.
  • Tracking is optional. You can pause or stop it from the tray icon.
  • If you decline or close this window, nothing is recorded and nothing is saved.";
}
