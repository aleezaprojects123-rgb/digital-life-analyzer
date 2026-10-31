using System.Globalization;
using System.Text.Json;

namespace Dla.Agent.Storage;

/// <summary>
/// One stored event as the timeline sees it. It is a read-only view: the original record stays in the database untouched, and
/// <see cref="Raw"/> is the exact JSON the store returned for it. Time is on the half-open interval [Start, End).
/// </summary>
public sealed record TimelineEntry(
    string Id, long StartMs, long EndMs, string? SessionId, string? Platform, string? ActivityType, int ContentCount,
    string? Domain, string Raw)
{
    public DateTimeOffset Start => DateTimeOffset.FromUnixTimeMilliseconds(StartMs);
    public DateTimeOffset End => DateTimeOffset.FromUnixTimeMilliseconds(EndMs);
    public double Seconds => (EndMs - StartMs) / 1000.0;
    public bool IsZeroLength => EndMs == StartMs;
}

/// <summary>
/// A stretch of time in which the set of events covering it does not change. Segments never overlap each other, so adding up
/// segments can never count a moment twice. <see cref="EventIds"/> lists every event that covers the segment (nothing is hidden);
/// <see cref="PrimaryEventId"/> is the one event that the segment's time is credited to when time is split by platform or domain.
/// </summary>
public sealed record TimelineSegment(long StartMs, long EndMs, string PrimaryEventId, IReadOnlyList<string> EventIds)
{
    public double Seconds => (EndMs - StartMs) / 1000.0;
}

/// <summary>The time of one local calendar day.</summary>
public sealed record DailyTotal(DateOnly Day, double ActiveSeconds);

/// <summary>
/// The chronological timeline of browser activity periods. A derived view: it reads events and never changes or deletes one.
///
/// THE NO-DOUBLE-COUNTING RULE (all of it, in one place)
///  1. Every event is the interval [start, end). The interval comes from the start and end times, not from adding durations.
///  2. The time axis is cut at every event start and end. Each piece (a segment) is covered by one or more events.
///  3. Time (ActiveSeconds) = the total length of the segments = the union of all events. A moment covered by ten events counts once.
///     Gaps with no event count as nothing. Nothing here looks at whether the user was touching the keyboard or mouse.
///  4. The time of a segment is credited to ONE primary event, so splitting by platform or domain also adds up to the same total.
///     Preference: the later start (the more specific, more recent one), then the earlier end, then the id.
///  5. content_count is NOT time. It is summed over the original events, once each, however much they overlap.
///  6. A zero-length event stays in the list with its content_count, and adds no time.
/// </summary>
public sealed class UnifiedTimeline
{
    /// <summary>Every event, original and identifiable, ordered by start, then end, then id.</summary>
    public IReadOnlyList<TimelineEntry> Entries { get; }

    /// <summary>Non-overlapping time pieces, in order. Time covered by no event has no segment.</summary>
    public IReadOnlyList<TimelineSegment> Segments { get; }

    /// <summary>Lines that could not be read as events when the timeline was built (never happens for lines that came from the store).</summary>
    public int Skipped { get; }

    private readonly Dictionary<string, TimelineEntry> _byId;

    private UnifiedTimeline(IReadOnlyList<TimelineEntry> entries, IReadOnlyList<TimelineSegment> segments, int skipped)
    {
        Entries = entries;
        Segments = segments;
        Skipped = skipped;
        _byId = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public TimelineEntry? Find(string id) => _byId.GetValueOrDefault(id);

    // ---- building -----------------------------------------------------------------------------------------------------------

    /// <summary>Reads events from SQLite (the source of truth). The store is only read.</summary>
    public static UnifiedTimeline FromStore(SqliteEventStore store, EventQuery? query = null) => FromJson(store.Read(query));

    public static UnifiedTimeline FromJson(IEnumerable<string> eventLines)
    {
        var entries = new List<TimelineEntry>();
        var skipped = 0;
        foreach (var line in eventLines)
        {
            if (TryParse(line, out var entry)) entries.Add(entry); else skipped++;
        }
        return Build(entries, skipped);
    }

    public static UnifiedTimeline Build(IEnumerable<TimelineEntry> events) => Build(events, 0);

    private static UnifiedTimeline Build(IEnumerable<TimelineEntry> events, int skipped)
    {
        var entries = events.OrderBy(e => e.StartMs).ThenBy(e => e.EndMs).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        return new UnifiedTimeline(entries, Sweep(entries), skipped);
    }

    private static List<TimelineSegment> Sweep(List<TimelineEntry> ordered)
    {
        var timed = ordered.Where(e => !e.IsZeroLength && e.EndMs > e.StartMs).ToList();   // already in start order
        var bounds = timed.SelectMany(e => new[] { e.StartMs, e.EndMs }).Distinct().Order().ToList();
        var segments = new List<TimelineSegment>();
        var covering = new List<TimelineEntry>();
        var next = 0;

        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            long from = bounds[i], to = bounds[i + 1];
            while (next < timed.Count && timed[next].StartMs <= from) covering.Add(timed[next++]);
            covering.RemoveAll(e => e.EndMs <= from);
            if (covering.Count == 0) continue;                       // a gap: nothing was recorded here

            var primary = covering.OrderByDescending(e => e.StartMs).ThenBy(e => e.EndMs).ThenBy(e => e.Id, StringComparer.Ordinal).First();
            segments.Add(new TimelineSegment(from, to, primary.Id, covering.OrderBy(e => e.StartMs).ThenBy(e => e.EndMs).ThenBy(e => e.Id, StringComparer.Ordinal).Select(e => e.Id).ToArray()));
        }
        return segments;
    }

