using Dla.Agent.Storage;

namespace Dla.Agent.Reports;

/// <summary>
/// Builds weekly and monthly reports from stored browser events. Nothing is stored: every number is calculated from the events, and the
/// events are only read.
///
/// THE RULES
///  1. Time comes from <see cref="UnifiedTimeline"/> (the union of events), cut at the period edges, so overlapping events never count a
///     moment twice and an event that crosses midnight or a month end is split between the two days or periods.
///  2. Platform total = all time credited to that platform (home, feed, search, landing pages, watching). Content watching time = the part
///     of it spent on an actual piece of content: an event of the content activity type that was counted as new content, or whose stored
///     address points at one item (/reel/*, /shorts/*, /watch, /@user/video/*). A landing page such as /reel or /reels has the same
///     activity type but no item, so it stays in the platform total only. Content time is inside the platform total, never added to it.
///  3. Content count = the sum of content_count of the events that START inside the period. A continuation of the same content has
///     content_count 0, so it adds time but no count.
///  4. Platforms outside the four supported ones are counted in the total and in OtherPlatformsSeconds, so the parts add up.
/// </summary>
public sealed class ReportGenerator
{
    public static readonly IReadOnlyList<string> Platforms = ["instagram", "youtube", "tiktok", "facebook"];

    /// <summary>The content kinds that are reported: (platform, activity_type). Each is counted by content_count and timed by activity_type.</summary>
    public static readonly IReadOnlyList<(string Platform, string ActivityType)> ContentKinds =
    [
        ("instagram", "reel"),
        ("youtube", "short_video"),
        ("youtube", "video"),
        ("tiktok", "short_video"),
        ("facebook", "reel"),
    ];

    private readonly SqliteEventStore? _store;
    private readonly TimeZoneInfo _zone;
    private readonly Func<DateTimeOffset> _clock;

