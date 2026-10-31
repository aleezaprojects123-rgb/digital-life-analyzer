using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dla.Agent.Media;

/// <summary>
/// Attaches media metadata to a finished browser (extension) event. It never creates a separate event for media, so time
/// is not counted twice. If the media changed during the period (next song, next Short), the period is split into
/// consecutive events that together cover exactly the original start..end. If nothing matches, the event is returned unchanged.
/// </summary>
public static class MediaEnricher
{
    private const int MinPieceSeconds = 3;
    private const int MaxSeconds = 24 * 3600;

    private static readonly string[] MusicHosts =
        ["music.youtube.com", "open.spotify.com", "soundcloud.com", "deezer.com", "music.apple.com", "listen.tidal.com"];

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Piece(int From, int To, MediaState? State);

    public static IReadOnlyList<string> Enrich(string eventJson, MediaTimeline timeline, DateTimeOffset now, Action<string>? note = null)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(eventJson) as JsonObject; }
        catch (JsonException) { return [eventJson]; }
        if (root is null) return [eventJson];

        if (Str(root, "source") != "extension") return [eventJson];   // not a browser event: no note
        if (!TryTime(Str(root, "start"), out var start) || !TryTime(Str(root, "end"), out var end)) return [eventJson];
        var total = (int)(end - start).TotalSeconds;
        if (total <= 0 || total > MaxSeconds) return [eventJson];

        var url = Str(root, "url");
        var title = Str(root, "title");
        var host = HostOf(url);

        var segments = timeline.Overlapping(start, end, now).Where(s => IsBrowser(s.State.App)).ToList();
        if (segments.Count == 0) { note?.Invoke("no_browser_media_in_period"); return [eventJson]; }

        var candidates = segments.Where(s => TitleMatches(title, s.State, host)).ToList();
        if (title is null && IsVideoOrMusicHost(host))
        {
            // No usable tab title: trust the media only if nothing else was playing at the same moment (Short A, then Short B is fine).
            var playing = segments.Where(s => s.State.Playback == "playing").ToList();
            var overlap = playing.Any(a => playing.Any(b => !ReferenceEquals(a, b) && a.State != b.State && a.Start < b.End && b.Start < a.End));
            if (playing.Count > 0 && !overlap) candidates = playing;
        }
        if (candidates.Count == 0)
        {
            note?.Invoke(title is null ? "no_title_and_not_unambiguous" : "browser_media_but_title_did_not_match");
            return [eventJson];
        }

        var pieces = Merge(Partition(candidates, start, total));
        if (pieces.All(p => p.State is null)) { note?.Invoke("match_too_short"); return [eventJson]; }
        note?.Invoke(pieces.Count == 1 ? "attached" : $"attached_split_{pieces.Count}");

        var id = Str(root, "id") ?? "event";
        var result = new List<string>(pieces.Count);
        for (var i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var node = (JsonObject)JsonNode.Parse(eventJson)!;
            if (pieces.Count > 1)
            {
                var suffix = i == 0 ? "" : $"-m{i}";
                node["id"] = (id.Length + suffix.Length > 64 ? id[..(64 - suffix.Length)] : id) + suffix;
                node["start"] = Format(start.AddSeconds(p.From));
                node["end"] = Format(start.AddSeconds(p.To));
                node["duration_seconds"] = p.To - p.From;
                if (i > 0 && node["content_count"] is not null) node["content_count"] = 0;   // one content item, however many media pieces it is split into
            }
            if (p.State is { } s)
            {
                node["media_title"] = s.Title;
                node["artist"] = s.Artist;
                node["album"] = s.Album;
                node["media_type"] = MediaType(url, host, s);
                node["playback"] = s.Playback;
                node["media_app"] = s.App;
            }
            result.Add(node.ToJsonString(Compact));
        }
        return result;
    }

    /// <summary>One media state (or none) for each whole second; playing beats paused, then the most recent.</summary>
    private static List<Piece> Partition(List<MediaTimeline.Segment> candidates, DateTimeOffset start, int total)
    {
        var pieces = new List<Piece>();
        MediaState? current = null;
        var from = 0;
        for (var sec = 0; sec < total; sec++)
        {
            var mid = start.AddSeconds(sec + 0.5);
            var state = candidates
                .Where(c => c.Start <= mid && mid < c.End)
                .OrderByDescending(c => c.State.Playback == "playing")
                .ThenByDescending(c => c.Start)
                .Select(c => c.State)
                .FirstOrDefault();
            if (sec > 0 && state != current)
            {
                pieces.Add(new Piece(from, sec, current));
                from = sec;
            }
            current = state;
        }
        pieces.Add(new Piece(from, total, current));
        return pieces;
    }

    /// <summary>Pieces shorter than a few seconds are absorbed by their longest neighbour; equal neighbours join.</summary>
    private static List<Piece> Merge(List<Piece> pieces)
    {
        var list = pieces.ToList();
        while (list.Count > 1)
        {
            var shortIndex = list.FindIndex(p => p.To - p.From < MinPieceSeconds);
            if (shortIndex < 0) break;
            var s = list[shortIndex];
            var left = shortIndex > 0 ? list[shortIndex - 1] : null;
            var right = shortIndex < list.Count - 1 ? list[shortIndex + 1] : null;
            var target = left is null ? right! : right is null ? left : (left.To - left.From >= right.To - right.From ? left : right);
            var merged = new Piece(Math.Min(s.From, target.From), Math.Max(s.To, target.To), target.State);
            var targetIndex = list.IndexOf(target);
            list[targetIndex] = merged;
            list.RemoveAt(shortIndex);
        }
        for (var i = list.Count - 1; i > 0; i--)
        {
            if (list[i].State != list[i - 1].State) continue;
            list[i - 1] = new Piece(list[i - 1].From, list[i].To, list[i].State);
            list.RemoveAt(i);
        }
        return list;
    }

    private static bool TitleMatches(string? eventTitle, MediaState state, string? host)
    {
        if (eventTitle is null) return false;
        var a = Normalize(eventTitle);
        var b = Normalize(state.Title);
        if (a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a))) return true;
        return IsMusicHost(host);
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.ToLowerInvariant();
        t = Regex.Replace(t, @"\s+-\s+youtube( music)?$", "");
        t = Regex.Replace(t, @"[^\p{L}\p{N}]+", " ");
        return t.Trim();
    }

    private static string? MediaType(string? url, string? host, MediaState state)
    {
        if (url is not null && url.Contains("/shorts/", StringComparison.OrdinalIgnoreCase)) return "short";
        if (IsMusicHost(host)) return "music";
        return state.Kind;
    }

    private static bool IsBrowser(string app)
    {
        var a = app.ToLowerInvariant();
        return a.Contains("chrome") || a.Contains("msedge") || a.Contains("edge");
    }

    private static bool IsMusicHost(string? host) => host is not null && MusicHosts.Any(h => host == h || host.EndsWith("." + h));

    private static bool IsVideoOrMusicHost(string? host) =>
        host is not null && (host == "youtube.com" || host.EndsWith(".youtube.com") || host == "youtu.be" || IsMusicHost(host));

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool TryTime(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
