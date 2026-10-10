using Dla.Agent.Core;

namespace Dla.Agent.Consent;

/// <summary>
/// The canonical consent text and its version. The agent never shows this text: consent is given on the official DLA
/// website, which must display exactly this text and report this version. The agent only checks the version.
/// </summary>
public static class ConsentText
{
    /// <summary>
    /// Bump when the wording changes materially; existing users are then asked again.
    /// 1: first version. 2: added the sentence about the optional "read this window's text" question (OCR).
    /// 3: withdrawal moved from the tray to the website, and consent is given on the website.
    /// </summary>
    public const int Version = 3;

    public const string Title = "Digital Life Analyzer — your privacy";

    public const string Body =
@"WHAT DLA RECORDS
  • The active app on your PC
  • The window title
  • The active website in Chrome or Edge
  • How long you spend on each

WHAT DLA NEVER RECORDS
  • Keystrokes — what you type is never recorded
  • Screenshots — none are stored
  • Private or incognito browser windows

WHERE YOUR DATA GOES
  • The raw records stay on this PC only.
  • Only encrypted summaries (for example, time per category per hour) are ever uploaded.
  • Tracking and categorization work with the internet unplugged.

IF A WINDOW TITLE IS UNCLEAR
  • Later, if a window's title is unclear, DLA may ask whether it can read that window's text on this PC (the picture is never saved and is deleted within 5 seconds); you can answer Always, Just this time or Never, and change your answer later.

YOUR CONTROL
  • Pause or resume recording at any time from the tray icon.
  • Withdraw your consent at any time on the DLA website. Recording stops and DLA no longer starts at login.

DLA records nothing until you accept on the DLA website.";
}

public enum ConsentState { None, Accepted, Declined, Withdrawn }

/// <summary>Where an acceptance came from. Only an acceptance given on the official website counts.</summary>
public enum ConsentSource { Local, Website }

public sealed record ConsentRecord(
    ConsentState State,
    int TextVersion,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? DeclinedAt,
    DateTimeOffset? WithdrawnAt,
    ConsentSource Source = ConsentSource.Local)   // files written before this field existed read as Local: not valid
{
    public static ConsentRecord None { get; } = new(ConsentState.None, 0, null, null, null);

    /// <summary>True only for consent accepted on the official website, for the current text version.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValidAccepted =>
        State == ConsentState.Accepted && TextVersion == ConsentText.Version && Source == ConsentSource.Website;
}

public sealed class ConsentStore
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;

    public ConsentStore(string dataDir, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(dataDir, "consent.json");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public ConsentRecord Load() => AtomicFile.ReadJson<ConsentRecord>(_path) ?? ConsentRecord.None;

    /// <summary>
    /// Records consent that was accepted on the official website. ONLY the website activation handshake may call
    /// this, and that handshake is not built yet (it needs the backend), so nothing in the shipped agent calls it:
    /// the agent can never become active until it exists. There is deliberately no local "Accept".
    /// </summary>
    internal ConsentRecord RecordWebsiteConsent() =>
        Save(new(ConsentState.Accepted, ConsentText.Version, _now(), null, null, ConsentSource.Website));

    public ConsentRecord Decline() => Save(new(ConsentState.Declined, ConsentText.Version, null, _now(), null));

    public ConsentRecord Withdraw()
    {
        var prev = Load();
        return Save(new(ConsentState.Withdrawn, prev.TextVersion, prev.AcceptedAt, null, _now(), prev.Source));
    }

    private ConsentRecord Save(ConsentRecord r)
    {
        AtomicFile.WriteJson(_path, r);
        return r;
    }
}

/// <summary>Persisted user settings that exist in Phase 0. Moves into the SQLite settings table in Step 5.</summary>
public sealed record AgentSettings(bool Paused);

public sealed class AgentSettingsStore
{
    private readonly string _path;
    public AgentSettingsStore(string dataDir) => _path = Path.Combine(dataDir, "settings.json");
    public AgentSettings Load() => AtomicFile.ReadJson<AgentSettings>(_path) ?? new AgentSettings(false);
    public void Save(AgentSettings s) => AtomicFile.WriteJson(_path, s);
}

/// <summary>The single rule every recorder must check before capturing anything.</summary>
public static class RecordingGate
{
    public static bool CanRecord(ConsentRecord consent, AgentSettings settings) =>
        consent.IsValidAccepted && !settings.Paused;
}
