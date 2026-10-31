using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Reports;
using Dla.Agent.Storage;
using static Dla.Agent.Tests.ReportTests;

namespace Dla.Agent.Tests;

/// <summary>
/// The separate UI reads reports with a secret from ui-access.token (header X-DLA-UI-Token). The secret opens the four report GET routes
/// and nothing else; the extension keeps exactly the access it had. 2026-10-26 is a Monday.
/// </summary>
public class UiAccessTests
{
    private const int Port = 49721;
    private static readonly AgentLinkConfig Config = LinkTestData.Config(Port, LinkTestData.ChromeId);
    private static readonly FixedStatus Agreed = new(new AgentStatus(true, true, 2));
    private static readonly string[] ReportPaths =
        ["/reports/weekly/current", "/reports/weekly/previous", "/reports/monthly/current", "/reports/monthly/previous"];

    private sealed class FakeReports : IReportService
    {
        private static ActivityReport One(ReportKind kind) => ReportGenerator.Build(
            kind == ReportKind.Weekly ? ReportPeriod.Week(new DateOnly(2026, 10, 26), TimeZoneInfo.Utc) : ReportPeriod.Month(2026, 10, TimeZoneInfo.Utc),
            [Ev("a", T(2026, 10, 27, 9), 600)], new DateOnly(2026, 10, 29));
        public ActivityReport GetCurrentWeeklyReport() => One(ReportKind.Weekly);
        public ActivityReport GetPreviousWeeklyReport() => One(ReportKind.Weekly);
        public ActivityReport GetCurrentMonthlyReport() => One(ReportKind.Monthly);
        public ActivityReport GetPreviousMonthlyReport() => One(ReportKind.Monthly);
        public DashboardToday GetTodayDashboard() => DashboardToday.From(ReportGenerator.Build(ReportPeriod.Day(new DateOnly(2026, 10, 27), TimeZoneInfo.Utc), [Ev("a", T(2026, 10, 27, 9), 600)], new DateOnly(2026, 10, 27)));
    }

    private sealed class RecordingSink : IEventSink
    {
        public int Appended;
        public bool AppendEvent(string jsonLine) { Appended++; return true; }
        public bool AppendEvents(IReadOnlyList<string> jsonLines) { Appended += jsonLines.Count; return true; }
    }

    private static string Header(string raw) => raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

    private static UiAccess NewAccess() => new("T0kenT0kenT0kenT0kenT0kenT0kenT0kenT0kenT0k", Path.Combine(Path.GetTempPath(), "never-written.token"));

