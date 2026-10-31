using System.Globalization;
using System.Text.Json;

namespace Dla.Agent.Storage;

/// <summary>One finished activity period, as stored. The column set is the normalized event, nothing else.</summary>
internal sealed record EventRow(
    string Id, string SchemaVersion, long StartMs, long EndMs, string StartText, string EndText, double DurationSeconds,
    string Source, string? SessionId, string? Platform, string? ActivityType, int ContentCount,
    string? Domain, string? Url, string? Title,
    string? Category, double? Confidence, bool Uncertain, string? LabelSource,
    string? ExtraJson);

/// <summary>
/// Turns one event line into a row. Only the normalized browser event (id/start/end/...) is understood. A line of any other shape, such as
/// a line the old desktop tracker wrote, is rejected, and a field the model no longer has (app, presence, idle, ...) is ignored, never stored.
/// Only known fields are kept. The URL is cut at '?' and '#' again here (defence in depth); nothing else about a page is read.
/// </summary>
internal static class EventMapper
{
    /// <summary>The agent's own media fields (added by the media matching). Nothing outside this list is ever stored.</summary>
    internal static readonly string[] MediaFields = ["media_title", "artist", "album", "media_type", "playback", "media_app"];

    private const int MaxText = 2048;

    public static bool TryParse(string json, out EventRow row, out string error)
    {
        row = null!;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Fail("not_an_object", out error);

            if (!Str(r, "start", out var startText) || startText is null) return Fail("missing_start", out error);
            if (!TryTime(startText, out var start)) return Fail("bad_start", out error);
            if (!Num(r, "duration_seconds", out var duration) || duration is null || duration < 0) return Fail("bad_duration", out error);

            if (!Str(r, "end", out var endText) || endText is null || !TryTime(endText, out var end)) return Fail("bad_end", out error);
            if (end < start) return Fail("end_before_start", out error);

            if (!Str(r, "source", out var source) || source is not "extension") return Fail("bad_source", out error);

            if (!Str(r, "id", out var id) || string.IsNullOrWhiteSpace(id) || id.Length > 64) return Fail("bad_id", out error);

            if (!Bool(r, "uncertain", required: false, out var uncertain)) return Fail("bad_uncertain", out error);

            foreach (var k in new[] { "session_id", "platform", "activity_type", "domain", "url", "title", "category", "label_source", "schema_version" })
                if (r.TryGetProperty(k, out var p) && p.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return Fail("bad_" + k, out error);

            var contentCount = 0;
            if (r.TryGetProperty("content_count", out var cc))
            {
                if (cc.ValueKind != JsonValueKind.Number || !cc.TryGetInt32(out contentCount) || contentCount < 0) return Fail("bad_content_count", out error);
            }

            double? confidence = null;
            if (r.TryGetProperty("confidence", out var conf) && conf.ValueKind != JsonValueKind.Null)
            {
                if (conf.ValueKind != JsonValueKind.Number) return Fail("bad_confidence", out error);
                confidence = conf.GetDouble();
            }

            string? extra = null;
            var media = new Dictionary<string, string>();
            foreach (var k in MediaFields)
                if (r.TryGetProperty(k, out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { } s) media[k] = Cap(s);
            if (media.Count > 0) extra = JsonSerializer.Serialize(media, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            var url = Get(r, "url");
            if (url is not null) url = CutQuery(url);

            row = new EventRow(
                id, Get(r, "schema_version") ?? "1", start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), startText, endText, duration.Value,
                source, Get(r, "session_id") is { } sid ? Cap(sid, 64) : null, Get(r, "platform"), Get(r, "activity_type"), contentCount,
                Get(r, "domain"), url is null ? null : Cap(url), Get(r, "title") is { } t ? Cap(t) : null,
                Get(r, "category"), confidence, uncertain, Get(r, "label_source"), extra);
            return true;
        }
        catch (JsonException)
        {
            error = "malformed_json";
            return false;
        }
    }

    private static bool Fail(string why, out string error) { error = why; return false; }

    private static string? Get(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Str(JsonElement r, string name, out string? value)
    {
        value = null;
        if (!r.TryGetProperty(name, out var p)) return true;
        if (p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString();
        return true;
    }

    private static bool Num(JsonElement r, string name, out double? value)
    {
        value = null;
        if (!r.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return false;
        value = p.GetDouble();
        return true;
    }

    private static bool Bool(JsonElement r, string name, bool required, out bool value)
    {
        value = false;
        if (!r.TryGetProperty(name, out var p)) return !required;
        if (p.ValueKind is JsonValueKind.True) { value = true; return true; }
        return p.ValueKind == JsonValueKind.False;
    }

    private static bool TryTime(string text, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

    private static string CutQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    private static string Cap(string s, int max = MaxText) => s.Length <= max ? s : s[..max];
}
