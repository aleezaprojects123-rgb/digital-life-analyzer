using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dla.Agent.Consent;
using Dla.Agent.Link;

namespace Dla.Agent.Tests;

internal static class LinkTestData
{
    // Made-up, well-formed IDs. They belong to no real extension.
    public const string ChromeId = "abcdefghijklmnopabcdefghijklmnop";
    public const string OtherChromeId = "ponmlkjihgfedcbaponmlkjihgfedcba";
    public const string MozId = "123e4567-e89b-12d3-a456-426614174000";

    public static string ChromeOrigin => "chrome-extension://" + ChromeId;

    public static AgentLinkConfig Config(int port, params string[] ids) => new(port, ids);

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string Request(int port, string method = "GET", string path = "/status", string? origin = "ORIGIN", string? host = "HOST", string extra = "")
    {
        origin = origin == "ORIGIN" ? ChromeOrigin : origin;
        host = host == "HOST" ? $"127.0.0.1:{port}" : host;
        var sb = new StringBuilder($"{method} {path} HTTP/1.1\r\n");
        if (host is not null) sb.Append($"Host: {host}\r\n");
        if (origin is not null) sb.Append($"Origin: {origin}\r\n");
        sb.Append(extra).Append("\r\n");
        return sb.ToString();
    }
}

internal sealed class FixedStatus(AgentStatus status) : IStatusSource
{
    public AgentStatus Status { get; set; } = status;
    public AgentStatus GetStatus() => Status;
}

public class AgentLinkConfigTests
{
    [Fact]
    public void Defaults_the_port_is_fixed_and_the_allowlist_is_empty_when_there_is_no_file()
    {
        using var t = new TempData();
        var config = AgentLinkConfig.Load(t.DataDir);

        Assert.Equal(49721, AgentLinkConfig.DefaultPort);
        Assert.Equal(AgentLinkConfig.DefaultPort, config.Port);
        Assert.Empty(config.AllowedExtensionIds);
        Assert.False(Directory.Exists(t.DataDir));            // loading never creates the folder or the file
    }

    [Fact]
    public void An_empty_allowlist_allows_no_origin_at_all()
    {
        var config = new AgentLinkConfig(1234, []);
        Assert.False(config.TryGetAllowedOrigin(LinkTestData.ChromeOrigin, out var origin));
        Assert.Equal("", origin);
    }