    private static bool TryParse(string line, out TimelineEntry entry)
    {
        entry = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;

            string? S(string n) => r.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            var id = S("id");
            if (string.IsNullOrEmpty(id) || S("source") != "extension" || S("start") is not { } st || S("end") is not { } en) return false;
            if (!Time(st, out var start) || !Time(en, out var end) || end < start) return false;

            var count = r.TryGetProperty("content_count", out var cc) && cc.ValueKind == JsonValueKind.Number && cc.TryGetInt32(out var n) ? n : 0;
            entry = new TimelineEntry(id, start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), S("session_id"), S("platform"),
                S("activity_type"), count, S("domain"), line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Time(string text, out DateTimeOffset t) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);

    // ---- totals (time) ------------------------------------------------------------------------------------------------------

    /// <summary>Seconds with a page in front: the union of all events. Never more than the span of the events.</summary>
    public double ActiveSeconds => Segments.Sum(s => s.Seconds);

    /// <summary>Seconds split by a key of the credited event (platform, domain, activity type ...). The values add up to <see cref="ActiveSeconds"/>.</summary>
    public IReadOnlyDictionary<string, double> ActiveSecondsBy(Func<TimelineEntry, string?> key)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in Segments)
        {
            var k = key(_byId[s.PrimaryEventId]) ?? "(none)";
            totals[k] = totals.GetValueOrDefault(k) + s.Seconds;
        }
        return totals;
    }

    /// <summary>Seconds inside [from, to). Segments are cut at the edges.</summary>
    public double ActiveSecondsBetween(DateTimeOffset from, DateTimeOffset to)
    {
        long a = from.ToUnixTimeMilliseconds(), b = to.ToUnixTimeMilliseconds();
        return Segments.Sum(s => Math.Max(0, Math.Min(s.EndMs, b) - Math.Max(s.StartMs, a)) / 1000.0);
    }

    /// <summary>
    /// Seconds per local calendar day. A segment that crosses midnight is cut there, so each day gets its own part and the
    /// days add up to <see cref="ActiveSeconds"/>. The events themselves are not cut.
    /// </summary>
    public IReadOnlyList<DailyTotal> PerDay(TimeZoneInfo zone)
    {
        var days = new SortedDictionary<DateOnly, double>();
        foreach (var s in Segments)
        {
            var cur = s.StartMs;
            while (cur < s.EndMs)
            {
                var next = Math.Min(s.EndMs, NextLocalMidnightMs(cur, zone));
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(cur), zone).DateTime);
                days[day] = days.GetValueOrDefault(day) + (next - cur) / 1000.0;
                cur = next;
            }
        }
        return days.Select(d => new DailyTotal(d.Key, d.Value)).ToList();
    }

    private static long NextLocalMidnightMs(long ms, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone).DateTime;
        var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);   // a daylight-saving gap at midnight
        var next = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero).ToUnixTimeMilliseconds();
        return next > ms ? next : ms + 3_600_000;
    }

    // ---- content counts (not time) ------------------------------------------------------------------------------------------

    /// <summary>The sum of content_count over the original events. Overlap and zero length do not change it.</summary>
    public int ContentCount => Entries.Sum(e => e.ContentCount);

    public IReadOnlyDictionary<string, int> ContentCountBy(Func<TimelineEntry, string?> key) =>
        Entries.Where(e => e.ContentCount != 0).GroupBy(e => key(e) ?? "(none)").ToDictionary(g => g.Key, g => g.Sum(e => e.ContentCount), StringComparer.Ordinal);
}
