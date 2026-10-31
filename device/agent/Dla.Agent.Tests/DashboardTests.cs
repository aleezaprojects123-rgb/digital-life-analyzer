using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Reports;
using Dla.Agent.Storage;
using static Dla.Agent.Tests.ReportTests;

namespace Dla.Agent.Tests;

/// <summary>
/// The daily dashboard: today's platform times and content counts, calculated from the events when asked, and its route
/// GET /dashboard/today (UI token only). Today in these tests is 2026-10-31 (UTC) unless a test moves the clock.
/// </summary>
public class DashboardTests
{
    private const int Port = 49721;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static readonly AgentLinkConfig Config = LinkTestData.Config(Port, LinkTestData.ChromeId);
    private static readonly FixedStatus Agreed = new(new AgentStatus(true, true, 2));

    private static DashboardToday Dash(params string[] events) =>
        DashboardToday.From(ReportGenerator.Build(ReportPeriod.Day(Today, Utc), events, Today));

    private static DateTimeOffset At(int h, int m = 0, int s = 0) => T(2026, 10, 31, h, m, s);

    private sealed class Fixture : IDisposable
    {
        private readonly TempData _dir = new();
        public DateTimeOffset Now;
        public SqliteEventStore Store { get; }
        public ReportService Service { get; }
        public Fixture(DateTimeOffset now, TimeZoneInfo? zone = null)
        {
            Now = now;
            Directory.CreateDirectory(_dir.DataDir);
            Store = SqliteEventStore.Open(Path.Combine(_dir.DataDir, "dla.db"), () => Now);
            Service = new ReportService(new ReportGenerator(Store, zone ?? Utc, () => Now));
        }
        public void Dispose() { Store.Dispose(); _dir.Dispose(); }
    }

    // ---- the calculation -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_day_has_every_total_and_count_at_zero_and_all_four_platforms_listed()
    {
        var d = Dash();
        Assert.Equal(Today, d.Date);
        Assert.Equal((0.0, 0.0), (d.TotalSeconds, d.OtherPlatformsSeconds));
        Assert.Equal(["instagram", "youtube", "tiktok", "facebook"], d.Platforms.Select(p => p.Platform).ToArray());
        Assert.All(d.Platforms, p => { Assert.Equal(0, p.TotalSeconds); Assert.All(p.Content.Values, c => Assert.Equal(0, c)); });
        Assert.Equal(["reels"], d.Platform("instagram").Content.Keys.ToArray());
        Assert.Equal(["shorts", "videos"], d.Platform("youtube").Content.Keys.ToArray());
        Assert.Equal(["videos"], d.Platform("tiktok").Content.Keys.ToArray());
        Assert.Equal(["reels"], d.Platform("facebook").Content.Keys.ToArray());
    }

    [Fact]
    public void Instagram_time_is_the_sum_of_its_events_feed_included_and_reels_are_the_sum_of_content_count_with_continuations_adding_none()
    {
        var d = Dash(
            Ev("r1", At(9, 0), 30, "instagram", "reel", 1),
            Ev("r2", At(9, 5), 45, "instagram", "reel", 1),
            Ev("r2more", At(9, 10), 20, "instagram", "reel", 0),                       // the same reel again: time, no count
            Ev("feed", At(9, 20), 120, "instagram", "social_feed", url: "https://www.instagram.com"));
        var ig = d.Platform("instagram");
        Assert.Equal(215, ig.TotalSeconds);                                             // 30 + 45 + 20 + 120
        Assert.Equal(2, ig.Content["reels"]);
        Assert.Equal(215, d.TotalSeconds);
    }

    [Fact]
    public void The_example_from_the_brief_195_seconds_and_two_reels()
    {
        var d = Dash(Ev("a", At(9, 0), 30, "instagram", "reel", 1), Ev("b", At(9, 5), 45, "instagram", "reel", 1), Ev("c", At(9, 10), 120, "instagram", "social_feed"));
        Assert.Equal((195.0, 2), (d.Platform("instagram").TotalSeconds, d.Platform("instagram").Content["reels"]));
    }

