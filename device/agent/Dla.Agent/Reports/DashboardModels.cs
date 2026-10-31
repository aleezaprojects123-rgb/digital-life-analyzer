namespace Dla.Agent.Reports;

/// <summary>One platform on the daily dashboard: all its time today (feed, search and navigation included) and its content counts.</summary>
public sealed record DashboardPlatform(string Platform, double TotalSeconds, IReadOnlyDictionary<string, int> Content);

/// <summary>
/// What the daily dashboard shows for one local calendar day. It is cut out of a one-day <see cref="ActivityReport"/>, so it uses exactly the
/// report rules (overlapping events counted once, content counted by content_count, landing pages not counted as content) and holds no event
/// data. Content keys: instagram reels; youtube shorts and videos; tiktok videos; facebook reels.
/// </summary>
public sealed record DashboardToday(DateOnly Date, double TotalSeconds, IReadOnlyList<DashboardPlatform> Platforms, double OtherPlatformsSeconds)
{
    public DashboardPlatform Platform(string name) => Platforms.First(p => string.Equals(p.Platform, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cuts the dashboard out of a one-day report. The report is the single place where time and counts are calculated.</summary>
    public static DashboardToday From(ActivityReport day)
    {
        int Count(string platform, string activityType) => day.ContentStat(platform, activityType).Count;

        var content = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["instagram"] = new Dictionary<string, int> { ["reels"] = Count("instagram", "reel") },
            ["youtube"] = new Dictionary<string, int> { ["shorts"] = Count("youtube", "short_video"), ["videos"] = Count("youtube", "video") },
            ["tiktok"] = new Dictionary<string, int> { ["videos"] = Count("tiktok", "short_video") },
            ["facebook"] = new Dictionary<string, int> { ["reels"] = Count("facebook", "reel") },
        };

        var platforms = day.Platforms.Select(p => new DashboardPlatform(p.Platform, p.Seconds, content[p.Platform])).ToList();
        return new DashboardToday(day.FirstDay, day.TotalSeconds, platforms, day.OtherPlatformsSeconds);
    }
}