    public ReportGenerator(SqliteEventStore? store = null, TimeZoneInfo? zone = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _zone = zone ?? TimeZoneInfo.Local;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The current local date in the report's time zone. This is the only thing that makes a report "current".</summary>
    public DateOnly Today => LocalDate(_clock(), _zone);

    public ActivityReport GetWeeklyReport(DateOnly startDate) => Generate(ReportPeriod.Week(startDate, _zone));

    public ActivityReport GetMonthlyReport(int year, int month) => Generate(ReportPeriod.Month(year, month, _zone));

    /// <summary>Today (local date) as a one-day report: the source of the daily dashboard.</summary>
    public ActivityReport GetTodayReport() => Generate(ReportPeriod.Day(Today, _zone));

    /// <summary>The Monday-to-Sunday week that contains today. Its status is InProgress.</summary>
    public ActivityReport GetCurrentWeeklyReport() => Generate(ReportPeriod.WeekContaining(Today, _zone));

    /// <summary>The Monday-to-Sunday week before the current one. Its status is Completed.</summary>
    public ActivityReport GetPreviousWeeklyReport() => Generate(ReportPeriod.WeekContaining(Today.AddDays(-7), _zone));

    /// <summary>The calendar month that contains today. Its status is InProgress.</summary>
    public ActivityReport GetCurrentMonthlyReport() => GetMonthlyReport(Today.Year, Today.Month);

    /// <summary>The calendar month before the current one (December of last year in January). Its status is Completed.</summary>
    public ActivityReport GetPreviousMonthlyReport()
    {
        var previous = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        return GetMonthlyReport(previous.Year, previous.Month);
    }

    public ActivityReport Generate(ReportPeriod period)
    {
        if (_store is null) throw new InvalidOperationException("No event store was given; use Build with event lines instead.");
        // From is one millisecond early so an event of zero length at the very start of the period is still read.
        return Build(period, _store.Read(new EventQuery(From: period.Start.AddMilliseconds(-1), To: period.End)), Today);
    }

    private static DateOnly LocalDate(DateTimeOffset moment, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);

    /// <summary>
    /// The calculation itself, from event lines in the stored shape. Pure: the same events, period and local date always give the same report.
    /// <paramref name="today"/> only sets <see cref="ActivityReport.Status"/>; when it is left out, the PC's current date in the period's time zone is used.
    /// </summary>
    public static ActivityReport Build(ReportPeriod period, IEnumerable<string> eventLines, DateOnly? today = null)
    {
        var timeline = UnifiedTimeline.FromJson(eventLines);
        long from = period.Start.ToUnixTimeMilliseconds(), to = period.End.ToUnixTimeMilliseconds();

        var platformSeconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var contentSeconds = new Dictionary<(string, string), double>();
        double total = 0;

        foreach (var segment in timeline.Segments)
        {
            var seconds = Overlap(segment.StartMs, segment.EndMs, from, to) / 1000.0;
            if (seconds <= 0) continue;
            var primary = timeline.Find(segment.PrimaryEventId)!;
            var platform = (primary.Platform ?? "").ToLowerInvariant();
            total += seconds;
            platformSeconds[platform] = platformSeconds.GetValueOrDefault(platform) + seconds;
            if (!IsActualContent(primary)) continue;
            var key = (platform, primary.ActivityType ?? "");
            contentSeconds[key] = contentSeconds.GetValueOrDefault(key) + seconds;
        }

        var platforms = Platforms.Select(p => new PlatformTotal(p, platformSeconds.GetValueOrDefault(p))).ToList();
        var content = ContentKinds.Select(k => new ContentStat(
            k.Platform, k.ActivityType,
            timeline.Entries.Where(e => e.StartMs >= from && e.StartMs < to && Is(e, k)).Sum(e => Math.Max(0, e.ContentCount)),
            contentSeconds.GetValueOrDefault(k))).ToList();

        var days = period.Days.Select(day =>
        {
            long a = period.MidnightOf(day).ToUnixTimeMilliseconds(), b = period.MidnightOf(day.AddDays(1)).ToUnixTimeMilliseconds();
            var seconds = timeline.Segments.Sum(s => Overlap(s.StartMs, s.EndMs, a, b)) / 1000.0;
            return new DailyReportEntry(day, day.DayOfWeek, seconds);
        }).ToList();

        // The monthly graph: blocks of 7 days from the 1st, each the sum of the daily totals above (one calculation, no second pass over events).
        var weeks = period.Kind != ReportKind.Monthly ? new List<WeeklyReportEntry>() : Enumerable.Range(0, (period.DayCount + 6) / 7).Select(i =>
        {
            var block = days.Skip(i * 7).Take(7).ToList();
            return new WeeklyReportEntry(i + 1, block[0].Date, block[^1].Date, block.Sum(d => d.TotalSeconds));
        }).ToList();

        return new ActivityReport(period.Kind, period.FirstDay, period.LastDay, period.Zone.Id, total, platforms,
            total - platforms.Sum(p => p.Seconds), content, days, period.StatusOn(today ?? LocalDate(DateTimeOffset.UtcNow, period.Zone)), weeks);
    }

    /// <summary>
    /// True when the event is an actual piece of content, not a landing or navigation page that shares the activity type. A counted event
    /// (content_count above 0) always is. A continuation of the same content has count 0, so it is recognised by its stored address:
    /// the path holds an item after /reel, /reels, /shorts, /video, /live or /embed, or it is the YouTube /watch page (the id is in the
    /// query string, which is never stored), or a youtu.be link. The stored address is sanitized, so ids appear as *.
    /// </summary>
    internal static bool IsActualContent(TimelineEntry e)
    {
        if (e.ContentCount > 0) return true;
        if (!TryReadAddress(e.Raw, out var host, out var segments)) return false;
        if (host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)) return segments.Length >= 1;
        if (segments.Length >= 1 && segments[0] == "watch") return host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < segments.Length; i++)
            if (segments[i] is "reel" or "reels" or "shorts" or "video" or "live" or "embed") return true;
        return false;
    }

    private static bool TryReadAddress(string rawEventJson, out string host, out string[] segments)
    {
        host = ""; segments = [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawEventJson);
            if (!doc.RootElement.TryGetProperty("url", out var u) || u.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            if (!Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)) return false;
            host = uri.Host;
            segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool Is(TimelineEntry e, (string Platform, string ActivityType) kind) =>
        string.Equals(e.Platform, kind.Platform, StringComparison.OrdinalIgnoreCase) && e.ActivityType == kind.ActivityType;

    private static long Overlap(long aStart, long aEnd, long bStart, long bEnd) => Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));
}