    /// <summary>A request as the UI sends it: no Origin, the secret in its header.</summary>
    private static StatusAnswer AsUi(string method, string path, UiAccess? ui, string? token, IReportService? reports = null, IStatusSource? status = null,
        string? body = null, IEventSink? sink = null, string extra = "", string? origin = null, string? host = "HOST")
    {
        var headers = (token is null ? "" : $"{UiAccess.TokenHeader}: {token}\r\n") + extra;
        var raw = LinkTestData.Request(Port, method, path, origin: origin, host: host, extra: headers + (body is null ? "" : $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"));
        return LocalStatusLink.Evaluate(Header(raw), Config, status ?? Agreed, body, sink, reports: reports ?? new FakeReports(), ui: ui);
    }

    private static StatusAnswer AsExtension(string method, string path, string? body = null, IEventSink? sink = null, bool withOrigin = true, UiAccess? ui = null, IStatusSource? status = null)
    {
        var extra = (withOrigin ? "" : $"{LocalStatusLink.ExtensionIdHeader}: {LinkTestData.ChromeId}\r\n") + (body is null ? "" : $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        var raw = LinkTestData.Request(Port, method, path, origin: withOrigin ? "ORIGIN" : null, extra: extra);
        return LocalStatusLink.Evaluate(Header(raw), Config, status ?? Agreed, body, sink, reports: new FakeReports(), ui: ui);
    }

    private static string ValidEvent => Ev("evt-1", T(2026, 10, 27, 9), 60, "youtube", "video", 1);

    // ---- the secret ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_new_random_secret_is_written_for_the_UI_each_time_and_removed_when_the_link_stops()
    {
        using var dir = new TempData();
        var first = UiAccess.Create(dir.DataDir)!;
        Assert.Equal(first.Token, File.ReadAllText(UiAccess.FilePathIn(dir.DataDir)));
        Assert.Equal(43, first.Token.Length);                                  // 256 bits, URL-safe
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Token);

        var second = UiAccess.Create(dir.DataDir)!;                            // a restart of the agent
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(first.Matches(second.Token));
        Assert.True(second.Matches(second.Token));
        Assert.False(File.Exists(UiAccess.FilePathIn(dir.DataDir) + ".tmp"));

        second.Remove();
        Assert.False(File.Exists(UiAccess.FilePathIn(dir.DataDir)));
    }

    [Fact]
    public void Matching_needs_exactly_the_secret()
    {
        var ui = NewAccess();
        Assert.True(ui.Matches(ui.Token));
        foreach (var wrong in new[] { "", " ", ui.Token[..^1], ui.Token + "x", ui.Token.ToLowerInvariant(), ui.Token.ToUpperInvariant(), new string('A', 43), ui.Token[..42] + "Z" })
            Assert.False(ui.Matches(wrong), wrong);
    }

    [Fact]
    public void When_the_secret_cannot_be_written_there_is_no_UI_access_and_nothing_throws()
    {
        using var dir = new TempData();
        var blocker = Path.Combine(dir.Root, "blocked");
        File.WriteAllText(blocker, "a file where the data folder should be");
        var log = new FakeLog();
        Assert.Null(UiAccess.Create(blocker, log));
        Assert.Single(log.Lines);
        Assert.DoesNotContain(blocker, log.Lines[0]);
    }

    // ---- the UI can read the four report routes ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/reports/weekly/current")]
    [InlineData("/reports/weekly/previous")]
    [InlineData("/reports/monthly/current")]
    [InlineData("/reports/monthly/previous")]
    public void The_UI_can_read_each_of_the_four_report_routes(string path)
    {
        var ui = NewAccess();
        var answer = AsUi("GET", path, ui, ui.Token);
        Assert.Equal(200, answer.Status);
        Assert.Null(answer.CorsOrigin);                                        // not a browser caller: no CORS header is ever given
        var json = JsonDocument.Parse(answer.Json!).RootElement;
        Assert.Equal(path.Contains("weekly") ? "Weekly" : "Monthly", json.GetProperty("kind").GetString());
        Assert.Equal(600, json.GetProperty("totalSeconds").GetDouble());
    }

    [Fact]
    public void The_UI_response_is_the_same_report_JSON_the_extension_route_returns_with_no_store_caching()
    {
        var ui = NewAccess();
        var viaUi = AsUi("GET", "/reports/weekly/current", ui, ui.Token);
        var viaExtension = AsExtension("GET", "/reports/weekly/current", ui: ui);
        Assert.Equal(viaExtension.Json, viaUi.Json);
    }

    // ---- the UI cannot do anything else ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_UI_secret_cannot_post_events_and_nothing_is_stored()
    {
        var ui = NewAccess();
        var sink = new RecordingSink();
        foreach (var path in new[] { "/events", "/reports/weekly/current" })
        {
            Assert.Equal(403, AsUi("POST", path, ui, ui.Token, body: ValidEvent, sink: sink).Status);
        }
        Assert.Equal(403, AsUi("OPTIONS", "/events", ui, ui.Token, sink: sink).Status);
        Assert.Equal(0, sink.Appended);
        // control: the same event from the extension is accepted and stored, so the zero above is the secret's doing
        Assert.Equal(200, AsExtension("POST", "/events", ValidEvent, sink).Status);
        Assert.Equal(1, sink.Appended);
    }

    [Fact]
    public void The_UI_secret_cannot_reach_status_or_any_unrelated_path_method_or_date_form()
    {
        var ui = NewAccess();
        foreach (var path in new[] { "/status", "/", "/events", "/reports", "/reports/", "/reports/weekly", "/reports/weekly/2026-10-26", "/reports/weekly/current/",
                                     "/reports/weekly/current?start=2026-01-01", "/reports/yearly/current", "/reports/../status", "/admin", "/ui-access.token" })
            Assert.Equal(403, AsUi("GET", path, ui, ui.Token).Status);
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" })
            Assert.Equal(403, AsUi(method, "/reports/weekly/current", ui, ui.Token).Status);
    }

    [Fact]
    public void A_wrong_missing_or_unconfigured_secret_gets_nothing()
    {
        var ui = NewAccess();
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, "wrong").Status);
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, "").Status);
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token + "0").Status);
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, null).Status);                // no secret and no extension: refused as before
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", null, ui.Token).Status);          // a link started without UI access ignores any secret
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, extra: $"{UiAccess.TokenHeader}: {ui.Token}\r\n").Status);   // repeated header
    }

    [Fact]
    public void The_secret_never_widens_through_other_headers_and_a_web_page_cannot_use_it()
    {
        var ui = NewAccess();
        // an extension id sent next to the secret does not turn it into extension access
        Assert.Equal(403, AsUi("GET", "/status", ui, ui.Token, extra: $"{LocalStatusLink.ExtensionIdHeader}: {LinkTestData.ChromeId}\r\n").Status);
        Assert.Equal(403, AsUi("POST", "/events", ui, ui.Token, body: ValidEvent, extra: $"{LocalStatusLink.ExtensionIdHeader}: {LinkTestData.ChromeId}\r\n", sink: new RecordingSink()).Status);
        // a browser always sends Origin: with it the secret is refused, even for an allowlisted extension origin
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, origin: "https://evil.example").Status);
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, origin: LinkTestData.ChromeOrigin).Status);
        // and the secret is not an extension id
        var raw = LinkTestData.Request(Port, "GET", "/status", origin: null, extra: $"{LocalStatusLink.ExtensionIdHeader}: {ui.Token}\r\n");
        Assert.Equal(403, LocalStatusLink.Evaluate(Header(raw), Config, Agreed, ui: ui).Status);
    }

    [Fact]
    public void Wrong_host_header_is_refused_for_the_UI_too()
    {
        var ui = NewAccess();
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, host: "evil.test").Status);          // DNS rebinding
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, host: "localhost:49721").Status);
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, host: "127.0.0.1:1").Status);
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, host: null).Status);
    }

    // ---- consent and tracking still apply --------------------------------------------------------------------------------------------------

    [Fact]
    public void Reports_need_consent_and_tracking_for_the_UI_exactly_as_for_the_extension()
    {
        var ui = NewAccess();
        foreach (var status in new[] { new AgentStatus(false, false, 0), new AgentStatus(true, false, 2), new AgentStatus(false, true, 2) })
        {
            foreach (var path in ReportPaths)
            {
                Assert.Equal(403, AsUi("GET", path, ui, ui.Token, status: new FixedStatus(status)).Status);
                Assert.Equal(403, AsExtension("GET", path, status: new FixedStatus(status)).Status);
            }
        }
        var live = new FixedStatus(new AgentStatus(false, false, 0));
        Assert.Equal(403, AsUi("GET", "/reports/weekly/current", ui, ui.Token, status: live).Status);
        live.Status = new AgentStatus(true, true, 2);                                    // the user agrees: the very next request works
        Assert.Equal(200, AsUi("GET", "/reports/weekly/current", ui, ui.Token, status: live).Status);
    }

    // ---- the extension keeps exactly what it had ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_extension_still_has_status_events_and_reports_and_the_UI_secret_changes_nothing_for_it()
    {
        var ui = NewAccess();
        foreach (var withOrigin in new[] { true, false })
        {
            Assert.Equal(200, AsExtension("GET", "/status", withOrigin: withOrigin, ui: ui).Status);
            foreach (var path in ReportPaths) Assert.Equal(200, AsExtension("GET", path, withOrigin: withOrigin, ui: ui).Status);
            var sink = new RecordingSink();
            Assert.Equal(200, AsExtension("POST", "/events", ValidEvent, sink, withOrigin, ui).Status);
            Assert.Equal(1, sink.Appended);
        }
        Assert.Equal(204, AsExtension("OPTIONS", "/events", ui: ui).Status);
        Assert.Equal(403, AsExtension("GET", "/elsewhere", ui: ui).Status);
        // and an unknown caller is still refused
        var raw = LinkTestData.Request(Port, "GET", "/status", origin: "https://evil.example");
        Assert.Equal(403, LocalStatusLink.Evaluate(Header(raw), Config, Agreed, ui: ui).Status);
    }

    // ---- real sockets: localhost only ------------------------------------------------------------------------------------------------------

    private static async Task<string> Raw(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Over_a_real_socket_the_UI_reads_reports_the_link_is_bound_to_127_0_0_1_and_stopping_removes_the_secret()
    {
        using var dir = new TempData();
        var ui = UiAccess.Create(dir.DataDir)!;
        var port = LinkTestData.FreePort();
        var config = LinkTestData.Config(port, LinkTestData.ChromeId);
        var sink = new RecordingSink();
        var link = new LocalStatusLink(config, Agreed, sink: sink, reports: new FakeReports(), ui: ui);
        try
        {
            Assert.True(link.Start());
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)link.Endpoint!).Address);

            var ok = await Raw(port, LinkTestData.Request(port, "GET", "/reports/monthly/previous", origin: null, extra: $"{UiAccess.TokenHeader}: {File.ReadAllText(ui.FilePath)}\r\n"));
            Assert.StartsWith("HTTP/1.1 200 OK", ok);
            Assert.Contains("Cache-Control: no-store", ok);
            Assert.DoesNotContain("Access-Control-Allow-Origin", ok);

            var post = await Raw(port, LinkTestData.Request(port, "POST", "/events", origin: null, extra: $"{UiAccess.TokenHeader}: {ui.Token}\r\nContent-Length: {Encoding.UTF8.GetByteCount(ValidEvent)}\r\n") + ValidEvent);
            Assert.StartsWith("HTTP/1.1 403", post);
            Assert.StartsWith("HTTP/1.1 403", await Raw(port, LinkTestData.Request(port, "GET", "/status", origin: null, extra: $"{UiAccess.TokenHeader}: {ui.Token}\r\n")));
            Assert.StartsWith("HTTP/1.1 403", await Raw(port, LinkTestData.Request(port, "GET", "/reports/weekly/current", origin: null)));          // no secret
            Assert.Equal(0, sink.Appended);

            // no other network address answers: connecting to this PC's own non-loopback addresses is refused
            foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                         .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                         .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
            {
                using var other = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Assert.ThrowsAnyAsync<Exception>(async () => await other.ConnectAsync(address, port, cts.Token));
            }
        }
        finally
        {
            link.Dispose();
        }
        Assert.False(File.Exists(UiAccess.FilePathIn(dir.DataDir)));                                    // the secret leaves with the agent
    }
}