    [Fact]
    public void YouTube_shorts_and_normal_videos_are_counted_separately_and_home_and_search_time_is_in_the_total()
    {
        var d = Dash(
            Ev("home", At(9, 0), 600, "youtube", "webpage", url: "https://www.youtube.com"),
            Ev("search", At(9, 20), 300, "youtube", "search", url: "https://www.youtube.com/results"),
            Ev("s1", At(10, 0), 11, "youtube", "short_video", 1), Ev("s2", At(10, 1), 21, "youtube", "short_video", 1), Ev("s3", At(10, 2), 9, "youtube", "short_video", 1),
            Ev("v1", At(11, 0), 900, "youtube", "video", 1), Ev("v1more", At(11, 30), 40, "youtube", "video", 0));
        var yt = d.Platform("youtube");
        Assert.Equal(3, yt.Content["shorts"]);
        Assert.Equal(1, yt.Content["videos"]);                                          // the continuation is not a second video
        Assert.Equal(600 + 300 + 41 + 940, yt.TotalSeconds);
    }

    [Fact]
    public void TikTok_videos_are_counted_from_short_video_events()
    {
        var d = Dash(Ev("f", At(9, 0), 20, "tiktok", "social_feed", url: "https://www.tiktok.com"),
                     Ev("a", At(9, 1), 11, "tiktok", "short_video", 1), Ev("b", At(9, 2), 31, "tiktok", "short_video", 1), Ev("c", At(9, 3), 12, "tiktok", "short_video", 1),
                     Ev("c2", At(9, 4), 5, "tiktok", "short_video", 0));
        Assert.Equal(3, d.Platform("tiktok").Content["videos"]);
        Assert.Equal(79, d.Platform("tiktok").TotalSeconds);
    }

    [Fact]
    public void Facebook_reels_are_counted_from_reel_events_and_the_reel_landing_page_is_time_only()
    {
        var d = Dash(Ev("feed", At(9, 0), 21, "facebook", "social_feed", url: "https://www.facebook.com"),
                     Ev("landing", At(9, 1), 4, "facebook", "reel", url: "https://www.facebook.com/reel"),
                     Ev("reel", At(9, 2), 18, "facebook", "reel", 1, url: "https://www.facebook.com/reel/*"));
        var fb = d.Platform("facebook");
        Assert.Equal(1, fb.Content["reels"]);
        Assert.Equal(43, fb.TotalSeconds);
    }

    [Fact]
    public void The_overall_total_and_other_sites_time_are_correct_and_content_time_is_not_added_twice()
    {
        var d = Dash(
            Ev("ig", At(9, 0), 100, "instagram", "reel", 1), Ev("yt", At(9, 10), 200, "youtube", "video", 1), Ev("tt", At(9, 20), 300, "tiktok", "short_video", 1),
            Ev("fb", At(9, 30), 400, "facebook", "reel", 1), Ev("docs", At(10, 0), 50), Ev("mail", At(10, 5), 25, "google", "messaging"));
        Assert.Equal(1075, d.TotalSeconds);
        Assert.Equal(75, d.OtherPlatformsSeconds);
        Assert.Equal([100.0, 200, 300, 400], d.Platforms.Select(p => p.TotalSeconds).ToArray());
        Assert.Equal(d.TotalSeconds, d.Platforms.Sum(p => p.TotalSeconds) + d.OtherPlatformsSeconds);
    }

    [Fact]
    public void Overlapping_events_count_a_moment_once()
    {
        var d = Dash(Ev("page", At(10, 0), 1800, "youtube", "webpage"), Ev("v", At(10, 5), 900, "youtube", "video", 1));
        Assert.Equal(1800, d.Platform("youtube").TotalSeconds);
        Assert.Equal(1800, d.TotalSeconds);
        Assert.Equal(1, d.Platform("youtube").Content["videos"]);
    }

    [Fact]
    public void Events_of_other_days_are_left_out_and_an_event_across_midnight_counts_only_its_part_of_today()
    {
        var d = Dash(
            Ev("yesterday", T(2026, 10, 30, 9), 500, "instagram", "reel", 1),
            Ev("tomorrow", T(2026, 11, 1, 9), 700, "instagram", "reel", 1),
            Ev("today", At(9), 60, "instagram", "reel", 1),
            Ev("late", T(2026, 10, 30, 23, 59), 180, "youtube", "webpage"),             // 23:59 yesterday to 00:02 today: 120 s today
            Ev("goes-on", At(23, 59), 180, "youtube", "webpage"));                      // 23:59 today to 00:02 tomorrow: 60 s today
        Assert.Equal(60, d.Platform("instagram").TotalSeconds);
        Assert.Equal(1, d.Platform("instagram").Content["reels"]);
        Assert.Equal(120 + 60, d.Platform("youtube").TotalSeconds);
        Assert.Equal(240, d.TotalSeconds);
    }

