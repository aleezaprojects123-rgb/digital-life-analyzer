namespace Dla.Agent.Reports;

public enum ReportKind { Weekly, Monthly, Daily }

/// <summary>Total time on one platform: everything tracked there (home, feed, search, watching), not only content.</summary>
public sealed record PlatformTotal(string Platform, double Seconds);

/// <summary>
/// One kind of content on one platform. <see cref="Count"/> is how many pieces were detected; <see cref="WatchSeconds"/> is the time spent
/// on that kind of content. It is part of the platform total, never added on top of it.
/// </summary>
public sealed record ContentStat(string Platform, string ActivityType, int Count, double WatchSeconds);

/// <summary>Tracked time of one calendar day (local time zone of the report).</summary>
public sealed record DailyReportEntry(DateOnly Date, DayOfWeek DayOfWeek, double TotalSeconds);

/// <summary>
/// Where a report period is compared with the current local date. It is worked out each time a report is requested, so it is always right
/// and needs no background job: InProgress while today is inside the period, Completed once the period's last day is over, Upcoming before it starts.
/// </summary>
public enum ReportStatus { InProgress, Completed, Upcoming }

/// <summary>
/// One bar of a monthly graph: a block of up to 7 days counted from the 1st (Week 1 = days 1-7, Week 2 = days 8-14, ...). A month has 4 or 5
/// of them; the last one is shorter when the month does not divide by 7. It is the sum of the report's daily totals, so it is never stored.
/// </summary>
public sealed record WeeklyReportEntry(int Number, DateOnly FirstDay, DateOnly LastDay, double TotalSeconds);

/// <summary>
/// The report the UI reads. It is computed from stored events on request and holds no database fields. Days run from
/// <see cref="FirstDay"/> to <see cref="LastDay"/> inclusive, one entry in <see cref="Days"/> for every one of them (days without activity are 0).
/// A weekly report's graph is <see cref="Days"/>; a monthly report's graph is <see cref="Weeks"/> (empty in a weekly report).
/// Platforms outside the four listed are in <see cref="OtherPlatformsSeconds"/>, so Platforms plus Other add up to <see cref="TotalSeconds"/>.
/// </summary>
public sealed record ActivityReport(
    ReportKind Kind,
    DateOnly FirstDay,
    DateOnly LastDay,
    string TimeZoneId,
    double TotalSeconds,
    IReadOnlyList<PlatformTotal> Platforms,
    double OtherPlatformsSeconds,
    IReadOnlyList<ContentStat> Content,
    IReadOnlyList<DailyReportEntry> Days,
    ReportStatus Status,
    IReadOnlyList<WeeklyReportEntry> Weeks)
{
    public PlatformTotal PlatformTotal(string platform) =>
        Platforms.First(p => string.Equals(p.Platform, platform, StringComparison.OrdinalIgnoreCase));

    public ContentStat ContentStat(string platform, string activityType) =>
        Content.First(c => string.Equals(c.Platform, platform, StringComparison.OrdinalIgnoreCase) && c.ActivityType == activityType);
}
