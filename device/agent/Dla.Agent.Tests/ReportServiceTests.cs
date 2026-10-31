using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Reports;
using Dla.Agent.Storage;
using Microsoft.Data.Sqlite;
using static Dla.Agent.Tests.ReportTests;

namespace Dla.Agent.Tests;

/// <summary>
/// The application-facing report operations (ReportService) and the read-only routes on the local link
/// (GET /reports/weekly|monthly/current|previous). "Now" is a clock the test moves; no test reads the real date.
/// 2026-10-26 is a Monday.
/// </summary>
public class ReportServiceTests
{
    private const int Port = 49721;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly FixedStatus Agreed = new(new AgentStatus(true, true, 2));

    private sealed class Fixture : IDisposable
    {
        private readonly TempData _dir = new();
        public DateTimeOffset Now;
        public SqliteEventStore Store { get; }
        public ReportService Service { get; }
        public string DataDir => _dir.DataDir;
        public string DbPath => Path.Combine(_dir.DataDir, "dla.db");

        public Fixture(DateTimeOffset now)
        {
            Now = now;
            Directory.CreateDirectory(_dir.DataDir);
            Store = SqliteEventStore.Open(DbPath, () => Now);
            Service = new ReportService(new ReportGenerator(Store, Utc, () => Now));
        }

        public void Dispose() { Store.Dispose(); _dir.Dispose(); }
    }

    private sealed class ThrowingReports : IReportService
    {
        public ActivityReport GetCurrentWeeklyReport() => throw new InvalidOperationException("SECRET sqlite path C:\\Users\\x\\dla.db");
        public ActivityReport GetPreviousWeeklyReport() => GetCurrentWeeklyReport();
        public ActivityReport GetCurrentMonthlyReport() => GetCurrentWeeklyReport();
        public ActivityReport GetPreviousMonthlyReport() => GetCurrentWeeklyReport();
        public DashboardToday GetTodayDashboard() => throw new InvalidOperationException("SECRET sqlite path");
    }

    /// <summary>The request line and headers without the final blank line, which is what the link's decision function is given.</summary>
    private static string Header(string raw) => raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

    private static StatusAnswer Get(string path, IReportService? reports, IStatusSource? status = null, IAgentLog? log = null, string method = "GET") =>
        LocalStatusLink.Evaluate(Header(LinkTestData.Request(Port, method, path)), LinkTestData.Config(Port, LinkTestData.ChromeId), status ?? Agreed, reports: reports, log: log);

    private static JsonElement Json(StatusAnswer answer)
    {
        Assert.Equal(200, answer.Status);
        return JsonDocument.Parse(answer.Json!).RootElement.Clone();
    }