    // ---- "today" follows the clock and the zone, nothing is stored ----------------------------------------------------------------------------

    [Fact]
    public void Today_follows_the_clock_in_the_local_zone_and_new_events_show_up_in_the_next_request()
    {
        using var f = new Fixture(T(2026, 10, 31, 12));
        f.Store.Append([Ev("a", At(9), 600, "instagram", "reel", 1)]);
        Assert.Equal((Today, 600.0, 1), (f.Service.GetTodayDashboard().Date, f.Service.GetTodayDashboard().TotalSeconds, f.Service.GetTodayDashboard().Platform("instagram").Content["reels"]));

        f.Store.Append([Ev("b", At(10), 300, "instagram", "reel", 1)]);
        Assert.Equal((900.0, 2), (f.Service.GetTodayDashboard().TotalSeconds, f.Service.GetTodayDashboard().Platform("instagram").Content["reels"]));

        f.Now = T(2026, 11, 1, 0, 0, 1);                                                // a new day: yesterday's events are no longer today's
        var next = f.Service.GetTodayDashboard();
        Assert.Equal((new DateOnly(2026, 11, 1), 0.0), (next.Date, next.TotalSeconds));
    }

    [Fact]
    public void Today_uses_the_local_date_not_the_UTC_date()
    {
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("test+5", TimeSpan.FromHours(5), "test+5", "test+5");
        using var f = new Fixture(T(2026, 10, 31, 20), plus5);                          // 20:00 UTC = 01:00 on 1 November at +05:00
        f.Store.Append([Ev("lateUtc", T(2026, 10, 31, 19, 30), 600), Ev("beforeMidnightLocal", T(2026, 10, 31, 18, 30), 600)]);   // 00:30 local (today) and 23:30 local (yesterday)
        var d = f.Service.GetTodayDashboard();
        Assert.Equal(new DateOnly(2026, 11, 1), d.Date);
        Assert.Equal(600, d.TotalSeconds);
    }

