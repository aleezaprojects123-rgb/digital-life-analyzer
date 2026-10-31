using System.Text.Json;
using System.Text.Json.Nodes;
using Dla.Agent.Storage;
using Microsoft.Data.Sqlite;

namespace Dla.Agent.Tests;

/// <summary>
/// The timeline of browser activity periods. All times are fixed (2026-10-31, UTC) and every expected number is written out in seconds.
/// Minutes in the comments: 10:00-10:10 = 600 s. The browser extension is the only collector, so every event here is a browser event.
/// </summary>
public class UnifiedTimelineTests
{
    private static DateTimeOffset At(string hhmm, int day = 0)
    {
        var p = hhmm.Split(':');
        return new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero).AddDays(day).AddHours(int.Parse(p[0])).AddMinutes(int.Parse(p[1])).AddSeconds(p.Length > 2 ? int.Parse(p[2]) : 0);
    }

    private static string Z(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>A browser event line in the stored shape.</summary>
    private static string Web(string id, string start, string end, string platform = "unknown", string activity = "webpage", string domain = "example.com",
        int count = 0, string? session = "S1", int startDay = 0, int endDay = 0)
    {
        var s = At(start, startDay); var e = At(end, endDay);
        return new JsonObject
        {
            ["schema_version"] = "1", ["id"] = id, ["start"] = Z(s), ["end"] = Z(e), ["duration_seconds"] = (long)(e - s).TotalSeconds, ["source"] = "extension",
            ["platform"] = platform, ["domain"] = domain, ["url"] = $"https://{domain}/x", ["title"] = null, ["activity_type"] = activity, ["content_count"] = count,
            ["session_id"] = session, ["category"] = null, ["confidence"] = null, ["uncertain"] = false, ["label_source"] = null,
        }.ToJsonString();
    }

    private static UnifiedTimeline Build(params string[] lines) => UnifiedTimeline.FromJson(lines);
    private static string[] Ids(UnifiedTimeline t) => t.Entries.Select(e => e.Id).ToArray();

    // ---- the required examples --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Example1_Two_pages_back_to_back_are_20_minutes()
    {
        var t = Build(Web("a", "10:00", "10:10", domain: "wikipedia.org"), Web("b", "10:10", "10:20", domain: "github.com"));
        Assert.Equal(1200, t.ActiveSeconds);
        Assert.Equal(new[] { "a", "b" }, Ids(t));
    }

    [Fact]
    public void Example2_Two_periods_overlapping_by_5_minutes_are_15_minutes_not_20()
    {
        var t = Build(Web("a", "10:00", "10:10"), Web("b", "10:05", "10:15", domain: "github.com"));
        Assert.Equal(900, t.ActiveSeconds);
        Assert.Equal(1200, t.Entries.Sum(e => e.Seconds));                 // the plain sum of rows is what we must NOT report
        Assert.Equal(new[] { 600.0, 600.0 }, t.Entries.Select(e => e.Seconds).ToArray());   // and both events keep their own length
    }

    [Fact]
    public void Example3_A_30_minute_page_with_a_YouTube_period_inside_is_30_minutes_and_YouTube_stays_identifiable()
    {
        var t = Build(Web("page", "10:00", "10:30", domain: "example.com"), Web("yt", "10:05", "10:20", platform: "youtube", activity: "video", domain: "youtube.com", count: 1));
        Assert.Equal(1800, t.ActiveSeconds);
        var yt = t.Find("yt")!;
        Assert.Equal(("youtube", "video", 900.0, 1), (yt.Platform, yt.ActivityType, yt.Seconds, yt.ContentCount));
        Assert.Equal(1800, t.Find("page")!.Seconds);                       // the long period keeps its whole 30 minutes too
        var by = t.ActiveSecondsBy(e => e.Domain);
        Assert.Equal(900, by["youtube.com"]);                              // YouTube is credited 15 minutes (the later, more specific period)
        Assert.Equal(900, by["example.com"]);                              // the rest stays with the long period
        Assert.Equal(1800, by.Values.Sum());
    }

    // ---- ordering and sequences ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Events_are_ordered_by_start_then_end_then_id_whatever_order_they_arrive_in()
    {
        var t = Build(Web("d", "10:30", "10:40"), Web("b", "10:00", "10:20"), Web("a", "10:00", "10:20"), Web("z", "10:00", "10:05"), Web("c", "10:20", "10:30"));
        Assert.Equal(new[] { "z", "a", "b", "c", "d" }, Ids(t));
        Assert.True(t.Segments.Zip(t.Segments.Skip(1), (x, y) => x.EndMs <= y.StartMs).All(ok => ok), "segments are in order and never overlap");
    }

    [Fact]
    public void Page_then_another_page_then_the_first_page_again_keeps_all_three_and_counts_all_30_minutes()
    {
        var t = Build(Web("w1", "10:00", "10:10", domain: "wikipedia.org"), Web("g", "10:10", "10:15", domain: "github.com"), Web("w2", "10:15", "10:30", domain: "wikipedia.org"));
        Assert.Equal(new[] { "w1", "g", "w2" }, Ids(t));
        Assert.Equal(1800, t.ActiveSeconds);
        var by = t.ActiveSecondsBy(e => e.Domain);
        Assert.Equal(1500, by["wikipedia.org"]);                           // 10 + 15 minutes: the two stretches are not merged away
        Assert.Equal(300, by["github.com"]);
    }

    [Fact]
    public void A_page_then_YouTube_then_a_page_adds_up_period_by_period()
    {
        var t = Build(Web("p1", "10:00", "10:05"), Web("yt", "10:05", "10:20", platform: "youtube", activity: "video", domain: "youtube.com", count: 1), Web("p2", "10:20", "10:30"));
        Assert.Equal(1800, t.ActiveSeconds);
        var by = t.ActiveSecondsBy(e => e.Platform);
        Assert.Equal(900, by["youtube"]);
        Assert.Equal(900, by["unknown"]);
    }

    // ---- overlap shapes ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_windows_reporting_the_same_moment_are_counted_once()
    {
        var t = Build(Web("w1", "10:00", "10:30"), Web("w2", "10:05", "10:25", domain: "wikipedia.org"));
        Assert.Equal(1800, t.ActiveSeconds);
        Assert.Equal(1200, t.ActiveSecondsBy(e => e.Domain)["wikipedia.org"]);   // credited to the later-starting period: 10:05-10:25
    }

    [Fact]
    public void Nested_intervals_count_only_the_outer_one()
    {
        var t = Build(Web("outer", "10:00", "11:00"), Web("mid", "10:10", "10:50"), Web("inner", "10:20", "10:30"));
        Assert.Equal(3600, t.ActiveSeconds);
        Assert.Equal(new[] { "outer", "mid", "inner" }, Ids(t));
        Assert.Equal(new[] { 3600.0, 2400.0, 600.0 }, t.Entries.Select(e => e.Seconds).ToArray());
    }

    [Fact]
    public void Partially_overlapping_chain_counts_the_union()
    {
        // 10:00-10:10, 10:08-10:20, 10:18-10:25: union 10:00-10:25 = 1500 s, the rows add up to 600 + 720 + 420 = 1740 s
        var t = Build(Web("a", "10:00", "10:10"), Web("b", "10:08", "10:20"), Web("c", "10:18", "10:25"));
        Assert.Equal(1500, t.ActiveSeconds);
        Assert.Equal(1740, t.Entries.Sum(e => e.Seconds));
    }

    [Fact]
    public void Identical_intervals_count_once_and_both_events_remain()
    {
        var t = Build(Web("x1", "10:00", "10:10"), Web("x2", "10:00", "10:10"));
        Assert.Equal(600, t.ActiveSeconds);
        Assert.Equal(new[] { "x1", "x2" }, Ids(t));
        Assert.Equal(new[] { "x1", "x2" }, t.Segments.Single().EventIds);  // both are listed on the segment
    }

    [Fact]
    public void A_gap_between_events_is_not_counted_and_not_invented()
    {
        var t = Build(Web("a", "10:00", "10:10"), Web("b", "10:20", "10:30"));
        Assert.Equal(1200, t.ActiveSeconds);
        Assert.Equal(2, t.Segments.Count);
    }

    // ---- zero duration --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_zero_length_event_is_kept_adds_no_time_and_keeps_its_content_count()
    {
        var t = Build(Web("blip", "10:05", "10:05", platform: "instagram", activity: "reel", count: 1), Web("page", "10:00", "10:10"));
        Assert.Equal(600, t.ActiveSeconds);
        Assert.Equal(2, t.Entries.Count);
        Assert.True(t.Find("blip")!.IsZeroLength);
        Assert.Equal(1, t.ContentCount);
        Assert.DoesNotContain(t.Segments, s => s.EventIds.Contains("blip"));
    }

    [Fact]
    public void Zero_length_events_alone_give_zero_time_and_no_segments()
    {
        var t = Build(Web("p1", "10:00", "10:00"), Web("p2", "10:00", "10:00"));
        Assert.Equal(0, t.ActiveSeconds);
        Assert.Empty(t.Segments);
        Assert.Equal(2, t.Entries.Count);
    }

    [Fact]
    public void A_zero_length_event_on_the_boundary_of_two_events_changes_nothing()
    {
        var t = Build(Web("a", "10:00", "10:10"), Web("blip", "10:10", "10:10"), Web("b", "10:10", "10:20"));
        Assert.Equal(1200, t.ActiveSeconds);
        Assert.Equal(new[] { "a", "blip", "b" }, Ids(t));
    }

    // ---- midnight --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_event_across_midnight_is_one_event_and_splits_per_day_in_the_daily_totals()
    {
        var t = Build(Web("late", "23:50", "00:10", endDay: 1));
        var e = Assert.Single(t.Entries);
        Assert.Equal(1200, e.Seconds);
        Assert.Equal(1200, t.ActiveSeconds);
        var days = t.PerDay(TimeZoneInfo.Utc);
        Assert.Equal(new[] { (new DateOnly(2026, 10, 31), 600.0), (new DateOnly(2026, 11, 1), 600.0) }, days.Select(d => (d.Day, d.ActiveSeconds)).ToArray());
        Assert.Equal(t.ActiveSeconds, days.Sum(d => d.ActiveSeconds));
    }

    [Fact]
    public void Midnight_is_the_users_local_midnight_not_UTC()
    {
        // UTC+5: 18:50-19:10 UTC is 23:50-00:10 local
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5");
        var t = Build(Web("late", "18:50", "19:10"));
        var days = t.PerDay(zone);
        Assert.Equal(new[] { (new DateOnly(2026, 10, 31), 600.0), (new DateOnly(2026, 11, 1), 600.0) }, days.Select(d => (d.Day, d.ActiveSeconds)).ToArray());
        Assert.Single(t.PerDay(TimeZoneInfo.Utc));                          // the same event is one day in UTC
    }

    [Fact]
    public void Overlap_across_midnight_is_still_counted_once_per_day()
    {
        var t = Build(
            Web("long", "23:00", "01:00", endDay: 1),
            Web("inside", "23:30", "00:30", endDay: 1, domain: "github.com"));
        // union 23:00-01:00 = 7200 s; day 1: 23:00-24:00 = 3600, day 2: 00:00-01:00 = 3600 (the 23:30-00:30 period adds nothing)
        Assert.Equal(7200, t.ActiveSeconds);
        var days = t.PerDay(TimeZoneInfo.Utc);
        Assert.Equal(new[] { 3600.0, 3600.0 }, days.Select(d => d.ActiveSeconds).ToArray());
        Assert.Equal(t.ActiveSeconds, days.Sum(d => d.ActiveSeconds));
    }

    [Fact]
    public void Time_between_two_instants_cuts_segments_at_the_edges()
    {
        var t = Build(Web("a", "10:00", "10:10"), Web("b", "10:05", "10:20"));
        Assert.Equal(1200, t.ActiveSeconds);                                // union 10:00-10:20
        Assert.Equal(600, t.ActiveSecondsBetween(At("10:00"), At("10:10")));
        Assert.Equal(300, t.ActiveSecondsBetween(At("10:15"), At("10:30")));
        Assert.Equal(1200, t.ActiveSecondsBetween(At("09:00"), At("12:00")));
        Assert.Equal(0, t.ActiveSecondsBetween(At("11:00"), At("12:00")));
    }

    // ---- sessions, content counts, no merging --------------------------------------------------------------------------------------------------

    [Fact]
    public void Session_ids_are_preserved_on_every_event()
    {
        var t = Build(Web("a", "10:00", "10:10", session: "S1"), Web("b", "10:10", "10:20", session: "S2"), Web("c", "10:20", "10:30", session: null), Web("d", "10:30", "10:40", session: "S3"));
        Assert.Equal(new string?[] { "S1", "S2", null, "S3" }, t.Entries.Select(e => e.SessionId).ToArray());
    }

    [Fact]
    public void The_original_record_is_kept_exactly()
    {
        var web = Web("w", "10:00", "10:10");
        var t = Build(web);
        Assert.Equal(web, t.Find("w")!.Raw);
        Assert.Equal("extension", JsonDocument.Parse(t.Find("w")!.Raw).RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void Content_counts_are_summed_over_the_original_events_and_ignore_overlap()
    {
        // three overlapping Reel/video periods: only 10 minutes of time, but 3 items were watched
        var t = Build(
            Web("r1", "10:00", "10:10", platform: "instagram", activity: "reel", count: 1),
            Web("r2", "10:00", "10:10", platform: "instagram", activity: "reel", count: 1),
            Web("v1", "10:05", "10:10", platform: "youtube", activity: "video", count: 1),
            Web("w", "10:00", "10:10"));
        Assert.Equal(3, t.ContentCount);
        var by = t.ContentCountBy(e => e.Platform);
        Assert.Equal(2, by["instagram"]);
        Assert.Equal(1, by["youtube"]);
        Assert.Equal(600, t.ActiveSeconds);                                // four overlapping events, ten minutes
    }

    [Fact]
    public void Time_only_pages_have_time_and_no_count()
    {
        var t = Build(
            Web("feed", "10:00", "10:20", platform: "instagram", activity: "social_feed", domain: "instagram.com"),
            Web("search", "10:20", "10:30", platform: "youtube", activity: "search", domain: "youtube.com"),
            Web("reel", "10:30", "10:40", platform: "instagram", activity: "reel", domain: "instagram.com", count: 1));
        Assert.Equal(2400, t.ActiveSeconds);
        Assert.Equal(1, t.ContentCount);                                    // only the Reel is an item; the feed and the search are time only
    }

    [Fact]
    public void Unrelated_overlapping_events_are_never_merged()
    {
        var t = Build(Web("page", "10:00", "10:30"), Web("wiki", "10:10", "10:20", domain: "wikipedia.org"), Web("yt", "10:15", "10:25", platform: "youtube", activity: "video", domain: "youtube.com"));
        Assert.Equal(3, t.Entries.Count);
        Assert.Equal(new[] { "page", "wiki", "yt" }, Ids(t));
        Assert.Equal(1800, t.ActiveSeconds);
        var middle = t.Segments.Single(s => s.StartMs == At("10:15").ToUnixTimeMilliseconds());
        Assert.Equal(new[] { "page", "wiki", "yt" }, middle.EventIds);     // in the overlap all three are visible, not one merged blob
        Assert.Equal("yt", middle.PrimaryEventId);                          // the later-starting period is credited
    }

    [Fact]
    public void Every_second_is_credited_to_exactly_one_event_so_the_splits_add_up_to_the_total()
    {
        var t = Build(Web("w", "10:00", "10:30"), Web("a", "10:05", "10:15", platform: "youtube", domain: "youtube.com"), Web("b", "10:10", "10:20", platform: "google", domain: "google.com"));
        Assert.Equal(1800, t.ActiveSeconds);
        Assert.Equal(t.ActiveSeconds, t.ActiveSecondsBy(e => e.Platform).Values.Sum());
        Assert.Equal(t.ActiveSeconds, t.ActiveSecondsBy(e => e.Domain).Values.Sum());
    }

    [Fact]
    public void An_empty_timeline_is_all_zeros()
    {
        var t = Build();
        Assert.Equal((0, 0, 0), (t.ActiveSeconds, t.ContentCount, t.Entries.Count));
        Assert.Empty(t.PerDay(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Lines_that_are_not_browser_events_are_skipped_and_counted()
    {
        var oldDesktopStyle = Web("old", "10:00", "10:10").Replace("\"source\":\"extension\"", "\"source\":\"agent\"");
        var t = Build("not json", "{\"id\":\"x\"}", oldDesktopStyle, Web("ok", "10:00", "10:10"));
        Assert.Single(t.Entries);                                           // desktop events are not part of the timeline any more
        Assert.Equal(3, t.Skipped);
    }

    // ---- proof against a brute-force count ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_union_rule_agrees_with_counting_every_single_second_for_300_random_events()
    {
        var rnd = new Random(20261031);
        var lines = new List<string>();
        var active = new bool[7200];
        for (var i = 0; i < 300; i++)
        {
            var start = rnd.Next(0, 6600);
            var len = rnd.Next(0, 600);
            var s = At("00:00").AddSeconds(start); var e = s.AddSeconds(len);
            var o = JsonNode.Parse(Web($"e{i}", "00:00", "00:00", domain: i % 2 == 0 ? "example.com" : "github.com"))!.AsObject();
            o["start"] = Z(s); o["end"] = Z(e); o["duration_seconds"] = len;
            lines.Add(o.ToJsonString());
            for (var sec = start; sec < start + len; sec++) active[sec] = true;
        }
        var t = UnifiedTimeline.FromJson(lines);
        Assert.Equal(Enumerable.Range(0, 7200).Count(x => active[x]), t.ActiveSeconds);
        Assert.Equal(300, t.Entries.Count);
        Assert.Equal(t.ActiveSeconds, t.ActiveSecondsBy(e => e.Domain).Values.Sum());
        // shuffling the input changes nothing
        var shuffled = UnifiedTimeline.FromJson(lines.OrderBy(_ => rnd.Next()));
        Assert.Equal(t.ActiveSeconds, shuffled.ActiveSeconds);
        Assert.Equal(t.Segments, shuffled.Segments, new SegmentComparer());
    }

    private sealed class SegmentComparer : IEqualityComparer<TimelineSegment>
    {
        public bool Equals(TimelineSegment? a, TimelineSegment? b) =>
            a is not null && b is not null && (a.StartMs, a.EndMs, a.PrimaryEventId) == (b.StartMs, b.EndMs, b.PrimaryEventId) && a.EventIds.SequenceEqual(b.EventIds);
        public int GetHashCode(TimelineSegment s) => HashCode.Combine(s.StartMs, s.EndMs);
    }
}

/// <summary>The timeline reads from the real SQLite store and never changes it.</summary>
public class UnifiedTimelineStoreTests : IDisposable
{
    private readonly TempData _t = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_t.Root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Z(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    [Fact]
    public void Browser_rows_from_SQLite_become_one_timeline_without_changing_the_database()
    {
        using var store = SqliteEventStore.Open(Path.Combine(_t.Root, "dla.db"), () => Now);
        var t0 = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
        string Web(string id, int from, int to, int count, string session, string platform, string activity, string domain) =>
            $$"""{"schema_version":"1","id":"{{id}}","start":"{{Z(t0.AddMinutes(from))}}","end":"{{Z(t0.AddMinutes(to))}}","duration_seconds":{{(to - from) * 60}},"source":"extension","platform":"{{platform}}","domain":"{{domain}}","url":"https://{{domain}}/x","title":null,"activity_type":"{{activity}}","content_count":{{count}},"session_id":"{{session}}","category":null,"confidence":null,"uncertain":false,"label_source":null}""";

        store.Append([
            Web("yt", 5, 20, 1, "S1", "youtube", "video", "youtube.com"),
            Web("yt2", 20, 25, 0, "S1", "youtube", "search", "youtube.com"),
            Web("wiki", 0, 5, 0, "S1", "unknown", "webpage", "wikipedia.org"),
            Web("reel", 30, 40, 1, "S1", "instagram", "reel", "instagram.com"),
        ]);
        var rowsBefore = store.Read();

        var timeline = UnifiedTimeline.FromStore(store);

        // wikipedia 10:00-10:05, youtube 10:05-10:25, a gap 10:25-10:30, a Reel 10:30-10:40: 300 + 1200 + 600 = 2100 s
        Assert.Equal(2100, timeline.ActiveSeconds);
        Assert.Equal(2, timeline.ContentCount);
        Assert.Equal(4, timeline.Entries.Count);
        Assert.Equal(new[] { "S1" }, timeline.Entries.Select(e => e.SessionId).Distinct().ToArray());
        Assert.Equal(1200, timeline.ActiveSecondsBy(e => e.Platform)["youtube"]);

        // nothing was changed or removed
        Assert.Equal(4, store.Count());
        Assert.Equal(rowsBefore, store.Read());
        Assert.Equal(timeline.Entries.Select(e => e.Raw).Order().ToArray(), rowsBefore.Order().ToArray());
    }

    [Fact]
    public void A_time_range_query_gives_a_timeline_of_just_that_range()
    {
        using var store = SqliteEventStore.Open(Path.Combine(_t.Root, "dla.db"), () => Now);
        string Web(string id, int hour) =>
            $$"""{"schema_version":"1","id":"{{id}}","start":"2026-10-31T{{hour:00}}:00:00Z","end":"2026-10-31T{{hour:00}}:30:00Z","duration_seconds":1800,"source":"extension","platform":"unknown","domain":"example.com","url":"https://example.com/x","title":null,"activity_type":"webpage","content_count":0,"session_id":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";
        store.Append([Web("h9", 9), Web("h10", 10), Web("h11", 11)]);
        var t = UnifiedTimeline.FromStore(store, new EventQuery(From: new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero), To: new DateTimeOffset(2026, 10, 31, 11, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new[] { "h10" }, t.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(1800, t.ActiveSeconds);
    }
}
