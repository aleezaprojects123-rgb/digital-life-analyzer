using Dla.Agent.Reports;
using Dla.Agent.Storage;
using static Dla.Agent.Tests.ReportTests;

namespace Dla.Agent.Tests;

/// <summary>
/// The data behind the weekly graph (7 daily totals) and the monthly graph (4 or 5 weekly totals), and the automatic InProgress / Completed
/// status. 2026-10-26 is a Monday. All times are fixed; "now" is always passed in, never read from the PC.
/// </summary>
public class ReportGraphDataTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private static ActivityReport Week(DateOnly today, params string[] events) => ReportGenerator.Build(ReportPeriod.Week(Monday, Utc), events, today);
    private static ActivityReport Month(int year, int month, DateOnly today, params string[] events) => ReportGenerator.Build(ReportPeriod.Month(year, month, Utc), events, today);

    /// <summary>A real SQLite store in a throwaway folder, with a clock the test controls.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempData _dir = new();
        public DateTimeOffset Now;
        public SqliteEventStore Store { get; }
        public Fixture(DateTimeOffset now)
        {
            Now = now;
            Directory.CreateDirectory(_dir.DataDir);
            Store = SqliteEventStore.Open(Path.Combine(_dir.DataDir, "dla.db"), () => Now);
        }
        public ReportGenerator Generator(TimeZoneInfo? zone = null) => new(Store, zone ?? Utc, () => Now);
        public void Dispose() { Store.Dispose(); _dir.Dispose(); }
    }

    // ---- weekly graph data -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_weekly_report_has_one_entry_for_each_of_the_seven_days_Monday_to_Sunday()
    {
        var r = Week(new DateOnly(2026, 10, 28));
        Assert.Equal(7, r.Days.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => Monday.AddDays(i)).ToArray(), r.Days.Select(d => d.Date).ToArray());
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            r.Days.Select(d => d.DayOfWeek).ToArray());
        Assert.Empty(r.Weeks);                                     // the weekly graph is the daily list; there is no second list to read
    }

    [Fact]
    public void Daily_totals_come_from_the_events_and_days_without_events_are_zero()
    {
        var r = Week(new DateOnly(2026, 11, 1),
            Ev("mon", T(2026, 10, 26, 9), 7200), Ev("wed1", T(2026, 10, 28, 9), 3000), Ev("wed2", T(2026, 10, 28, 15), 600),
            Ev("fri", T(2026, 10, 30, 22), 5400), Ev("sun", T(2026, 11, 1, 8), 4500));
        Assert.Equal([7200.0, 0, 3600, 0, 5400, 0, 4500], r.Days.Select(d => d.TotalSeconds).ToArray());
        Assert.Equal(20700, r.TotalSeconds);
        Assert.Equal(r.TotalSeconds, r.Days.Sum(d => d.TotalSeconds));
    }

    [Fact]
    public void An_empty_week_has_seven_zero_days_and_an_empty_month_has_zero_weeks()
    {
        var w = Week(new DateOnly(2026, 10, 28));
        Assert.All(w.Days, d => Assert.Equal(0, d.TotalSeconds));
        Assert.Equal(0, w.TotalSeconds);
        var m = Month(2026, 11, new DateOnly(2026, 11, 10));
        Assert.Equal(5, m.Weeks.Count);
        Assert.All(m.Weeks, x => Assert.Equal(0, x.TotalSeconds));
        Assert.All(m.Days, d => Assert.Equal(0, d.TotalSeconds));
        Assert.Equal(0, m.TotalSeconds);
    }

    [Fact]
    public void An_event_across_midnight_is_split_between_the_two_daily_entries()
    {
        var r = Week(new DateOnly(2026, 11, 2), Ev("night", T(2026, 10, 27, 23, 58), 300));   // Tuesday 23:58 to Wednesday 00:03
        Assert.Equal((120.0, 180.0), (r.Days[1].TotalSeconds, r.Days[2].TotalSeconds));
        Assert.Equal(300, r.TotalSeconds);
    }

    // ---- monthly graph data ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_30_day_month_has_five_weekly_buckets_the_last_one_with_two_days()
    {
        var r = Month(2026, 11, new DateOnly(2026, 12, 5),
            Ev("w1", T(2026, 11, 1, 9), 100), Ev("w1b", T(2026, 11, 7, 9), 50), Ev("w2", T(2026, 11, 8, 9), 200), Ev("w3", T(2026, 11, 21, 9), 300),
            Ev("w4", T(2026, 11, 22, 9), 400), Ev("w4b", T(2026, 11, 28, 9), 40), Ev("w5", T(2026, 11, 29, 9), 500), Ev("w5b", T(2026, 11, 30, 9), 5));
        Assert.Equal([1, 2, 3, 4, 5], r.Weeks.Select(w => w.Number).ToArray());
        Assert.Equal([150.0, 200, 300, 440, 505], r.Weeks.Select(w => w.TotalSeconds).ToArray());
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7)), (r.Weeks[0].FirstDay, r.Weeks[0].LastDay));
        Assert.Equal((new DateOnly(2026, 11, 29), new DateOnly(2026, 11, 30)), (r.Weeks[4].FirstDay, r.Weeks[4].LastDay));
        Assert.Equal(r.TotalSeconds, r.Weeks.Sum(w => w.TotalSeconds));
        Assert.Equal(30, r.Days.Count);                              // the daily list is still there too
    }

    [Theory]
    [InlineData(2026, 1, 5, 3)]      // 31 days: weeks of 7, 7, 7, 7 and 3
    [InlineData(2026, 4, 5, 2)]      // 30 days
    [InlineData(2027, 2, 4, 7)]      // 28 days: exactly four full weeks
    [InlineData(2028, 2, 5, 1)]      // leap February, 29 days: the fifth bucket is a single day
    public void The_number_of_weekly_buckets_and_the_length_of_the_last_one_follow_the_length_of_the_month(int year, int month, int buckets, int lastDays)
    {
        var r = Month(year, month, new DateOnly(2030, 1, 1));
        Assert.Equal(buckets, r.Weeks.Count);
        var last = r.Weeks[^1];
        Assert.Equal(lastDays, last.LastDay.DayNumber - last.FirstDay.DayNumber + 1);
        Assert.Equal(r.LastDay, last.LastDay);
        for (var i = 1; i < r.Weeks.Count; i++) Assert.Equal(r.Weeks[i - 1].LastDay.AddDays(1), r.Weeks[i].FirstDay);   // no gap, no overlap
    }

    [Fact]
    public void An_event_across_the_boundary_between_two_monthly_buckets_is_split()
    {
        var r = Month(2026, 11, new DateOnly(2026, 11, 20), Ev("x", T(2026, 11, 7, 23, 59), 120));   // 7 Nov 23:59 to 8 Nov 00:01
        Assert.Equal((60.0, 60.0), (r.Weeks[0].TotalSeconds, r.Weeks[1].TotalSeconds));
        Assert.Equal(120, r.TotalSeconds);
    }

    [Fact]
    public void An_event_across_a_month_end_is_split_between_the_last_bucket_of_one_month_and_the_first_of_the_next()
    {
        var crossing = Ev("x", T(2026, 10, 31, 23, 55), 600);          // 5 minutes in October, 5 in November
        var oct = Month(2026, 10, new DateOnly(2026, 12, 1), crossing);
        var nov = Month(2026, 11, new DateOnly(2026, 12, 1), crossing);
        Assert.Equal(300, oct.Weeks[^1].TotalSeconds);                 // 29-31 October
        Assert.Equal(300, nov.Weeks[0].TotalSeconds);
        Assert.Equal(300, oct.TotalSeconds);
        Assert.Equal(300, nov.TotalSeconds);
    }

    [Fact]
    public void Overlapping_events_are_counted_once_inside_a_bucket_and_in_the_report()
    {
        var r = Month(2026, 11, new DateOnly(2026, 11, 20), Ev("a", T(2026, 11, 3, 10, 0), 600), Ev("b", T(2026, 11, 3, 10, 5), 600));   // 10:00-10:10 and 10:05-10:15
        Assert.Equal(900, r.Weeks[0].TotalSeconds);
        Assert.Equal(900, r.Days[2].TotalSeconds);
        Assert.Equal(900, r.TotalSeconds);
    }

    // ---- status: InProgress / Completed (worked out on request, no job) --------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-26", ReportStatus.InProgress)]    // the first day
    [InlineData("2026-10-29", ReportStatus.InProgress)]
    [InlineData("2026-11-01", ReportStatus.InProgress)]    // the last day is still in progress
    [InlineData("2026-11-02", ReportStatus.Completed)]     // the next Monday: the week is over
    [InlineData("2027-03-01", ReportStatus.Completed)]
    [InlineData("2026-10-25", ReportStatus.Upcoming)]      // a week that has not started
    public void A_week_is_InProgress_while_today_is_inside_it_and_Completed_after_it(string today, ReportStatus expected)
    {
        Assert.Equal(expected, Week(DateOnly.Parse(today)).Status);
    }

    [Theory]
    [InlineData("2026-11-01", ReportStatus.InProgress)]
    [InlineData("2026-11-30", ReportStatus.InProgress)]
    [InlineData("2026-12-01", ReportStatus.Completed)]
    [InlineData("2026-10-31", ReportStatus.Upcoming)]
    public void A_month_is_InProgress_while_today_is_inside_it_and_Completed_after_it(string today, ReportStatus expected)
    {
        Assert.Equal(expected, Month(2026, 11, DateOnly.Parse(today)).Status);
    }

    [Fact]
    public void The_same_period_changes_from_InProgress_to_Completed_by_itself_when_the_date_moves_on()
    {
        using var f = new Fixture(T(2026, 11, 1, 23, 59, 59));
        var g = f.Generator();
        Assert.Equal(ReportStatus.InProgress, g.GetWeeklyReport(Monday).Status);
        f.Now = T(2026, 11, 2, 0, 0, 0);
        Assert.Equal(ReportStatus.Completed, g.GetWeeklyReport(Monday).Status);
        Assert.Equal(ReportStatus.InProgress, g.GetMonthlyReport(2026, 11).Status);
        f.Now = T(2026, 12, 1, 0, 0, 0);
        Assert.Equal(ReportStatus.Completed, g.GetMonthlyReport(2026, 11).Status);
    }

    // ---- current and previous periods -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Current_and_previous_week_are_found_from_the_clock_and_switch_at_local_midnight()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        var g = f.Generator();
        var current = g.GetCurrentWeeklyReport();
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), ReportStatus.InProgress), (current.FirstDay, current.LastDay, current.Status));
        var previous = g.GetPreviousWeeklyReport();
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));

        f.Now = T(2026, 11, 1, 23, 59, 59);                            // the last second of the week
        Assert.Equal(new DateOnly(2026, 10, 26), g.GetCurrentWeeklyReport().FirstDay);

        f.Now = T(2026, 11, 2, 0, 0, 0);                               // a new week starts: nothing was run, nothing was stored
        current = g.GetCurrentWeeklyReport();
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), ReportStatus.InProgress), (current.FirstDay, current.LastDay, current.Status));
        previous = g.GetPreviousWeeklyReport();
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));
    }

    [Fact]
    public void Current_and_previous_month_are_found_from_the_clock_including_across_a_year_end()
    {
        using var f = new Fixture(T(2026, 11, 15, 12));
        var g = f.Generator();
        var current = g.GetCurrentMonthlyReport();
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), ReportStatus.InProgress), (current.FirstDay, current.LastDay, current.Status));
        var previous = g.GetPreviousMonthlyReport();
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));

        f.Now = T(2027, 1, 15, 12);
        previous = g.GetPreviousMonthlyReport();
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));
        Assert.Equal(new DateOnly(2027, 1, 1), g.GetCurrentMonthlyReport().FirstDay);
    }

    [Fact]
    public void New_events_in_the_store_change_the_next_report_with_nothing_regenerated_by_hand()
    {
        using var f = new Fixture(T(2026, 10, 28, 18));
        var g = f.Generator();
        f.Store.Append([Ev("a", T(2026, 10, 27, 9), 600, "youtube", "webpage")]);
        var before = g.GetCurrentWeeklyReport();
        Assert.Equal(600, before.TotalSeconds);

        f.Store.Append([Ev("b", T(2026, 10, 28, 9), 900, "youtube", "video", 1)]);
        var after = g.GetCurrentWeeklyReport();
        Assert.Equal(1500, after.TotalSeconds);
        Assert.Equal(900, after.Days[2].TotalSeconds);
        Assert.Equal(1500, after.PlatformTotal("youtube").Seconds);
        Assert.Equal((1, 900.0), (after.ContentStat("youtube", "video").Count, after.ContentStat("youtube", "video").WatchSeconds));
        Assert.Equal(1500, g.GetCurrentMonthlyReport().TotalSeconds);   // the monthly view of the same events agrees
    }

    // ---- time zones ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_current_period_follows_the_local_date_not_the_UTC_date()
    {
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("test+5", TimeSpan.FromHours(5), "test+5", "test+5");
        using var f = new Fixture(T(2026, 11, 1, 20));                   // Sunday 20:00 UTC = Monday 01:00 at +05:00
        Assert.Equal(new DateOnly(2026, 10, 26), f.Generator(Utc).GetCurrentWeeklyReport().FirstDay);
        var local = f.Generator(plus5);
        Assert.Equal(new DateOnly(2026, 11, 2), local.Today);
        Assert.Equal(new DateOnly(2026, 11, 2), local.GetCurrentWeeklyReport().FirstDay);
        Assert.Equal(ReportStatus.Completed, local.GetWeeklyReport(Monday).Status);
        Assert.Equal(ReportStatus.InProgress, f.Generator(Utc).GetWeeklyReport(Monday).Status);
    }

    [Fact]
    public void Daily_and_weekly_buckets_are_cut_at_local_midnight_in_a_non_UTC_zone()
    {
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("test+5", TimeSpan.FromHours(5), "test+5", "test+5");
        // 2026-11-07 18:59:00Z to 19:01:00Z crosses local midnight (7 Nov 23:59 to 8 Nov 00:01 at +05:00): the end of bucket 1 and the start of bucket 2.
        var r = ReportGenerator.Build(ReportPeriod.Month(2026, 11, plus5), [Ev("x", T(2026, 11, 7, 18, 59), 120)], new DateOnly(2026, 11, 20));
        Assert.Equal((60.0, 60.0), (r.Weeks[0].TotalSeconds, r.Weeks[1].TotalSeconds));
        Assert.Equal((60.0, 60.0), (r.Days[6].TotalSeconds, r.Days[7].TotalSeconds));
    }

    // ---- the existing rules still hold inside the graph data --------------------------------------------------------------------------------

    [Fact]
    public void Platform_content_and_continuation_rules_hold_in_a_monthly_report_and_content_time_is_not_added_to_the_total()
    {
        var r = Month(2026, 11, new DateOnly(2026, 12, 1),
            Ev("home", T(2026, 11, 3, 9, 0), 600, "youtube", "webpage"),
            Ev("search", T(2026, 11, 3, 9, 20), 300, "youtube", "search"),
            Ev("short", T(2026, 11, 3, 9, 40), 1200, "youtube", "short_video", 1),
            Ev("video", T(2026, 11, 3, 10, 10), 900, "youtube", "video", 1),
            Ev("video-more", T(2026, 11, 3, 10, 40), 100, "youtube", "video", 0),
            Ev("landing", T(2026, 11, 4, 9, 0), 40, "facebook", "reel", url: "https://www.facebook.com/reel"),
            Ev("fbreel", T(2026, 11, 4, 9, 5), 18, "facebook", "reel", 1),
            Ev("elsewhere", T(2026, 11, 4, 10, 0), 70));
        Assert.Equal(3100, r.PlatformTotal("youtube").Seconds);                             // 600 + 300 + 1200 + 900 + 100
        Assert.Equal(1200, r.ContentStat("youtube", "short_video").WatchSeconds);
        Assert.Equal((1, 1000.0), (r.ContentStat("youtube", "video").Count, r.ContentStat("youtube", "video").WatchSeconds));   // the continuation: time, no count
        Assert.Equal(58, r.PlatformTotal("facebook").Seconds);
        Assert.Equal((1, 18.0), (r.ContentStat("facebook", "reel").Count, r.ContentStat("facebook", "reel").WatchSeconds));   // landing time is not reel time
        Assert.Equal(70, r.OtherPlatformsSeconds);
        Assert.Equal(3228, r.TotalSeconds);
        Assert.Equal(r.TotalSeconds, r.Platforms.Sum(p => p.Seconds) + r.OtherPlatformsSeconds);   // content time is inside these, never added on top
        Assert.Equal(r.TotalSeconds, r.Weeks.Sum(w => w.TotalSeconds));
    }
}
