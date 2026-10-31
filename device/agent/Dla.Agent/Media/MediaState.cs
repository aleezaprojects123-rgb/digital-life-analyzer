namespace Dla.Agent.Media;

/// <summary>
/// What the Windows media controls (System Media Transport Controls) say is playing in one app, right now.
/// Only these text fields. No thumbnail, no page content. Playback is "playing" or "paused".
/// </summary>
public sealed record MediaState(string App, string? Title, string? Artist, string? Album, string? Kind, string Playback)
{
    public const int MaxText = 200;
    public const int MaxApp = 260;

    /// <summary>Control characters removed, spaces collapsed, cut to the contract's length. Empty becomes null.</summary>
    public static string? Clean(string? text, int max = MaxText)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var cleaned = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0) return null;
        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }

    public MediaState Normalized() => this with
    {
        App = Clean(App, MaxApp) ?? "unknown",
        Title = Clean(Title),
        Artist = Clean(Artist),
        Album = Clean(Album),
        Kind = Kind is "music" or "video" ? Kind : null,
    };

    public bool HasContent => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);
}
