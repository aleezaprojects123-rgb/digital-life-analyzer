using System.Net;
using System.Text;
using Dla.Agent.Consent;
using Dla.Agent.Link;

namespace Dla.Agent.Tests;

internal sealed class FakeEventSink : IEventSink
{
    public List<string> Events { get; } = [];
    public bool AppendEvent(string jsonLine)
    {
        Events.Add(jsonLine);
        return true;
    }
}

public class EventsEndpointEvaluateTests
{
    private const int Port = 49999;
    private static readonly AgentLinkConfig Config = LinkTestData.Config(Port, LinkTestData.ChromeId);
    private static readonly FixedStatus Status = new(new AgentStatus(true, true, 1));

    private static string SampleEvent(
        string id = "evt-1",
        string url = "https://example.com/test",
        int duration = 15,
        string domain = "example.com",
        string source = "extension",
        string schemaVersion = "1")
    {
        return $$"""
        {
          "schema_version": "{{schemaVersion}}",
          "id": "{{id}}",
          "start": "2026-10-27T05:15:00Z",
          "end": "2026-10-27T05:15:15Z",
          "duration_seconds": {{duration}},
          "source": "{{source}}",
          "domain": "{{domain}}",
          "url": "{{url}}",
          "title": "Example Domain",
          "presence": null,
          "category": null,
          "confidence": null,
          "uncertain": false,
          "label_source": null
        }
        """;
    }