    [Fact]
    public void A_valid_file_gives_the_ids_lowercased_and_without_duplicates()
    {
        using var t = new TempData();
        t.WriteFile(AgentLinkConfig.FileName, $$"""{ "allowedExtensionIds": [ "{{LinkTestData.ChromeId.ToUpperInvariant()}}", "{{LinkTestData.ChromeId}}", "{{LinkTestData.MozId}}" ] }""");

        var config = AgentLinkConfig.Load(t.DataDir);

        Assert.Equal(new[] { LinkTestData.ChromeId, LinkTestData.MozId }.OrderBy(x => x, StringComparer.Ordinal), config.AllowedExtensionIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "allowedExtensionIds": null }""")]
    [InlineData("""{ "allowedExtensionIds": "abcdefghijklmnopabcdefghijklmnop" }""")]   // a string, not a list
    [InlineData("""{ "allowedExtensionIds": { "a": 1 } }""")]
    [InlineData("""{ "allowedExtensionIds": [ 1, true, null, {}, [] ] }""")]
    public void A_missing_corrupt_or_wrongly_shaped_file_allows_nothing(string content)
    {
        using var t = new TempData();
        t.WriteFile(AgentLinkConfig.FileName, content);
        Assert.Empty(AgentLinkConfig.Load(t.DataDir).AllowedExtensionIds);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("abc")]                                                  // too short
    [InlineData("abcdefghijklmnopabcdefghijklmnopq")]                    // too long
    [InlineData("abcdefghijklmnopabcdefghijklmnoz")]                     // letter outside a-p
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop")]  // a full origin is not an id
    [InlineData("abcdefghijklmnopabcdefghijklmnop/")]
    [InlineData("123e4567-e89b-12d3-a456-42661417400")]                  // short uuid
    [InlineData("https://evil.example")]
    public void Entries_that_do_not_look_like_a_real_extension_id_are_dropped(string entry)
    {
        using var t = new TempData();
        t.WriteFile(AgentLinkConfig.FileName, JsonSerializer.Serialize(new { allowedExtensionIds = new[] { entry, LinkTestData.ChromeId } }));
        Assert.Equal([LinkTestData.ChromeId], AgentLinkConfig.Load(t.DataDir).AllowedExtensionIds);
    }

    [Fact]
    public void A_huge_file_is_ignored()
    {
        using var t = new TempData();
        t.WriteFile(AgentLinkConfig.FileName, $$"""{ "allowedExtensionIds": [ "{{LinkTestData.ChromeId}}" ] }""" + new string(' ', 20_000));
        Assert.Empty(AgentLinkConfig.Load(t.DataDir).AllowedExtensionIds);
    }

    // ---- the Origin check ----

    private static AgentLinkConfig Allowing() => LinkTestData.Config(1234, LinkTestData.ChromeId, LinkTestData.MozId);

    [Fact]
    public void An_allowlisted_chrome_or_edge_extension_origin_is_allowed_and_echoed_unchanged()
    {
        Assert.True(Allowing().TryGetAllowedOrigin(LinkTestData.ChromeOrigin, out var origin));
        Assert.Equal(LinkTestData.ChromeOrigin, origin);
    }

    [Fact]
    public void An_allowlisted_firefox_style_origin_is_allowed()
    {
        Assert.True(Allowing().TryGetAllowedOrigin("moz-extension://" + LinkTestData.MozId, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:49721")]
    [InlineData("file://")]
    [InlineData("chrome://extensions")]
    [InlineData("chrome-extension://")]
    [InlineData("chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba")]                  // well formed but not in the list
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/")]                 // trailing slash
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html")]       // a path
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop:80")]               // a port
    [InlineData("CHROME-EXTENSION://abcdefghijklmnopabcdefghijklmnop")]                  // wrong case
    [InlineData("chrome-extension://ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]
    [InlineData(" chrome-extension://abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop ")]
    [InlineData("https://abcdefghijklmnopabcdefghijklmnop")]                             // right id, web scheme
    [InlineData("moz-extension://abcdefghijklmnopabcdefghijklmnop")]                     // chrome-shaped id with moz scheme
    [InlineData("chrome-extension://123e4567-e89b-12d3-a456-426614174000")]              // uuid with chrome scheme
    [InlineData("moz-extension://123E4567-E89B-12D3-A456-426614174000")]
    public void Everything_else_is_refused(string? origin)
    {
        Assert.False(Allowing().TryGetAllowedOrigin(origin, out var echoed));
        Assert.Equal("", echoed);
    }

    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("123e4567-e89b-12d3-a456-426614174000", true)]
    [InlineData("ponmlkjihgfedcbaponmlkjihgfedcba", false)]       // well formed, not listed
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", false)]       // upper case
    [InlineData(" abcdefghijklmnopabcdefghijklmnop", false)]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_id_header_form_allows_only_a_listed_well_formed_id(string? id, bool expected)
    {
        Assert.Equal(expected, Allowing().TryGetAllowedId(id, out var echoed));
        Assert.Equal(expected ? id : "", echoed);
    }

    [Fact]
    public void A_very_long_origin_is_refused()
    {
        Assert.False(Allowing().TryGetAllowedOrigin("chrome-extension://" + new string('a', 5000), out _));
    }
}

public class AgentStatusTests
{
    [Fact]
    public void The_json_has_exactly_the_three_agreed_fields()
    {
        var json = new AgentStatus(true, true, 1).ToJson();
        Assert.Equal("""{"consented":true,"trackingEnabled":true,"consentVersion":1}""", json);

        using var doc = JsonDocument.Parse(new AgentStatus(false, false, 0).ToJson());
        Assert.Equal(["consented", "trackingEnabled", "consentVersion"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    private static (AgentStatusProvider provider, AgentState state, FakeGate gate) Make(bool consent)
    {
        var gate = new FakeGate { HasConsent = consent };
        var state = new AgentState();
        return (new AgentStatusProvider(gate, state), state, gate);
    }

    [Fact]
    public void Without_consent_everything_is_false_and_the_version_is_zero()
    {
        var (provider, _, _) = Make(consent: false);
        Assert.Equal(new AgentStatus(false, false, 0), provider.GetStatus());
    }

    [Fact]
    public void With_consent_and_tracking_on_it_says_consented_and_the_terms_version()
    {
        var (provider, _, _) = Make(consent: true);
        Assert.Equal(new AgentStatus(true, true, ConsentText.Version), provider.GetStatus());
    }

    [Fact]
    public void While_tracking_is_paused_consented_is_false_so_an_extension_stays_silent()
    {
        var (provider, state, _) = Make(consent: true);
        state.SetTrackingEnabled(false);
        Assert.Equal(new AgentStatus(false, false, ConsentText.Version), provider.GetStatus());
        state.SetTrackingEnabled(true);
        Assert.True(provider.GetStatus().Consented);
    }

    [Fact]
    public void Tracking_enabled_is_never_true_without_consent_even_though_the_pause_flag_defaults_to_on()
    {
        var (provider, state, _) = Make(consent: false);
        Assert.True(state.TrackingEnabled);
        Assert.False(provider.GetStatus().TrackingEnabled);
    }

    [Fact]
    public void Withdrawing_consent_shows_up_on_the_very_next_call()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);
        store.SaveAcceptance(DateTimeOffset.UtcNow);
        var provider = new AgentStatusProvider(new FileConsentGate(store), new AgentState());
        Assert.True(provider.GetStatus().Consented);

        File.Delete(t.ConsentFile);
        Assert.Equal(new AgentStatus(false, false, 0), provider.GetStatus());
    }
}

/// <summary>The request decision, tested without any socket.</summary>
public class EvaluateTests
{
    private const int Port = 49999;
    private static readonly AgentLinkConfig Config = LinkTestData.Config(Port, LinkTestData.ChromeId);
    private static readonly FixedStatus Status = new(new AgentStatus(true, true, 1));

    private static StatusAnswer Ask(string rawRequest) =>
        LocalStatusLink.Evaluate(rawRequest[..rawRequest.IndexOf("\r\n\r\n", StringComparison.Ordinal)], Config, Status);

    private static string Good(string extra = "") => LinkTestData.Request(Port, extra: extra);

    [Fact]
    public void The_one_good_request_gets_200_with_the_status_json_and_the_origin_to_echo()
    {
        var answer = Ask(Good());
        Assert.Equal(200, answer.Status);
        Assert.Equal("""{"consented":true,"trackingEnabled":true,"consentVersion":1}""", answer.Json);
        Assert.Equal(LinkTestData.ChromeOrigin, answer.CorsOrigin);
    }

    [Fact]
    public void Header_names_are_case_insensitive_and_http_1_0_is_fine()
    {
        var raw = $"GET /status HTTP/1.0\r\nhost: 127.0.0.1:{Port}\r\norigin: {LinkTestData.ChromeOrigin}\r\n\r\n";
        Assert.Equal(200, Ask(raw).Status);
    }

    [Fact]
    public void Harmless_extra_headers_are_allowed()
    {
        Assert.Equal(200, Ask(Good("Accept: application/json\r\nUser-Agent: test\r\nAccept-Encoding: gzip\r\n")).Status);
    }

    [Theory]
    [InlineData("web page origin", "https://evil.example")]
    [InlineData("localhost web page", "http://localhost:3000")]
    [InlineData("null origin", "null")]
    [InlineData("unlisted extension", "chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba")]
    [InlineData("empty origin", "")]
    public void Other_origins_get_403_and_nothing_else(string why, string origin)
    {
        _ = why;
        var answer = Ask(LinkTestData.Request(Port, origin: origin));
        Assert.Equal(new StatusAnswer(403, null, null), answer);
    }

    [Fact]
    public void A_request_without_an_origin_gets_403()
    {
        Assert.Equal(403, Ask(LinkTestData.Request(Port, origin: null)).Status);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("CONNECT")]
    [InlineData("get")]
    [InlineData("TRACE")]
    public void Any_method_other_than_GET_gets_403_even_with_an_allowed_origin(string method)
    {
        Assert.Equal(403, Ask(LinkTestData.Request(Port, method: method)).Status);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/status/")]
    [InlineData("/status/x")]
    [InlineData("/STATUS")]
    [InlineData("/status?x=1")]
    [InlineData("//status")]
    [InlineData("/events")]
    [InlineData("/data")]
    [InlineData("/../status")]
    [InlineData("*")]
    [InlineData("status")]
    [InlineData("http://127.0.0.1:49999/status")]
    public void Any_other_path_gets_403_even_with_an_allowed_origin(string path)
    {
        Assert.Equal(403, Ask(LinkTestData.Request(Port, path: path)).Status);
    }

    [Theory]
    [InlineData("localhost:49999")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:1")]
    [InlineData("evil.example")]
    [InlineData("evil.example:49999")]
    [InlineData("[::1]:49999")]
    [InlineData("0.0.0.0:49999")]
    [InlineData("")]
    public void A_wrong_host_header_gets_403_which_blocks_dns_rebinding(string host)
    {
        Assert.Equal(403, Ask(LinkTestData.Request(Port, host: host)).Status);
    }

    [Fact]
    public void A_missing_host_header_gets_403()
    {
        Assert.Equal(403, Ask(LinkTestData.Request(Port, host: null)).Status);
    }

    [Theory]
    [InlineData("GET /status")]                                  // no version
    [InlineData("GET /status HTTP/2.0")]
    [InlineData("GET /status HTTP/1")]
    [InlineData("GET /status HTTP/1.1 extra")]
    [InlineData("GET  /status HTTP/1.1")]                        // double space
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("\u0000\u0001\u0002")]
    public void A_malformed_request_line_gets_403(string requestLine)
    {
        var raw = $"{requestLine}\r\nHost: 127.0.0.1:{Port}\r\nOrigin: {LinkTestData.ChromeOrigin}\r\n\r\n";
        Assert.Equal(403, Ask(raw).Status);
    }

    [Theory]
    [InlineData("Origin: chrome-extension://abcdefghijklmnopabcdefghijklmnop\r\n")]            // a second, identical Origin
    [InlineData("Origin: https://evil.example\r\n")]                                           // a second, different Origin
    [InlineData("Host: evil.example\r\n")]                                                     // a second Host
    [InlineData(" folded: value\r\n")]                                                         // folded header line
    [InlineData("\tfolded: value\r\n")]
    [InlineData("NoColonHere\r\n")]
    [InlineData(": empty name\r\n")]
    [InlineData("Bad Name: value\r\n")]                                                         // space in the name
    [InlineData("Origin : chrome-extension://abcdefghijklmnopabcdefghijklmnop\r\n")]            // space before the colon
    [InlineData("X-Evil: a\u0001b\r\n")]                                                        // control character
    [InlineData("X-Evil: café\r\n")]                                                       // non-ASCII
    public void Malformed_repeated_or_tricky_headers_get_403(string extraHeader)
    {
        Assert.Equal(403, Ask(Good(extraHeader)).Status);
    }

    // ---- extensions whose browser sends no Origin: they name themselves in X-DLA-Extension-Id ----

    private static string WithId(string id, string? origin = null) =>
        LinkTestData.Request(Port, origin: origin, extra: $"X-DLA-Extension-Id: {id}\r\n");

    [Fact]
    public void No_origin_but_a_listed_extension_id_header_gets_200_without_any_cors_header()
    {
        var answer = Ask(WithId(LinkTestData.ChromeId));
        Assert.Equal(200, answer.Status);
        Assert.Equal("""{"consented":true,"trackingEnabled":true,"consentVersion":1}""", answer.Json);
        Assert.Null(answer.CorsOrigin);
    }

    [Theory]
    [InlineData("ponmlkjihgfedcbaponmlkjihgfedcba")]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop")]
    public void No_origin_and_a_wrong_id_header_gets_403(string id)
    {
        Assert.Equal(403, Ask(WithId(id)).Status);
    }

    [Fact]
    public void An_origin_that_is_not_allowed_is_refused_even_with_a_good_id_header()
    {
        Assert.Equal(403, Ask(WithId(LinkTestData.ChromeId, origin: "https://evil.example")).Status);
        Assert.Equal(403, Ask(WithId(LinkTestData.ChromeId, origin: "null")).Status);
    }

    [Fact]
    public void A_preflight_for_the_id_header_is_refused_so_web_pages_cannot_use_it()
    {
        var raw = LinkTestData.Request(Port, method: "OPTIONS", origin: "https://evil.example",
            extra: "Access-Control-Request-Method: GET\r\nAccess-Control-Request-Headers: x-dla-extension-id\r\n");
        Assert.Equal(403, Ask(raw).Status);
    }

    [Fact]
    public void A_repeated_id_header_gets_403()
    {
        var twice = LinkTestData.Request(Port, origin: null, extra: $"X-DLA-Extension-Id: {LinkTestData.ChromeId}\r\nX-DLA-Extension-Id: {LinkTestData.ChromeId}\r\n");
        Assert.Equal(403, Ask(twice).Status);
    }

    [Fact]
    public void An_empty_allowlist_means_even_a_perfect_request_gets_403()
    {
        var empty = LinkTestData.Config(Port);
        var raw = Good();
        Assert.Equal(403, LocalStatusLink.Evaluate(raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)], empty, Status).Status);
    }

    [Fact]
    public void The_answer_follows_the_live_status()
    {
        var live = new FixedStatus(new AgentStatus(false, false, 0));
        var raw = Good();
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.Contains("\"consented\":false", LocalStatusLink.Evaluate(header, Config, live).Json);
        live.Status = new AgentStatus(true, true, 1);
        Assert.Contains("\"consented\":true", LocalStatusLink.Evaluate(header, Config, live).Json);
    }
}

/// <summary>A real server on a real loopback port.</summary>
public class LocalStatusLinkLiveTests
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };

    private static (LocalStatusLink link, int port, FixedStatus status) Start(params string[] ids)
    {
        var port = LinkTestData.FreePort();
        var status = new FixedStatus(new AgentStatus(true, true, 1));
        var link = new LocalStatusLink(LinkTestData.Config(port, ids.Length == 0 ? [LinkTestData.ChromeId] : ids), status);
        Assert.True(link.Start());
        return (link, port, status);
    }

    private static Task<HttpResponseMessage> Get(int port, string? origin, string path = "/status", HttpMethod? method = null, string? host = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return Client.SendAsync(request);
    }

    /// <summary>Sends raw bytes and returns whatever comes back until the server closes the connection.</summary>
    private static async Task<string> Raw(int port, string text, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes(text));
        using var cts = new CancellationTokenSource(timeoutMs);
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0) sb.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        return sb.ToString();
    }

    private static bool CanConnect(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Parse(ip), port).Wait(1500) && client.Connected;
        }
        catch { return false; }
    }

    // ---- the good case ----

    [Fact]
    public async Task An_allowed_extension_gets_200_the_json_and_the_cors_header_for_its_own_origin()
    {
        var (link, port, _) = Start();
        using var _ = link;

        using var response = await Get(port, LinkTestData.ChromeOrigin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(LinkTestData.ChromeOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("Origin", response.Headers.Vary.Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["consented", "trackingEnabled", "consentVersion"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(doc.RootElement.GetProperty("consented").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("consentVersion").GetInt32());
    }

    [Fact]
    public async Task The_answer_changes_when_tracking_is_paused()
    {
        var (link, port, status) = Start();
        using var _ = link;
        status.Status = new AgentStatus(false, false, 1);

        using var response = await Get(port, LinkTestData.ChromeOrigin);
        Assert.Contains("\"consented\":false", await response.Content.ReadAsStringAsync());
    }

    // ---- everything else gets 403 and no CORS header ----

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("null")]
    [InlineData("chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba")]
    [InlineData("moz-extension://123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("file://")]
    public async Task Other_origins_get_403_and_never_a_cors_header(string origin)
    {
        var (link, port, _) = Start();
        using var _ = link;

        using var response = await Get(port, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_real_request_with_no_origin_but_the_extension_id_header_gets_200_and_no_cors_header()
    {
        var (link, port, _) = Start();
        using var _ = link;
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        request.Headers.TryAddWithoutValidation("X-DLA-Extension-Id", LinkTestData.ChromeId);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Contains("\"consented\":true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_real_request_with_a_wrong_id_header_gets_403()
    {
        var (link, port, _) = Start();
        using var _ = link;
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        request.Headers.TryAddWithoutValidation("X-DLA-Extension-Id", LinkTestData.OtherChromeId);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_origin_gets_403()
    {
        var (link, port, _) = Start();
        using var _ = link;
        using var response = await Get(port, origin: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task An_allowed_origin_still_gets_403_for_other_paths_and_methods()
    {
        var (link, port, _) = Start();
        using var _ = link;

        foreach (var path in new[] { "/", "/status/", "/events", "/status/x", "/STATUS" })
        {
            using var response = await Get(port, LinkTestData.ChromeOrigin, path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options, HttpMethod.Head })
        {
            using var response = await Get(port, LinkTestData.ChromeOrigin, method: method);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_spoofed_host_header_is_refused()
    {
        var (link, port, _) = Start();
        using var _ = link;
        using var response = await Get(port, LinkTestData.ChromeOrigin, host: "evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Garbage_gets_403_or_a_hang_up_but_never_a_200()
    {
        var (link, port, _) = Start();
        using var _ = link;

        foreach (var junk in new[] { "hello\r\n\r\n", "\r\n\r\n", "GET\r\n\r\n", "\u0016\u0003\u0001\u0000\r\n\r\n", "GET /status HTTP/1.1\r\nHost\r\n\r\n" })
        {
            var reply = await Raw(port, junk);
            Assert.DoesNotContain("200 OK", reply);
            Assert.DoesNotContain("consented", reply);
        }
        // the server is still fine afterwards
        using var ok = await Get(port, LinkTestData.ChromeOrigin);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task An_oversized_header_is_cut_off_and_never_answered_with_the_status()
    {
        var (link, port, _) = Start();
        using var _ = link;
        var huge = LinkTestData.Request(port, extra: "X-Big: " + new string('a', 20_000) + "\r\n");
        var reply = await Raw(port, huge);
        Assert.DoesNotContain("consented", reply);
        using var ok = await Get(port, LinkTestData.ChromeOrigin);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task A_silent_client_is_hung_up_on_and_cannot_block_other_clients()
    {
        var (link, port, _) = Start();
        using var _ = link;

        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);          // connects and says nothing

        using var ok = await Get(port, LinkTestData.ChromeOrigin);    // others are served meanwhile
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var stream = silent.GetStream();
        using var cts = new CancellationTokenSource(8000);
        var read = await stream.ReadAsync(new byte[16], cts.Token);   // the server closes it after its timeout
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Many_clients_at_once_are_answered_and_the_server_survives_a_flood()
    {
        var (link, port, _) = Start();
        using var _ = link;

        var flood = Enumerable.Range(0, 40).Select(async _ =>
        {
            using var c = new TcpClient();
            try { await c.ConnectAsync(IPAddress.Loopback, port); } catch { }
        });
        await Task.WhenAll(flood);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var r = await Get(port, LinkTestData.ChromeOrigin);
            return r.StatusCode;
        }));
        Assert.All(results, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    // ---- where it listens, and when ----

    [Fact]
    public void It_listens_on_127_0_0_1_only()
    {
        var (link, port, _) = Start();
        using var _ = link;

        var endpoint = (IPEndPoint)link.Endpoint!;
        Assert.Equal(IPAddress.Loopback, endpoint.Address);
        Assert.Equal(port, endpoint.Port);

        // and a real attempt from this PC's other network addresses must not connect
        var others = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString()).Distinct().ToArray();
        foreach (var ip in others)
            Assert.False(CanConnect(ip, port), $"the status link must not be reachable at {ip}:{port}");
    }

    [Fact]
    public void Stop_closes_the_port_and_it_can_be_started_again()
    {
        var (link, port, _) = Start();
        Assert.True(link.IsListening);
        Assert.True(CanConnect("127.0.0.1", port));

        link.Stop();
        Assert.False(link.IsListening);
        Assert.False(CanConnect("127.0.0.1", port));

        Assert.True(link.Start());
        Assert.True(CanConnect("127.0.0.1", port));
        link.Dispose();
        Assert.False(CanConnect("127.0.0.1", port));
    }

    [Fact]
    public void A_port_that_is_already_in_use_does_not_crash_it_start_just_returns_false()
    {
        var port = LinkTestData.FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var log = new FakeLog();
            using var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new FixedStatus(default), log);

            var started = link.Start();                     // must not throw

            Assert.False(started);
            Assert.False(link.IsListening);
            Assert.Single(log.Lines);
            Assert.Contains("could not start", log.Lines[0]);
            link.Stop();                                    // stopping a link that never started is harmless
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public void Starting_twice_is_harmless()
    {
        var (link, _, _) = Start();
        using var _ = link;
        Assert.True(link.Start());
        Assert.True(link.IsListening);
    }
}

/// <summary>The most important rule: nothing listens before the user has consented.</summary>
public class LinkConsentTests
{
    private static bool CanConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(1500) && client.Connected;
        }
        catch { return false; }
    }

    [Fact]
    public void Without_consent_the_link_is_never_created_and_nothing_listens()
    {
        var port = LinkTestData.FreePort();
        var created = 0;
        var host = new AgentHost(new FakeGate { HasConsent = false }, () => new FakeTray(),
            () => { created++; return new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new FixedStatus(default)); });

        for (var i = 0; i < 5; i++) Assert.False(host.TryStart());

        Assert.Equal(0, created);
        Assert.Null(host.Link);
        Assert.False(CanConnect(port));
    }

    [Fact]
    public void With_consent_the_link_starts_once_after_the_tray_and_stops_with_the_host()
    {
        var port = LinkTestData.FreePort();
        var created = 0;
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => new FakeTray(),
            () => { created++; return new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new FixedStatus(default)); });

        Assert.True(host.TryStart());
        Assert.True(host.TryStart());                     // asking again never creates a second link

        Assert.Equal(1, created);
        Assert.True(host.Link!.IsListening);
        Assert.True(CanConnect(port));

        host.Stop();
        Assert.Null(host.Link);
        Assert.False(CanConnect(port));
    }

    [Fact]
    public void Consent_is_checked_when_starting_so_a_gate_that_turns_yes_later_starts_it_only_then()
    {
        var port = LinkTestData.FreePort();
        var gate = new FakeGate { HasConsent = false };
        var host = new AgentHost(gate, () => new FakeTray(),
            () => new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new FixedStatus(default)));

        Assert.False(host.TryStart());
        Assert.False(CanConnect(port));
        gate.HasConsent = true;
        Assert.True(host.TryStart());
        Assert.True(CanConnect(port));
        host.Dispose();
    }

    [Fact]
    public void A_link_that_cannot_start_never_stops_the_tray_or_the_agent()
    {
        var port = LinkTestData.FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var host = new AgentHost(new FakeGate { HasConsent = true }, () => new FakeTray(),
                () => new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new FixedStatus(default)));

            Assert.True(host.TryStart());                 // the port is taken, but the tray still starts
            Assert.True(host.IsRunning);
            Assert.False(host.Link!.IsListening);
            host.Stop();
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public void A_link_that_throws_while_being_created_or_started_never_stops_the_tray()
    {
        var host1 = new AgentHost(new FakeGate { HasConsent = true }, () => new FakeTray(), () => throw new InvalidOperationException("boom"));
        Assert.True(host1.TryStart());
        Assert.Null(host1.Link);

        var host2 = new AgentHost(new FakeGate { HasConsent = true }, () => new FakeTray(), () => new ThrowingLink());
        Assert.True(host2.TryStart());
        Assert.Null(host2.Link);
    }

    private sealed class ThrowingLink : IAgentLink
    {
        public bool IsListening => false;
        public bool Start() => throw new InvalidOperationException("boom");
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public void A_host_without_a_link_still_works_as_before()
    {
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => new FakeTray());
        Assert.True(host.TryStart());
        Assert.Null(host.Link);
    }

    [Fact]
    public void Decline_through_the_whole_start_up_means_nothing_listens()
    {
        using var t = new TempData();
        var port = LinkTestData.FreePort();
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);
        var onboarding = new OnboardingFlow(gate, store, () => ConsentDecision.Declined, new FakeLog(), () => DateTimeOffset.UtcNow);
        var host = new AgentHost(gate, () => new FakeTray(),
            () => new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new FixedStatus(default)));

        var result = new AgentLauncher(onboarding, host, _ => { }).Launch();

        Assert.False(result.TrayStarted);
        Assert.False(CanConnect(port));
    }

    [Fact]
    public void Accept_through_the_whole_start_up_makes_the_link_listen_and_answer_the_real_status()
    {
        using var t = new TempData();
        var port = LinkTestData.FreePort();
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);
        var state = new AgentState();
        var onboarding = new OnboardingFlow(gate, store, () => ConsentDecision.Accepted, new FakeLog(), () => DateTimeOffset.UtcNow);
        var host = new AgentHost(gate, () => new FakeTray(),
            () => new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new AgentStatusProvider(gate, state)));

        string? body = null;
        new AgentLauncher(onboarding, host, h =>
        {
            Assert.True(h.Link!.IsListening);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
            request.Headers.TryAddWithoutValidation("Origin", LinkTestData.ChromeOrigin);
            body = client.Send(request).Content.ReadAsStringAsync().Result;
        }).Launch();

        Assert.Equal("""{"consented":true,"trackingEnabled":true,"consentVersion":1}""", body);
    }
}