    // ---- the four operations take no date ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Current_weekly_report_is_asked_for_without_a_date_and_has_seven_days_and_a_status()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("a", T(2026, 10, 27, 9), 600), Ev("b", T(2026, 10, 29, 9), 300)]);
        var r = f.Service.GetCurrentWeeklyReport();
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), ReportStatus.InProgress), (r.FirstDay, r.LastDay, r.Status));
        Assert.Equal(7, r.Days.Count);
        Assert.Equal(900, r.TotalSeconds);
        Assert.Equal(ReportKind.Weekly, r.Kind);
    }

    [Fact]
    public void Previous_weekly_report_is_the_completed_week_before_without_a_date()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("old", T(2026, 10, 21, 9), 1200), Ev("now", T(2026, 10, 27, 9), 600)]);
        var r = f.Service.GetPreviousWeeklyReport();
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), ReportStatus.Completed), (r.FirstDay, r.LastDay, r.Status));
        Assert.Equal(1200, r.TotalSeconds);
        Assert.Equal(1200, r.Days[2].TotalSeconds);                    // Wednesday 21 October
    }

    [Fact]
    public void Current_monthly_report_has_the_weekly_buckets_and_previous_monthly_report_is_completed()
    {
        using var f = new Fixture(T(2026, 11, 10, 12));
        f.Store.Append([Ev("oct", T(2026, 10, 30, 9), 700), Ev("nov1", T(2026, 11, 2, 9), 100), Ev("nov2", T(2026, 11, 9, 9), 200)]);
        var current = f.Service.GetCurrentMonthlyReport();
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), ReportStatus.InProgress), (current.FirstDay, current.LastDay, current.Status));
        Assert.Equal([1, 2, 3, 4, 5], current.Weeks.Select(w => w.Number).ToArray());
        Assert.Equal([100.0, 200, 0, 0, 0], current.Weeks.Select(w => w.TotalSeconds).ToArray());
        var previous = f.Service.GetPreviousMonthlyReport();
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));
        Assert.Equal(700, previous.TotalSeconds);
    }

    // ---- the period follows the clock -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Current_and_previous_week_move_on_by_themselves_when_the_clock_enters_a_new_week()
    {
        using var f = new Fixture(T(2026, 11, 1, 23, 59, 59));
        Assert.Equal(new DateOnly(2026, 10, 26), f.Service.GetCurrentWeeklyReport().FirstDay);
        f.Now = T(2026, 11, 2, 0, 0, 1);
        var current = f.Service.GetCurrentWeeklyReport();
        var previous = f.Service.GetPreviousWeeklyReport();
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), ReportStatus.InProgress), (current.FirstDay, current.LastDay, current.Status));
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));
    }

    [Fact]
    public void Current_month_moves_on_by_itself_and_previous_month_crosses_the_year_boundary()
    {
        using var f = new Fixture(T(2026, 12, 31, 23, 59));
        Assert.Equal(new DateOnly(2026, 12, 1), f.Service.GetCurrentMonthlyReport().FirstDay);
        f.Now = T(2027, 1, 1, 0, 1);
        var current = f.Service.GetCurrentMonthlyReport();
        var previous = f.Service.GetPreviousMonthlyReport();
        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31), ReportStatus.InProgress), (current.FirstDay, current.LastDay, current.Status));
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), ReportStatus.Completed), (previous.FirstDay, previous.LastDay, previous.Status));
    }

    [Fact]
    public void New_events_appear_in_the_next_request_and_nothing_is_stored_for_reports()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("a", T(2026, 10, 27, 9), 600, "youtube", "webpage")]);
        var tablesBefore = Tables(f.DbPath);
        var filesBefore = Directory.GetFileSystemEntries(f.DataDir).Select(Path.GetFileName).Order().ToArray();
        var countBefore = f.Store.Count();

        Assert.Equal(600, f.Service.GetCurrentWeeklyReport().TotalSeconds);
        f.Service.GetPreviousWeeklyReport(); f.Service.GetCurrentMonthlyReport(); f.Service.GetPreviousMonthlyReport();
        Assert.Equal(countBefore, f.Store.Count());                      // asking for reports adds no rows
        Assert.Equal(tablesBefore, Tables(f.DbPath));                    // and no tables
        Assert.Equal(filesBefore, Directory.GetFileSystemEntries(f.DataDir).Select(Path.GetFileName).Order().ToArray());

        f.Store.Append([Ev("b", T(2026, 10, 29, 9), 900, "youtube", "video", 1)]);
        var next = f.Service.GetCurrentWeeklyReport();
        Assert.Equal(1500, next.TotalSeconds);
        Assert.Equal(1500, f.Service.GetCurrentMonthlyReport().TotalSeconds);
        Assert.Equal(tablesBefore, Tables(f.DbPath));                    // events were the only thing that grew
    }

    private static string[] Tables(string dbPath)
    {
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var rd = cmd.ExecuteReader();
        var names = new List<string>();
        while (rd.Read()) names.Add(rd.GetString(0));
        return names.ToArray();
    }

    // ---- the routes -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_four_report_routes_answer_200_and_need_no_date_or_query()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("a", T(2026, 10, 27, 9), 600, "instagram", "reel", 1)]);

        var weekly = Json(Get("/reports/weekly/current", f.Service));
        Assert.Equal("2026-10-26", weekly.GetProperty("firstDay").GetString());
        Assert.Equal("2026-11-01", weekly.GetProperty("lastDay").GetString());
        Assert.Equal("InProgress", weekly.GetProperty("status").GetString());
        Assert.Equal("Weekly", weekly.GetProperty("kind").GetString());
        Assert.Equal(600, weekly.GetProperty("totalSeconds").GetDouble());
        Assert.Equal(7, weekly.GetProperty("days").GetArrayLength());
        Assert.Equal("Monday", weekly.GetProperty("days")[0].GetProperty("dayOfWeek").GetString());
        Assert.Equal("Sunday", weekly.GetProperty("days")[6].GetProperty("dayOfWeek").GetString());
        Assert.Equal("2026-10-27", weekly.GetProperty("days")[1].GetProperty("date").GetString());
        Assert.Equal(600, weekly.GetProperty("days")[1].GetProperty("totalSeconds").GetDouble());
        Assert.Equal(0, weekly.GetProperty("weeks").GetArrayLength());
        Assert.Equal(4, weekly.GetProperty("platforms").GetArrayLength());
        Assert.Equal(600, weekly.GetProperty("platforms")[0].GetProperty("seconds").GetDouble());       // instagram
        Assert.Equal(5, weekly.GetProperty("content").GetArrayLength());
        Assert.Equal(1, weekly.GetProperty("content")[0].GetProperty("count").GetInt32());

        var previousWeek = Json(Get("/reports/weekly/previous", f.Service));
        Assert.Equal(("2026-10-19", "Completed"), (previousWeek.GetProperty("firstDay").GetString(), previousWeek.GetProperty("status").GetString()));

        var monthly = Json(Get("/reports/monthly/current", f.Service));
        Assert.Equal(("2026-10-01", "2026-10-31", "InProgress", "Monthly"),
            (monthly.GetProperty("firstDay").GetString(), monthly.GetProperty("lastDay").GetString(), monthly.GetProperty("status").GetString(), monthly.GetProperty("kind").GetString()));
        Assert.Equal(5, monthly.GetProperty("weeks").GetArrayLength());                                // 31 days: 7+7+7+7+3
        Assert.Equal(600, monthly.GetProperty("weeks")[3].GetProperty("totalSeconds").GetDouble());    // 22-28 October holds the 27th
        Assert.Equal(1, monthly.GetProperty("weeks")[0].GetProperty("number").GetInt32());

        var previousMonth = Json(Get("/reports/monthly/previous", f.Service));
        Assert.Equal(("2026-09-01", "Completed"), (previousMonth.GetProperty("firstDay").GetString(), previousMonth.GetProperty("status").GetString()));
    }

    [Fact]
    public void The_response_has_report_data_only_no_event_rows_urls_or_session_ids()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("secret-event-id", T(2026, 10, 27, 9), 600, "youtube", "video", 1, url: "https://www.youtube.com/watch")]);
        var text = Get("/reports/weekly/current", f.Service).Json!;
        foreach (var banned in new[] { "secret-event-id", "youtube.com", "https://", "S1", "sessionId", "session_id", "url", "title", "source", "\"id\"", "extension", "app", "idle", "presence" })
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Routes_follow_the_clock_and_cross_the_year_boundary()
    {
        using var f = new Fixture(T(2027, 1, 15, 12));
        var previous = Json(Get("/reports/monthly/previous", f.Service));
        Assert.Equal(("2026-12-01", "2026-12-31", "Completed"), (previous.GetProperty("firstDay").GetString(), previous.GetProperty("lastDay").GetString(), previous.GetProperty("status").GetString()));
        Assert.Equal("2027-01-01", Json(Get("/reports/monthly/current", f.Service)).GetProperty("firstDay").GetString());

        f.Now = T(2026, 11, 2, 0, 0, 1);
        Assert.Equal("2026-11-02", Json(Get("/reports/weekly/current", f.Service)).GetProperty("firstDay").GetString());
        Assert.Equal("Completed", Json(Get("/reports/weekly/previous", f.Service)).GetProperty("status").GetString());
    }

    [Fact]
    public void New_events_show_up_in_the_next_route_request()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        Assert.Equal(0, Json(Get("/reports/weekly/current", f.Service)).GetProperty("totalSeconds").GetDouble());
        f.Store.Append([Ev("a", T(2026, 10, 28, 9), 300)]);
        Assert.Equal(300, Json(Get("/reports/weekly/current", f.Service)).GetProperty("totalSeconds").GetDouble());
    }

    [Fact]
    public void The_report_routes_are_guarded_like_every_other_route()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        // Before the user agreed, or with tracking off: no report.
        Assert.Equal(403, Get("/reports/weekly/current", f.Service, new FixedStatus(new AgentStatus(false, false, 0))).Status);
        Assert.Equal(403, Get("/reports/weekly/current", f.Service, new FixedStatus(new AgentStatus(true, false, 2))).Status);
        // Read-only, fixed paths only: no other method, no date in the path, no extra path.
        Assert.Equal(403, Get("/reports/weekly/current", f.Service, method: "POST").Status);
        Assert.Equal(403, Get("/reports/weekly/2026-10-26", f.Service).Status);
        Assert.Equal(403, Get("/reports/weekly/current?start=2026-01-01", f.Service).Status);
        Assert.Equal(403, Get("/reports/yearly/current", f.Service).Status);
        Assert.Equal(403, Get("/reports", f.Service).Status);
        // An unknown caller (no allowlisted extension, wrong host) is refused before any report logic runs.
        var calls = 0;
        var counting = new CountingReports(() => calls++);
        // Control: the same request from the allowlisted caller does reach the report logic, so the zero below means the guard worked.
        Get("/reports/weekly/current", counting);
        Assert.Equal(1, calls);
        calls = 0;
        var stranger = LocalStatusLink.Evaluate(Header(LinkTestData.Request(Port, "GET", "/reports/weekly/current", origin: null)), LinkTestData.Config(Port, LinkTestData.ChromeId), Agreed, reports: counting);
        var wrongHost = LocalStatusLink.Evaluate(Header(LinkTestData.Request(Port, "GET", "/reports/weekly/current", host: "evil.test")), LinkTestData.Config(Port, LinkTestData.ChromeId), Agreed, reports: counting);
        Assert.Equal((403, 403, 0), (stranger.Status, wrongHost.Status, calls));
    }

    private sealed class CountingReports(Action onCall) : IReportService
    {
        public ActivityReport GetCurrentWeeklyReport() { onCall(); throw new InvalidOperationException(); }
        public ActivityReport GetPreviousWeeklyReport() => GetCurrentWeeklyReport();
        public ActivityReport GetCurrentMonthlyReport() => GetCurrentWeeklyReport();
        public ActivityReport GetPreviousMonthlyReport() => GetCurrentWeeklyReport();
        public DashboardToday GetTodayDashboard() { onCall(); throw new InvalidOperationException(); }
    }

    [Fact]
    public void A_failing_report_gives_a_plain_500_and_no_stack_trace_path_or_message()
    {
        var log = new FakeLog();
        var answer = Get("/reports/weekly/current", new ThrowingReports(), log: log);
        Assert.Equal(500, answer.Status);
        Assert.Equal("{\"error\":\"report_failed\"}", answer.Json);
        Assert.DoesNotContain("SECRET", answer.Json!);
        Assert.DoesNotContain("SECRET", string.Join("\n", log.Lines));      // the log holds the exception type only
        Assert.Single(log.Lines);
    }

    [Fact]
    public void Without_a_database_the_report_route_says_unavailable_instead_of_failing()
    {
        var answer = Get("/reports/monthly/current", reports: null);
        Assert.Equal((500, "{\"error\":\"reports_unavailable\"}"), (answer.Status, answer.Json));
    }

    // ---- one real HTTP request through the socket --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_real_local_request_for_the_current_week_returns_the_report_as_JSON()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("a", T(2026, 10, 27, 9), 600)]);
        var port = LinkTestData.FreePort();
        using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), Agreed, reports: f.Service, sink: new FakeSink());
        Assert.True(link.Start());

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(LinkTestData.Request(port, "GET", "/reports/weekly/current")));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync();

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Type: application/json", response);
        Assert.Contains("Cache-Control: no-store", response);
        var body = JsonDocument.Parse(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).RootElement;
        Assert.Equal((600, 7, "InProgress"), (body.GetProperty("totalSeconds").GetInt32(), body.GetProperty("days").GetArrayLength(), body.GetProperty("status").GetString()));
    }

    private sealed class FakeSink : IEventSink
    {
        public bool AppendEvent(string jsonLine) => true;
        public bool AppendEvents(IReadOnlyList<string> jsonLines) => true;
    }

    // ---- the calculations behind it are unchanged -------------------------------------------------------------------------------------------

    [Fact]
    public void The_service_returns_exactly_what_the_report_generator_calculates()
    {
        using var f = new Fixture(T(2026, 10, 29, 12));
        f.Store.Append([Ev("a", T(2026, 10, 27, 9), 600, "youtube", "webpage"), Ev("b", T(2026, 10, 27, 10), 900, "youtube", "video", 1),
                        Ev("c", T(2026, 10, 28, 10), 40, "facebook", "reel", url: "https://www.facebook.com/reel")]);
        var direct = new ReportGenerator(f.Store, Utc, () => f.Now).GetCurrentWeeklyReport();
        var viaService = f.Service.GetCurrentWeeklyReport();
        Assert.Equal(ReportJson.Serialize(direct), ReportJson.Serialize(viaService));
        Assert.Equal(1540, viaService.TotalSeconds);
        Assert.Equal(1500, viaService.PlatformTotal("youtube").Seconds);
        Assert.Equal((0, 0.0), (viaService.ContentStat("facebook", "reel").Count, viaService.ContentStat("facebook", "reel").WatchSeconds));   // landing: platform time only
    }
}