    private static StatusAnswer AskPost(string body, string? origin = "ORIGIN", string extra = "", IEventSink? sink = null, Func<string, bool>? isDuplicate = null, FixedStatus? st = null)
    {
        var raw = LinkTestData.Request(Port, method: "POST", path: "/events", origin: origin, extra: $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{extra}");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        return LocalStatusLink.Evaluate(header, Config, st ?? Status, body, sink, isDuplicate);
    }

    [Fact]
    public void Valid_browser_event_returns_200_and_appends_to_sink()
    {
        var sink = new FakeEventSink();
        var body = SampleEvent("evt-1");
        var answer = AskPost(body, sink: sink);

        Assert.Equal(200, answer.Status);
        Assert.Equal("{\"ok\":true}", answer.Json);
        Assert.Equal(LinkTestData.ChromeOrigin, answer.CorsOrigin);
        Assert.Single(sink.Events);
        Assert.Equal(body, sink.Events[0]);
    }

    [Fact]
    public void Valid_browser_event_with_extension_id_header_and_no_origin_returns_200_without_cors_header()
    {
        var sink = new FakeEventSink();
        var body = SampleEvent("evt-2");
        var raw = LinkTestData.Request(Port, method: "POST", path: "/events", origin: null,
            extra: $"X-DLA-Extension-Id: {LinkTestData.ChromeId}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        var answer = LocalStatusLink.Evaluate(header, Config, Status, body, sink);

        Assert.Equal(200, answer.Status);
        Assert.Null(answer.CorsOrigin);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Duplicate_event_id_returns_200_ok_and_does_not_append_again()
    {
        var sink = new FakeEventSink();
        var body = SampleEvent("dup-1");
        var answer = AskPost(body, sink: sink, isDuplicate: id => id == "dup-1");

        Assert.Equal(200, answer.Status);
        Assert.Contains("duplicate", answer.Json!);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Missing_or_empty_body_returns_400()
    {
        var answer = AskPost("");
        Assert.Equal(400, answer.Status);
    }

    [Fact]
    public void Malformed_json_returns_400()
    {
        var answer = AskPost("{ not json");
        Assert.Equal(400, answer.Status);
    }

    [Theory]
    [InlineData("bad schema version", "2", "extension", "example.com", "https://example.com")]
    [InlineData("bad source", "1", "agent", "example.com", "https://example.com")]
    [InlineData("bad domain with space", "1", "extension", "not a domain", "https://example.com")]
    [InlineData("url with query string", "1", "extension", "example.com", "https://example.com?query=1")]
    [InlineData("url with fragment", "1", "extension", "example.com", "https://example.com#section")]
    public void Invalid_schema_fields_return_400(string why, string sv, string src, string dom, string url)
    {
        _ = why;
        var body = SampleEvent(schemaVersion: sv, source: src, domain: dom, url: url);
        var answer = AskPost(body);
        Assert.Equal(400, answer.Status);
    }

    [Theory]
    [InlineData("\"app\": null,")]
    [InlineData("\"app\": \"chrome.exe\",")]
    [InlineData("\"presence\": null,")]
    [InlineData("\"presence\": \"active\",")]
    public void An_older_extension_build_that_still_sends_app_or_presence_is_not_refused_because_nothing_reads_them(string oldFields)
    {
        var body = SampleEvent().Replace("\"title\": \"Example Domain\",", "\"title\": \"Example Domain\", " + oldFields);
        Assert.Contains("title", body);
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(body, out _, out var error), error);
    }

    [Fact]
    public void An_event_from_the_desktop_side_is_refused()
    {
        Assert.False(LocalStatusLink.TryValidateExtensionEvent(SampleEvent(source: "agent"), out _, out var error));
        Assert.Equal("invalid_source", error);
    }

    [Fact]
    public void An_event_without_any_idle_field_is_accepted_and_stored_exactly_as_sent()
    {
        var sink = new FakeEventSink();
        var body = SampleEvent();
        Assert.DoesNotContain("idle", body);
        var answer = AskPost(body, sink: sink);
        Assert.Equal(200, answer.Status);
        Assert.Equal(body, Assert.Single(sink.Events));
    }

    [Theory]
    [InlineData("\"idle\": false,")]
    [InlineData("\"idle\": true,")]
    [InlineData("\"browser_idle\": true,")]
    [InlineData("\"browser_idle\": \"yes\",")]
    public void An_older_extension_build_that_still_sends_idle_fields_is_not_refused_because_nothing_reads_them(string idleText)
    {
        var body = SampleEvent().Replace("\"presence\": null,", idleText + " \"presence\": null,");
        Assert.Contains("idle", body);
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(body, out _, out var error), error);
    }

    [Fact]
    public void Negative_duration_returns_400()
    {
        var body = SampleEvent(duration: -5);
        Assert.Equal(400, AskPost(body).Status);
    }

    [Fact]
    public void When_tracking_is_paused_returns_403()
    {
        var paused = new FixedStatus(new AgentStatus(true, false, 1));
        var answer = AskPost(SampleEvent(), st: paused);
        Assert.Equal(403, answer.Status);
    }

    [Fact]
    public void When_consent_is_not_given_returns_403()
    {
        var noConsent = new FixedStatus(new AgentStatus(false, false, 0));
        var answer = AskPost(SampleEvent(), st: noConsent);
        Assert.Equal(403, answer.Status);
    }

    [Fact]
    public void Options_preflight_for_events_returns_204_with_cors_for_allowed_origin()
    {
        var raw = LinkTestData.Request(Port, method: "OPTIONS", path: "/events");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        var answer = LocalStatusLink.Evaluate(header, Config, Status);
        Assert.Equal(204, answer.Status);
        Assert.Equal(LinkTestData.ChromeOrigin, answer.CorsOrigin);
    }

    [Fact]
    public void Options_preflight_for_events_from_unauthorized_origin_returns_403()
    {
        var raw = LinkTestData.Request(Port, method: "OPTIONS", path: "/events", origin: "https://evil.example");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        var answer = LocalStatusLink.Evaluate(header, Config, Status);
        Assert.Equal(403, answer.Status);
    }

    [Fact]
    public void Get_on_events_returns_403()
    {
        var raw = LinkTestData.Request(Port, method: "GET", path: "/events");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        var answer = LocalStatusLink.Evaluate(header, Config, Status);
        Assert.Equal(403, answer.Status);
    }
}

public class EventsEndpointLiveTests
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };

    private static string SampleEventJson(string id = "live-1") =>
        $$"""
        {
          "schema_version": "1",
          "id": "{{id}}",
          "start": "2026-10-27T05:15:00Z",
          "end": "2026-10-27T05:15:10Z",
          "duration_seconds": 10,
          "source": "extension",
          "app": null,
          "domain": "example.org",
          "url": "https://example.org/path",
          "title": "Example",
          "presence": null,
          "category": null,
          "confidence": null,
          "uncertain": false,
          "label_source": null
        }
        """;

    [Fact]
    public async Task Live_post_events_appends_to_jsonl_and_returns_200_with_cors()
    {
        using var t = new TempData();
        var port = LinkTestData.FreePort();
        var eventsFile = Path.Combine(t.DataDir, "events.jsonl");
        var sink = new JsonlEventSink(eventsFile);
        var status = new FixedStatus(new AgentStatus(true, true, 1));
        using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), status, sink: sink);
        Assert.True(link.Start());

        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/events");
        request.Headers.TryAddWithoutValidation("Origin", LinkTestData.ChromeOrigin);
        request.Content = new StringContent(SampleEventJson("live-1"), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(LinkTestData.ChromeOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var lines = File.ReadAllLines(eventsFile);
        Assert.Single(lines);
        Assert.Contains("\"id\":\"live-1\"", lines[0]);
        Assert.Contains("\"source\":\"extension\"", lines[0]);
    }

    [Fact]
    public async Task Live_post_events_rejects_malformed_payload_with_400()
    {
        var port = LinkTestData.FreePort();
        var status = new FixedStatus(new AgentStatus(true, true, 1));
        using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), status);
        Assert.True(link.Start());

        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/events");
        request.Headers.TryAddWithoutValidation("Origin", LinkTestData.ChromeOrigin);
        request.Content = new StringContent("not json", Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Live_post_events_rejects_unallowlisted_origin_with_403()
    {
        var port = LinkTestData.FreePort();
        var status = new FixedStatus(new AgentStatus(true, true, 1));
        using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), status);
        Assert.True(link.Start());

        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/events");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Content = new StringContent(SampleEventJson("live-evil"), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Live_post_events_deduplicates_same_event_id()
    {
        using var t = new TempData();
        var port = LinkTestData.FreePort();
        var eventsFile = Path.Combine(t.DataDir, "events.jsonl");
        var sink = new JsonlEventSink(eventsFile);
        var status = new FixedStatus(new AgentStatus(true, true, 1));
        using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), status, sink: sink);
        Assert.True(link.Start());

        for (int i = 0; i < 2; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/events");
            req.Headers.TryAddWithoutValidation("Origin", LinkTestData.ChromeOrigin);
            req.Content = new StringContent(SampleEventJson("live-dup-id"), Encoding.UTF8, "application/json");
            using var resp = await Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        var lines = File.ReadAllLines(eventsFile);
        Assert.Single(lines);
    }
}

public class JsonlEventSinkTests
{
    [Fact]
    public void Appends_events_safely_and_creates_directory()
    {
        using var t = new TempData();
        var file = Path.Combine(t.DataDir, "sub", "events.jsonl");
        var sink = new JsonlEventSink(file);

        Assert.True(sink.AppendEvent("{\"id\":\"1\"}"));
        Assert.True(sink.AppendEvent("{\"id\":\"2\"}"));

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"id\":\"1\"}", lines[0]);
        Assert.Equal("{\"id\":\"2\"}", lines[1]);
    }
}

internal sealed class FailingEventSink : IEventSink
{
    public bool AppendEvent(string jsonLine) => false;
}

public class EventsEndpointRobustnessTests
{
    private const int Port = 49999;
    private static readonly AgentLinkConfig Config = LinkTestData.Config(Port, LinkTestData.ChromeId);
    private static readonly FixedStatus Status = new(new AgentStatus(true, true, 1));