    [Fact]
    public void Asking_for_the_dashboard_stores_nothing_and_the_schema_is_unchanged()
    {
        using var f = new Fixture(T(2026, 10, 31, 12));
        f.Store.Append([Ev("a", At(9), 600, "youtube", "video", 1)]);
        var before = f.Store.Count();
        f.Service.GetTodayDashboard();
        Assert.Equal(before, f.Store.Count());
        Assert.Equal(4, SqliteEventStore.SchemaVersion);                                // no new version, no new column, no new table
        var columns = f.Store.Read().Select(l => JsonDocument.Parse(l).RootElement.EnumerateObject().Select(p => p.Name).ToArray()).First();
        Assert.DoesNotContain(columns, c => c.Contains("total_time", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the response ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_json_has_the_dashboard_shape_and_none_of_the_private_event_fields()
    {
        var d = Dash(Ev("secret-event-id", At(9), 600, "youtube", "video", 1, url: "https://www.youtube.com/watch"),
                     Ev("fb", At(9, 20), 100, "facebook", "reel", 1), Ev("other", At(10), 25));
        var text = ReportJson.Serialize(d);
        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["date", "totalSeconds", "platforms", "otherPlatformsSeconds"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("2026-10-31", root.GetProperty("date").GetString());
        Assert.Equal(725, root.GetProperty("totalSeconds").GetDouble());
        Assert.Equal(25, root.GetProperty("otherPlatformsSeconds").GetDouble());
        var platforms = root.GetProperty("platforms");
        Assert.Equal(["instagram", "youtube", "tiktok", "facebook"], platforms.EnumerateArray().Select(p => p.GetProperty("platform").GetString()!).ToArray());
        Assert.Equal(["platform", "totalSeconds", "content"], platforms[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, platforms[1].GetProperty("content").GetProperty("videos").GetInt32());
        Assert.Equal(0, platforms[1].GetProperty("content").GetProperty("shorts").GetInt32());
        Assert.Equal(1, platforms[3].GetProperty("content").GetProperty("reels").GetInt32());
        foreach (var banned in new[] { "secret-event-id", "youtube.com", "http", "url", "title", "session", "source", "extension", "\"id\"", "app", "idle", "presence", "duration", "start", "end", "activity", "extra", "S1" })
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the route -------------------------------------------------------------------------------------------------------------------------

    private static string Header(string raw) => raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

    private static UiAccess NewAccess() => new("D4shD4shD4shD4shD4shD4shD4shD4shD4shD4s", Path.Combine(Path.GetTempPath(), "never-written.token"));

    private static StatusAnswer AsUi(string method, string path, UiAccess? ui, string? token, IReportService reports, IStatusSource? status = null, string? origin = null) =>
        LocalStatusLink.Evaluate(Header(LinkTestData.Request(Port, method, path, origin: origin, extra: token is null ? "" : $"{UiAccess.TokenHeader}: {token}\r\n")),
            Config, status ?? Agreed, reports: reports, ui: ui);

    private static StatusAnswer AsExtension(string method, string path, IReportService reports, bool withOrigin, UiAccess? ui = null)
    {
        var extra = withOrigin ? "" : $"{LocalStatusLink.ExtensionIdHeader}: {LinkTestData.ChromeId}\r\n";
        return LocalStatusLink.Evaluate(Header(LinkTestData.Request(Port, method, path, origin: withOrigin ? "ORIGIN" : null, extra: extra)), Config, Agreed, reports: reports, ui: ui);
    }

    [Fact]
    public void The_UI_token_reads_the_dashboard_with_a_real_calculated_body_and_no_cors()
    {
        using var f = new Fixture(T(2026, 10, 31, 12));
        f.Store.Append([Ev("a", At(9), 195, "instagram", "reel", 1)]);
        var ui = NewAccess();
        var answer = AsUi("GET", "/dashboard/today", ui, ui.Token, f.Service);
        Assert.Equal(200, answer.Status);
        Assert.Null(answer.CorsOrigin);
        var json = JsonDocument.Parse(answer.Json!).RootElement;
        Assert.Equal("2026-10-31", json.GetProperty("date").GetString());
        Assert.Equal(195, json.GetProperty("platforms")[0].GetProperty("totalSeconds").GetDouble());
        Assert.Equal(1, json.GetProperty("platforms")[0].GetProperty("content").GetProperty("reels").GetInt32());
        Assert.Equal(answer.Json, ReportJson.Serialize(f.Service.GetTodayDashboard()));
    }

    [Fact]
    public void A_missing_wrong_or_unconfigured_token_gets_403_and_the_dashboard_is_never_calculated()
    {
        var calls = 0;
        var spy = new SpyReports(() => calls++);
        var ui = NewAccess();
        Assert.Equal(200, AsUi("GET", "/dashboard/today", ui, ui.Token, spy).Status);                    // control: the right token does reach it
        Assert.Equal(1, calls);
        calls = 0;
        Assert.Equal(403, AsUi("GET", "/dashboard/today", ui, null, spy).Status);
        Assert.Equal(403, AsUi("GET", "/dashboard/today", ui, "wrong", spy).Status);
        Assert.Equal(403, AsUi("GET", "/dashboard/today", ui, ui.Token[..^1], spy).Status);
        Assert.Equal(403, AsUi("GET", "/dashboard/today", null, ui.Token, spy).Status);
        Assert.Equal(403, AsUi("GET", "/dashboard/today", ui, ui.Token, spy, origin: "https://evil.example").Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Only_GET_on_exactly_this_path_works_for_the_UI()
    {
        var ui = NewAccess();
        var spy = new SpyReports(() => { });
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" })
            Assert.Equal(403, AsUi(method, "/dashboard/today", ui, ui.Token, spy).Status);
        foreach (var path in new[] { "/dashboard", "/dashboard/", "/dashboard/today/", "/dashboard/yesterday", "/dashboard/today?date=2026-10-01", "/dashboard/2026-10-31", "/Dashboard/today", "/dashboard/today/extra" })
            Assert.Equal(403, AsUi("GET", path, ui, ui.Token, spy).Status);
    }

    [Fact]
    public void Consent_and_tracking_are_required_for_the_dashboard()
    {
        var ui = NewAccess();
        var spy = new SpyReports(() => { });
        foreach (var status in new[] { new AgentStatus(false, false, 0), new AgentStatus(true, false, 2), new AgentStatus(false, true, 2) })
            Assert.Equal(403, AsUi("GET", "/dashboard/today", ui, ui.Token, spy, new FixedStatus(status)).Status);
    }

    [Fact]
    public void The_extension_does_not_get_the_dashboard_and_keeps_everything_it_had()
    {
        var spy = new SpyReports(() => { });
        var ui = NewAccess();
        foreach (var withOrigin in new[] { true, false })
        {
            Assert.Equal(403, AsExtension("GET", "/dashboard/today", spy, withOrigin, ui).Status);
            Assert.Equal(200, AsExtension("GET", "/status", spy, withOrigin, ui).Status);
            Assert.Equal(200, AsExtension("GET", "/reports/weekly/current", spy, withOrigin, ui).Status);
        }
    }

    [Fact]
    public void The_UI_token_still_reads_the_four_report_routes_and_still_cannot_reach_status_or_events()
    {
        using var f = new Fixture(T(2026, 10, 31, 12));
        var ui = NewAccess();
        foreach (var path in new[] { "/reports/weekly/current", "/reports/weekly/previous", "/reports/monthly/current", "/reports/monthly/previous" })
        {
            var answer = AsUi("GET", path, ui, ui.Token, f.Service);
            Assert.Equal(200, answer.Status);
            Assert.Contains("\"firstDay\"", answer.Json);
        }
        Assert.Equal(403, AsUi("GET", "/status", ui, ui.Token, f.Service).Status);
        Assert.Equal(403, AsUi("POST", "/events", ui, ui.Token, f.Service).Status);
    }

    private sealed class SpyReports(Action onDashboard) : IReportService
    {
        private static ActivityReport Any() => ReportGenerator.Build(ReportPeriod.Week(new DateOnly(2026, 10, 26), TimeZoneInfo.Utc), [], new DateOnly(2026, 10, 31));
        public ActivityReport GetCurrentWeeklyReport() => Any();
        public ActivityReport GetPreviousWeeklyReport() => Any();
        public ActivityReport GetCurrentMonthlyReport() => Any();
        public ActivityReport GetPreviousMonthlyReport() => Any();
        public DashboardToday GetTodayDashboard() { onDashboard(); return DashboardToday.From(ReportGenerator.Build(ReportPeriod.Day(Today, TimeZoneInfo.Utc), [], Today)); }
    }

    private sealed class NullSink : IEventSink
    {
        public bool AppendEvent(string jsonLine) => true;
        public bool AppendEvents(IReadOnlyList<string> jsonLines) => true;
    }

    [Fact]
    public async Task Over_a_real_socket_the_dashboard_answers_the_UI_with_no_store_and_refuses_everyone_else()
    {
        using var f = new Fixture(T(2026, 10, 31, 12));
        f.Store.Append([Ev("a", At(9), 300, "youtube", "short_video", 1)]);
        using var dir = new TempData();
        var ui = UiAccess.Create(dir.DataDir)!;
        var port = LinkTestData.FreePort();
        using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), Agreed, sink: new NullSink(), reports: f.Service, ui: ui);
        Assert.True(link.Start());

        async Task<string> Send(string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        var ok = await Send(LinkTestData.Request(port, "GET", "/dashboard/today", origin: null, extra: $"{UiAccess.TokenHeader}: {ui.Token}\r\n"));
        Assert.StartsWith("HTTP/1.1 200 OK", ok);
        Assert.Contains("Cache-Control: no-store", ok);
        Assert.DoesNotContain("Access-Control-Allow-Origin", ok);
        var body = JsonDocument.Parse(ok[(ok.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).RootElement;
        Assert.Equal(1, body.GetProperty("platforms")[1].GetProperty("content").GetProperty("shorts").GetInt32());
        Assert.Equal(300, body.GetProperty("totalSeconds").GetDouble());

        Assert.StartsWith("HTTP/1.1 403", await Send(LinkTestData.Request(port, "GET", "/dashboard/today", origin: null)));
        Assert.StartsWith("HTTP/1.1 403", await Send(LinkTestData.Request(port, "POST", "/dashboard/today", origin: null, extra: $"{UiAccess.TokenHeader}: {ui.Token}\r\nContent-Length: 0\r\n")));
        Assert.StartsWith("HTTP/1.1 403", await Send(LinkTestData.Request(port, "GET", "/dashboard/today")));     // the extension's own origin
    }
}
