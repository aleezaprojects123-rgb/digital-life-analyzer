using System.Text.Json.Nodes;
using Dla.Agent.Reports;
using Dla.Agent.Storage;

namespace Dla.Agent.Tests;

/// <summary>
/// Weekly and monthly reports. Every event time is fixed and every expected number is written out in seconds. Unless a test says
/// otherwise the time zone is UTC. 2026-10-26 is a Monday. Seconds in the comments: 10 min = 600 s.
/// </summary>
public class ReportTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Monday = new(2026, 10, 26);

    internal static DateTimeOffset T(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    /// <summary>The stored (sanitized) address an event of this kind normally has: content kinds point at one item, everything else at the site root.</summary>
    private static string DefaultUrl(string platform, string activity) => (platform, activity) switch
    {
        ("instagram", "reel") => "https://www.instagram.com/reels/*",
        ("facebook", "reel") => "https://www.facebook.com/reel/*",
        ("youtube", "short_video") => "https://www.youtube.com/shorts/*",
        ("youtube", "video") => "https://www.youtube.com/watch",
        ("tiktok", "short_video") => "https://www.tiktok.com/@someone/video/*",
        _ => "https://example.com/x",
    };

    internal static string Ev(string id, DateTimeOffset start, int seconds, string platform = "unknown", string activity = "webpage", int count = 0, string? url = null)
    {
        var end = start.AddSeconds(seconds);
        return new JsonObject
        {
            ["schema_version"] = "1", ["id"] = id, ["start"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["end"] = end.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["duration_seconds"] = seconds, ["source"] = "extension", ["platform"] = platform, ["domain"] = "example.com", ["url"] = url ?? DefaultUrl(platform, activity),
            ["title"] = null, ["activity_type"] = activity, ["content_count"] = count, ["session_id"] = "S1", ["category"] = null, ["confidence"] = null,
            ["uncertain"] = false, ["label_source"] = null,
        }.ToJsonString();
    }

    private static ActivityReport Week(params string[] events) => ReportGenerator.Build(ReportPeriod.Week(Monday, Utc), events);
    private static ActivityReport Month(int year, int month, params string[] events) => ReportGenerator.Build(ReportPeriod.Month(year, month, Utc), events);

    // ---- 1, 2: total time -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Weekly_total_time_adds_up_the_events_of_the_week()
    {
        var r = Week(Ev("a", T(2026, 10, 26, 9), 600), Ev("b", T(2026, 10, 28, 14), 1800));
        Assert.Equal(2400, r.TotalSeconds);
        Assert.Equal(ReportKind.Weekly, r.Kind);
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)), (r.FirstDay, r.LastDay));
    }

    [Fact]
    public void Monthly_total_time_adds_up_the_events_of_the_month()
    {
        var r = Month(2026, 11, Ev("a", T(2026, 11, 1, 8), 600), Ev("b", T(2026, 11, 15, 12), 900), Ev("c", T(2026, 11, 30, 23, 0), 1200));
        Assert.Equal(2700, r.TotalSeconds);
        Assert.Equal(ReportKind.Monthly, r.Kind);
    }

    [Fact]
    public void Overlapping_events_count_a_moment_once()
    {
        var r = Week(Ev("a", T(2026, 10, 26, 10, 0), 600), Ev("b", T(2026, 10, 26, 10, 5), 600));   // 10:00-10:10 and 10:05-10:15
        Assert.Equal(900, r.TotalSeconds);
    }

    [Fact]
    public void An_empty_period_is_all_zeros_with_every_day_listed()
    {
        var r = Week();
        Assert.Equal(0, r.TotalSeconds);
        Assert.Equal(7, r.Days.Count);
        Assert.All(r.Platforms, p => Assert.Equal(0, p.Seconds));
        Assert.All(r.Content, c => Assert.Equal((0, 0.0), (c.Count, c.WatchSeconds)));
    }

    // ---- 3: platform totals -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Platform_totals_are_kept_per_platform_and_other_sites_are_reported_apart()
    {
        var r = Week(
            Ev("ig", T(2026, 10, 26, 9), 100, "instagram", "social_feed"),
            Ev("yt", T(2026, 10, 26, 10), 200, "youtube", "webpage"),
            Ev("tt", T(2026, 10, 26, 11), 300, "tiktok", "social_feed"),
            Ev("fb", T(2026, 10, 26, 12), 400, "facebook", "social_feed"),
            Ev("other", T(2026, 10, 26, 13), 50, "unknown", "webpage"));
        Assert.Equal([100.0, 200.0, 300.0, 400.0], r.Platforms.Select(p => p.Seconds).ToArray());
        Assert.Equal(["instagram", "youtube", "tiktok", "facebook"], r.Platforms.Select(p => p.Platform).ToArray());
        Assert.Equal(50, r.OtherPlatformsSeconds);
        Assert.Equal(1050, r.TotalSeconds);
        Assert.Equal(r.TotalSeconds, r.Platforms.Sum(p => p.Seconds) + r.OtherPlatformsSeconds);
    }

    // ---- 4 to 8: content count and watching time --------------------------------------------------------------------------------------------

    [Fact]
    public void Instagram_reels_are_counted_and_timed()
    {
        var r = Week(Ev("r1", T(2026, 10, 26, 9, 0), 50, "instagram", "reel", 1), Ev("r2", T(2026, 10, 26, 9, 1), 30, "instagram", "reel", 1),
            Ev("f", T(2026, 10, 26, 9, 5), 60, "instagram", "social_feed"));
        var c = r.ContentStat("instagram", "reel");
        Assert.Equal((2, 80.0), (c.Count, c.WatchSeconds));
        Assert.Equal(140, r.PlatformTotal("instagram").Seconds);
    }

    [Fact]
    public void YouTube_shorts_are_counted_and_timed()
    {
        var r = Week(Ev("s1", T(2026, 10, 27, 9, 0), 11, "youtube", "short_video", 1), Ev("s2", T(2026, 10, 27, 9, 1), 21, "youtube", "short_video", 1));
        var c = r.ContentStat("youtube", "short_video");
        Assert.Equal((2, 32.0), (c.Count, c.WatchSeconds));
        Assert.Equal((0, 0.0), (r.ContentStat("youtube", "video").Count, r.ContentStat("youtube", "video").WatchSeconds));
    }

    [Fact]
    public void YouTube_normal_videos_are_counted_and_timed()
    {
        var r = Week(Ev("v1", T(2026, 10, 27, 9, 0), 300, "youtube", "video", 1), Ev("v2", T(2026, 10, 27, 10, 0), 200, "youtube", "video", 1));
        var c = r.ContentStat("youtube", "video");
        Assert.Equal((2, 500.0), (c.Count, c.WatchSeconds));
        Assert.Equal((0, 0.0), (r.ContentStat("youtube", "short_video").Count, r.ContentStat("youtube", "short_video").WatchSeconds));
    }

    [Fact]
    public void TikTok_videos_are_counted_and_timed()
    {
        var r = Week(Ev("t1", T(2026, 10, 28, 9, 0), 11, "tiktok", "short_video", 1), Ev("t2", T(2026, 10, 28, 9, 1), 31, "tiktok", "short_video", 1),
            Ev("t3", T(2026, 10, 28, 9, 2), 12, "tiktok", "short_video", 1));
        var c = r.ContentStat("tiktok", "short_video");
        Assert.Equal((3, 54.0), (c.Count, c.WatchSeconds));
    }

    [Fact]
    public void Facebook_reels_are_counted_and_timed_and_the_reel_landing_page_adds_platform_time_only()
    {
        // The real Facebook test: feed 21 s, the /reel landing page 4 s (activity type reel, no item), then one reel 18 s.
        var r = Week(Ev("fb0", T(2026, 10, 28, 9, 0), 21, "facebook", "social_feed", url: "https://www.facebook.com"),
            Ev("fb1", T(2026, 10, 28, 9, 1), 4, "facebook", "reel", url: "https://www.facebook.com/reel"),
            Ev("fb2", T(2026, 10, 28, 9, 2), 18, "facebook", "reel", 1, url: "https://www.facebook.com/reel/*"));
        var c = r.ContentStat("facebook", "reel");
        Assert.Equal((1, 18.0), (c.Count, c.WatchSeconds));
        Assert.Equal(43, r.PlatformTotal("facebook").Seconds);        // 21 + 4 + 18: the landing page is still platform time
        Assert.Equal(43, r.TotalSeconds);
    }

    // ---- landing and navigation pages versus actual content ---------------------------------------------------------------------------------

    [Fact]
    public void Facebook_reel_landing_page_alone_is_platform_time_but_no_reel_time_and_no_reel_count()
    {
        foreach (var landing in new[] { "https://www.facebook.com/reel", "https://www.facebook.com/reel/", "https://www.facebook.com/reels" })
        {
            var r = Week(Ev("l", T(2026, 10, 26, 9), 40, "facebook", "reel", url: landing));
            Assert.Equal(40, r.PlatformTotal("facebook").Seconds);
            Assert.Equal((0, 0.0), (r.ContentStat("facebook", "reel").Count, r.ContentStat("facebook", "reel").WatchSeconds));
            Assert.Equal(40, r.TotalSeconds);
        }
    }

    [Fact]
    public void An_actual_Facebook_reel_is_platform_time_reel_time_and_one_reel()
    {
        var r = Week(Ev("fb", T(2026, 10, 26, 9), 30, "facebook", "reel", 1, url: "https://www.facebook.com/reel/*"));
        Assert.Equal(30, r.PlatformTotal("facebook").Seconds);
        Assert.Equal((1, 30.0), (r.ContentStat("facebook", "reel").Count, r.ContentStat("facebook", "reel").WatchSeconds));
    }

    [Fact]
    public void The_same_distinction_holds_for_every_other_content_kind()
    {
        // Each case: a landing-style address that shares the content activity type (time only), then a real item (counted and timed).
        (string Platform, string Activity, string Landing, string Item)[] cases =
        [
            ("instagram", "reel", "https://www.instagram.com/reels", "https://www.instagram.com/reels/*"),
            ("instagram", "reel", "https://www.instagram.com/reel", "https://www.instagram.com/someone/reel/*"),
            ("youtube", "short_video", "https://www.youtube.com/shorts", "https://www.youtube.com/shorts/*"),
            ("youtube", "video", "https://www.youtube.com/embed", "https://www.youtube.com/embed/*"),
            ("tiktok", "short_video", "https://www.tiktok.com/@someone", "https://www.tiktok.com/@someone/video/*"),
        ];
        foreach (var (platform, activity, landing, item) in cases)
        {
            var r = Week(Ev("land", T(2026, 10, 26, 9, 0), 10, platform, activity, url: landing), Ev("item", T(2026, 10, 26, 9, 5), 20, platform, activity, 1, url: item));
            Assert.Equal(30, r.PlatformTotal(platform).Seconds);
            var c = r.ContentStat(platform, activity);
            Assert.Equal((1, 20.0), (c.Count, c.WatchSeconds));
        }
    }

    [Fact]
    public void A_continuation_keeps_adding_watching_time_for_every_content_kind_without_adding_to_the_count()
    {
        (string Platform, string Activity, string Item)[] cases =
        [
            ("instagram", "reel", "https://www.instagram.com/reels/*"),
            ("facebook", "reel", "https://www.facebook.com/reel/*"),
            ("youtube", "short_video", "https://www.youtube.com/shorts/*"),
            ("youtube", "video", "https://www.youtube.com/watch"),           // the real stored address: the video id is in the query, which is removed
            ("tiktok", "short_video", "https://www.tiktok.com/@someone/video/*"),
        ];
        foreach (var (platform, activity, item) in cases)
        {
            var r = Week(Ev("first", T(2026, 10, 26, 9, 0), 10, platform, activity, 1, url: item), Ev("again", T(2026, 10, 26, 9, 30), 40, platform, activity, 0, url: item));
            var c = r.ContentStat(platform, activity);
            Assert.Equal((1, 50.0), (c.Count, c.WatchSeconds));
            Assert.Equal(50, r.PlatformTotal(platform).Seconds);
        }
    }

    [Fact]
    public void Landing_time_is_not_content_time_even_when_a_landing_page_event_overlaps_a_real_one()
    {
        // The landing page is the later start, so it is credited the overlap; that second is platform time, not reel time.
        var r = Week(Ev("reel", T(2026, 10, 26, 9, 0), 60, "facebook", "reel", 1, url: "https://www.facebook.com/reel/*"),
            Ev("landing", T(2026, 10, 26, 9, 0, 40), 30, "facebook", "reel", url: "https://www.facebook.com/reel"));
        Assert.Equal(70, r.PlatformTotal("facebook").Seconds);
        Assert.Equal(40, r.ContentStat("facebook", "reel").WatchSeconds);
        Assert.Equal((1, 40.0), (r.ContentStat("facebook", "reel").Count, r.ContentStat("facebook", "reel").WatchSeconds));
    }

    // ---- 12: continuations ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_continuation_of_the_same_content_adds_time_but_not_a_second_count()
    {
        var r = Week(Ev("v", T(2026, 10, 26, 9, 0), 9, "youtube", "video", 1), Ev("v-cont", T(2026, 10, 26, 9, 5), 40, "youtube", "video", 0));
        var c = r.ContentStat("youtube", "video");
        Assert.Equal((1, 49.0), (c.Count, c.WatchSeconds));
    }

    // ---- 13, 14: platform total versus content time -----------------------------------------------------------------------------------------

    [Fact]
    public void Platform_total_includes_browsing_and_content_time_stays_separate()
    {
        var r = Week(
            Ev("home", T(2026, 10, 26, 9, 0), 600, "youtube", "webpage"),
            Ev("search", T(2026, 10, 26, 10, 0), 300, "youtube", "search"),
            Ev("shorts", T(2026, 10, 26, 11, 0), 1200, "youtube", "short_video", 1),
            Ev("video", T(2026, 10, 26, 12, 0), 900, "youtube", "video", 1));
        Assert.Equal(3000, r.PlatformTotal("youtube").Seconds);                       // 10 + 5 + 20 + 15 minutes
        Assert.Equal(1200, r.ContentStat("youtube", "short_video").WatchSeconds);
        Assert.Equal(900, r.ContentStat("youtube", "video").WatchSeconds);
        Assert.Equal(3000, r.TotalSeconds);
    }

    [Fact]
    public void Content_time_never_exceeds_the_platform_total_even_when_events_overlap()
    {
        var r = Week(Ev("page", T(2026, 10, 26, 10, 0), 1800, "youtube", "webpage"), Ev("v", T(2026, 10, 26, 10, 5), 900, "youtube", "video", 1));
        Assert.Equal(1800, r.PlatformTotal("youtube").Seconds);
        Assert.Equal(900, r.ContentStat("youtube", "video").WatchSeconds);
        Assert.True(r.ContentStat("youtube", "video").WatchSeconds <= r.PlatformTotal("youtube").Seconds);
    }

    // ---- 9, 10: daily breakdown -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Weekly_daily_totals_list_seven_days_Monday_to_Sunday()
    {
        var r = Week(Ev("a", T(2026, 10, 26, 9), 600), Ev("b", T(2026, 10, 28, 14), 1800), Ev("c", T(2026, 11, 1, 20), 120));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            r.Days.Select(d => d.DayOfWeek).ToArray());
        Assert.Equal([600.0, 0, 1800, 0, 0, 0, 120], r.Days.Select(d => d.TotalSeconds).ToArray());
        Assert.Equal(r.TotalSeconds, r.Days.Sum(d => d.TotalSeconds));
    }

    [Fact]
    public void Monthly_daily_totals_list_every_day_of_the_month()
    {
        var r = Month(2026, 11, Ev("a", T(2026, 11, 1, 8), 600), Ev("b", T(2026, 11, 10, 8), 60), Ev("c", T(2026, 11, 30, 8), 90));
        Assert.Equal(30, r.Days.Count);
        Assert.Equal(new DateOnly(2026, 11, 1), r.Days[0].Date);
        Assert.Equal(new DateOnly(2026, 11, 30), r.Days[29].Date);
        Assert.Equal((600.0, 60.0, 90.0), (r.Days[0].TotalSeconds, r.Days[9].TotalSeconds, r.Days[29].TotalSeconds));
        Assert.Equal(r.TotalSeconds, r.Days.Sum(d => d.TotalSeconds));
    }

    [Fact]
    public void A_week_can_start_on_any_day_and_WeekContaining_gives_Monday_to_Sunday()
    {
        var r = ReportGenerator.Build(ReportPeriod.Week(new DateOnly(2026, 10, 29), Utc), [Ev("a", T(2026, 10, 29, 9), 60)]);
        Assert.Equal(DayOfWeek.Thursday, r.Days[0].DayOfWeek);
        Assert.Equal(60, r.Days[0].TotalSeconds);
        Assert.Equal(new DateOnly(2026, 10, 26), ReportPeriod.WeekContaining(new DateOnly(2026, 10, 31), Utc).FirstDay);
        Assert.Equal(new DateOnly(2026, 10, 26), ReportPeriod.WeekContaining(new DateOnly(2026, 11, 1), Utc).FirstDay);   // a Sunday belongs to the week before
        Assert.Equal(new DateOnly(2026, 11, 2), ReportPeriod.WeekContaining(new DateOnly(2026, 11, 2), Utc).FirstDay);
    }

    // ---- 11: events outside the period ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Events_outside_the_period_are_left_out_of_time_platforms_and_content()
    {
        var r = Week(
            Ev("in", T(2026, 10, 27, 9), 100, "instagram", "reel", 1),
            Ev("before", T(2026, 10, 25, 9), 500, "instagram", "reel", 1),     // the Sunday before
            Ev("after", T(2026, 11, 2, 9), 700, "instagram", "reel", 1));      // the Monday after
        Assert.Equal(100, r.TotalSeconds);
        Assert.Equal(100, r.PlatformTotal("instagram").Seconds);
        Assert.Equal((1, 100.0), (r.ContentStat("instagram", "reel").Count, r.ContentStat("instagram", "reel").WatchSeconds));
    }

    // ---- 15: boundaries ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_event_across_midnight_is_split_between_the_two_days_and_totals_stay_whole()
    {
        var r = Week(Ev("x", T(2026, 10, 31, 23, 59), 120));   // Saturday 23:59 to Sunday 00:01
        Assert.Equal((60.0, 60.0), (r.Days[5].TotalSeconds, r.Days[6].TotalSeconds));
        Assert.Equal(120, r.TotalSeconds);
    }

    [Fact]
    public void An_event_across_a_month_end_is_split_between_the_months_and_counted_once_where_it_starts()
    {
        var crossing = Ev("x", T(2026, 10, 31, 23, 50), 1200, "youtube", "video", 1);   // 23:50 to 00:10
        var oct = Month(2026, 10, crossing);
        var nov = Month(2026, 11, crossing);
        Assert.Equal((600.0, 600.0), (oct.TotalSeconds, nov.TotalSeconds));
        Assert.Equal((1, 0), (oct.ContentStat("youtube", "video").Count, nov.ContentStat("youtube", "video").Count));
        Assert.Equal((600.0, 600.0), (oct.ContentStat("youtube", "video").WatchSeconds, nov.ContentStat("youtube", "video").WatchSeconds));
        Assert.Equal(600, oct.Days[30].TotalSeconds);     // 31 October
        Assert.Equal(600, nov.Days[0].TotalSeconds);      // 1 November
    }

    [Fact]
    public void Period_edges_are_half_open_an_event_starting_at_midnight_is_in_and_one_ending_at_midnight_adds_nothing_to_the_next_period()
    {
        var r = Month(2026, 11, Ev("starts", T(2026, 11, 1, 0, 0), 60), Ev("ends", T(2026, 10, 31, 23, 59), 60), Ev("next", T(2026, 12, 1, 0, 0), 60));
        Assert.Equal(60, r.TotalSeconds);
    }

    [Fact]
    public void A_zero_length_event_at_the_start_of_the_period_still_counts_its_content_but_no_time()
    {
        var r = Week(Ev("z", T(2026, 10, 26, 0, 0), 0, "tiktok", "short_video", 1));
        Assert.Equal((1, 0.0, 0.0), (r.ContentStat("tiktok", "short_video").Count, r.ContentStat("tiktok", "short_video").WatchSeconds, r.TotalSeconds));
    }

    [Theory]
    [InlineData(2026, 1, 31)]
    [InlineData(2026, 2, 28)]
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 12, 31)]
    [InlineData(2028, 2, 29)]
    public void A_month_has_the_right_number_of_days(int year, int month, int days)
    {
        var r = Month(year, month);
        Assert.Equal(days, r.Days.Count);
        Assert.Equal(new DateOnly(year, month, days), r.LastDay);
    }

    // ---- 16: leap year ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void February_of_a_leap_year_has_29_days_and_the_29th_is_counted()
    {
        var r = Month(2028, 2, Ev("leap", T(2028, 2, 29, 12), 300), Ev("march", T(2028, 3, 1, 0), 999));
        Assert.Equal(29, r.Days.Count);
        Assert.Equal(300, r.Days[28].TotalSeconds);
        Assert.Equal(300, r.TotalSeconds);
    }

    [Fact]
    public void February_of_a_normal_year_has_28_days_and_nothing_spills_into_it_from_March()
    {
        var r = Month(2027, 2, Ev("last", T(2027, 2, 28, 12), 300), Ev("march", T(2027, 3, 1, 0), 999));
        Assert.Equal(28, r.Days.Count);
        Assert.Equal(300, r.TotalSeconds);
    }

    [Fact]
    public void An_invalid_month_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportPeriod.Month(2026, 13, Utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportPeriod.Month(2026, 0, Utc));
    }

    // ---- time zones and the real store -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Days_follow_the_local_time_zone_not_UTC()
    {
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("test+5", TimeSpan.FromHours(5), "test+5", "test+5");
        // 2026-10-26 20:00Z is already Tuesday 01:00 at +05:00; the week still starts on Monday local midnight (Sunday 19:00Z).
        var r = ReportGenerator.Build(ReportPeriod.Week(Monday, plus5), [Ev("a", T(2026, 10, 26, 20), 600), Ev("sun", T(2026, 10, 25, 18, 59), 60)]);
        Assert.Equal(600, r.Days[1].TotalSeconds);
        Assert.Equal(0, r.Days[0].TotalSeconds);
        Assert.Equal(600, r.TotalSeconds);                   // the event at 18:59Z is Sunday 23:59 local: before the week
    }

    [Fact]
    public void A_report_from_the_real_SQLite_store_matches_the_pure_calculation()
    {
        using var dir = new TempData();
        Directory.CreateDirectory(dir.DataDir);
        var lines = new[]
        {
            Ev("a", T(2026, 10, 26, 9), 600, "youtube", "webpage"),
            Ev("b", T(2026, 10, 26, 10), 900, "youtube", "video", 1),
            Ev("c", T(2026, 10, 31, 23, 59, 0), 120, "instagram", "reel", 1),
            Ev("d", T(2026, 11, 2, 9), 500, "tiktok", "short_video", 1),      // next week
        };
        using var store = SqliteEventStore.Open(Path.Combine(dir.DataDir, "dla.db"), () => T(2026, 11, 1, 12));
        store.Append(lines);

        var fromStore = new ReportGenerator(store, Utc).GetWeeklyReport(Monday);
        Assert.Equal(ReportGenerator.Build(ReportPeriod.Week(Monday, Utc), lines), fromStore, ReportComparer.Instance);
        Assert.Equal(1620, fromStore.TotalSeconds);
        Assert.Equal(1500, fromStore.PlatformTotal("youtube").Seconds);
        Assert.Equal(1, fromStore.ContentStat("instagram", "reel").Count);
        Assert.Equal(0, fromStore.ContentStat("tiktok", "short_video").Count);
        Assert.Equal(60, fromStore.Days[6].TotalSeconds);

        var january = new ReportGenerator(store, Utc).GetMonthlyReport(2026, 1);
        Assert.Equal(0, january.TotalSeconds);
    }

    /// <summary>Records hold lists, which records compare by reference, so reports are compared by their numbers.</summary>
    private sealed class ReportComparer : IEqualityComparer<ActivityReport>
    {
        public static readonly ReportComparer Instance = new();
        public bool Equals(ActivityReport? a, ActivityReport? b) => a is not null && b is not null && Key(a) == Key(b);
        public int GetHashCode(ActivityReport r) => Key(r).GetHashCode();
        private static string Key(ActivityReport r) =>
            string.Join("|", r.Kind, r.FirstDay, r.LastDay, r.TotalSeconds, r.OtherPlatformsSeconds,
                string.Join(",", r.Platforms.Select(p => $"{p.Platform}:{p.Seconds}")),
                string.Join(",", r.Content.Select(c => $"{c.Platform}/{c.ActivityType}:{c.Count}:{c.WatchSeconds}")),
                string.Join(",", r.Days.Select(d => $"{d.Date}:{d.TotalSeconds}")));
    }
}