    // Exactly what the extension's TabLogger produces (checked against its test output): no trailing slash on a bare domain,
    // a non-ASCII title, presence/category/confidence/label_source all null.
    private const string TabLoggerEvent =
        "{\"schema_version\":\"1\",\"id\":\"2f1c9a4e-6b1d-4f0e-9d53-0c1f4d8a7b11\",\"start\":\"2026-10-30T09:15:00Z\",\"end\":\"2026-10-30T09:15:25Z\"," +
        "\"duration_seconds\":25,\"source\":\"extension\",\"app\":null,\"domain\":\"youtube.com\",\"url\":\"https://www.youtube.com/watch\"," +
        "\"title\":\"Café – YouTube\",\"presence\":null,\"category\":null,\"confidence\":null,\"uncertain\":false,\"label_source\":null}";

    private static StatusAnswer Post(string body, IEventSink sink, Func<string, bool>? dup = null, Action<string>? forget = null)
    {
        var raw = LinkTestData.Request(Port, method: "POST", path: "/events", origin: "ORIGIN", extra: $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        return LocalStatusLink.Evaluate(header, Config, Status, body, sink, dup, forget);
    }

    [Fact]
    public void Real_tab_logger_event_is_accepted_and_written()
    {
        var sink = new FakeEventSink();
        var answer = Post(TabLoggerEvent, sink);
        Assert.Equal(200, answer.Status);
        Assert.Single(sink.Events);
    }

    [Theory]
    [InlineData("{\"schema_version\":1,\"source\":\"extension\"}")]
    [InlineData("{\"schema_version\":\"1\",\"source\":{}}")]
    [InlineData("{\"schema_version\":null,\"source\":null}")]
    [InlineData("[1,2]")]
    public void Wrong_value_types_give_400_and_never_throw(string body)
    {
        var answer = Post(body, new FakeEventSink());
        Assert.Equal(400, answer.Status);
    }

    [Fact]
    public void Write_failure_gives_500_and_the_id_is_forgotten_so_a_retry_is_not_a_duplicate()
    {
        var seen = new HashSet<string>();
        Func<string, bool> dup = id => !seen.Add(id);
        Action<string> forget = id => seen.Remove(id);

        var first = Post(TabLoggerEvent, new FailingEventSink(), dup, forget);
        Assert.Equal(500, first.Status);

        var sink = new FakeEventSink();
        var retry = Post(TabLoggerEvent, sink, dup, forget);
        Assert.Equal(200, retry.Status);
        Assert.DoesNotContain("duplicate", retry.Json);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Forget_id_on_the_real_link_lets_the_same_id_in_again()
    {
        using var t = new TempData();
        using var link = new LocalStatusLink(LinkTestData.Config(Port, LinkTestData.ChromeId), Status, null, new FakeEventSink());
        Assert.False(link.IsDuplicateAndRecord("a"));
        Assert.True(link.IsDuplicateAndRecord("a"));
        link.ForgetId("a");
        Assert.False(link.IsDuplicateAndRecord("a"));
    }

    [Fact]
    public void Sink_writes_no_byte_order_mark_and_one_line_per_event_next_to_python_lines()
    {
        using var t = new TempData();
        var file = Path.Combine(t.DataDir, "events.jsonl");
        var sink = new JsonlEventSink(file);

        Assert.True(sink.AppendEvent(TabLoggerEvent));
        File.AppendAllText(file, "{\"source\": \"agent\", \"id\": \"p1\"}\n");   // a Python-style line
        Assert.True(sink.AppendEvent(TabLoggerEvent.Replace("2f1c9a4e", "3a2d0b5f")));

        var bytes = File.ReadAllBytes(file);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "file starts with a BOM");

        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines) System.Text.Json.JsonDocument.Parse(line).Dispose();
        Assert.Contains("\"source\":\"extension\"", lines[0]);
        Assert.Contains("\"source\": \"agent\"", lines[1]);
        Assert.Contains("\"source\":\"extension\"", lines[2]);
    }

    [Fact]
    public void Sink_reports_failure_when_the_file_cannot_be_written()
    {
        using var t = new TempData();
        var blocked = Path.Combine(t.DataDir, "a-folder");
        Directory.CreateDirectory(blocked);                       // a folder where the file should be
        Assert.False(new JsonlEventSink(blocked).AppendEvent(TabLoggerEvent));
    }
}
