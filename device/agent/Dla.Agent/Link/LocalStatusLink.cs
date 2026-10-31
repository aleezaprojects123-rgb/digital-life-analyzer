using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dla.Agent.Link;

/// <summary>The decision for one request: status code with the JSON body and the CORS origin to echo, or 403 with nothing.</summary>
internal readonly record struct StatusAnswer(int Status, string? Json, string? CorsOrigin);

/// <summary>
/// A tiny local web endpoint for the extension. It answers:
///
///   GET /status   ->   {"consented":true,"trackingEnabled":true,"consentVersion":1}
///   POST /events  ->   receives closed browser extension activity events and appends to events.jsonl
///   OPTIONS /events -> CORS preflight response for allowlisted extension origins
///   GET /reports/weekly/current | weekly/previous | monthly/current | monthly/previous
///                   -> the report, calculated by the report service on request (read-only; never needs a date)
///   GET /dashboard/today
///                   -> today's platform times and content counts (UI only; the extension does not get it)
///
/// and nothing else: no other path, no other method, no data transfer out of this PC.
///
/// Two kinds of caller. The allowlisted extension may use every route above. The separate Digital Life Analyzer UI proves itself with the
/// X-DLA-UI-Token header (see UiAccess) and may use ONLY the four report GET routes and GET /dashboard/today: /status, /events and everything else are refused for it.
///
/// It listens on a plain socket bound to 127.0.0.1, so other computers on the network cannot reach it.
///
/// A request is answered with 200/204 only if ALL security checks hold; anything else gets 403 with no CORS headers:
///   * valid method and target,
///   * a Host header of exactly 127.0.0.1:port (this blocks DNS-rebinding tricks),
///   * an allowlisted extension: either an Origin that is an allowlisted extension origin, or no Origin but an
///     X-DLA-Extension-Id header with an allowlisted id (web pages are refused).
/// </summary>
public sealed partial class LocalStatusLink : IAgentLink
{
    private const int MaxHeaderBytes = 8192;
    private const int MaxBodyBytes = 65536;
    private const int MaxConnections = 8;
    public const string ExtensionIdHeader = "X-DLA-Extension-Id";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex DomainShape();

    private readonly AgentLinkConfig _config;
    private readonly IStatusSource _status;
    private readonly IAgentLog? _log;
    private readonly IEventSink _sink;
    private readonly Reports.IReportService? _reports;
    private readonly UiAccess? _ui;
    private readonly object _gate = new();
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private const int MaxSeenIds = 2000;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _active;

    private readonly Media.MediaCollector? _media;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");   // one recording session per link run

    private readonly IDisposable? _owned;   // storage upkeep that lives and dies with the link

    public LocalStatusLink(AgentLinkConfig config, IStatusSource status, IAgentLog? log = null, IEventSink? sink = null, Media.MediaCollector? media = null, IDisposable? owned = null, Reports.IReportService? reports = null, UiAccess? ui = null)
    {
        _reports = reports;
        _ui = ui;
        _owned = owned;
        _media = media;
        _config = config;
        _status = status;
        _log = log;
        _sink = sink ?? new JsonlEventSink(JsonlEventSink.DefaultEventsFilePath());
    }

    public bool IsListening => _listener is not null;

    public int Port => _config.Port;

    /// <summary>Where the socket is bound (for tests): always 127.0.0.1.</summary>
    internal EndPoint? Endpoint => _listener?.LocalEndpoint;

    public bool Start()
    {
        lock (_gate)
        {
            if (_listener is not null) return true;

            var listener = new TcpListener(IPAddress.Loopback, _config.Port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start(MaxConnections);
            }
            catch (SocketException ex)
            {
                _log?.Write($"Status link could not start on 127.0.0.1:{_config.Port} ({ex.SocketErrorCode}). The agent keeps running without it.");
                return false;
            }

            _cts = new CancellationTokenSource();
            _listener = listener;
            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
            _log?.Write($"Status link listening on 127.0.0.1:{_config.Port}.");
            _log?.Write($"Browser recording session {_sessionId[..8]} (events carry the full id in session_id).");
            _media?.Start();
            if (_sink is JsonlEventSink jsonl)
                _log?.Write($"Browser events are appended to {Path.GetFullPath(jsonl.FilePath)}");
            else if (_sink is Storage.SqliteEventSink sqlite)
                _log?.Write($"Browser events are stored in {Path.GetFullPath(sqlite.DatabasePath)}");
            return true;
        }
    }

    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            var listener = _listener;
            if (listener is null) return;
            _listener = null;
            cts = _cts;
            _cts = null;
            loop = _acceptLoop;
            _acceptLoop = null;

            cts?.Cancel();
            try { listener.Stop(); } catch (SocketException) { }
        }

        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        cts?.Dispose();
        _media?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _ui?.Remove();
        _owned?.Dispose();
    }

    /// <summary>What is stored for an accepted browser event: stamped with this recording session, then matched with media.</summary>
    private IReadOnlyList<string> AcceptedEvent(string body)
    {
        var stamped = SessionStamp.Apply(body, _sessionId);
        return _media is null ? [stamped] : _media.Enrich(stamped);
    }

    internal bool IsDuplicateAndRecord(string id)
    {
        lock (_gate)
        {
            if (_seenIds.Contains(id))
                return true;

            if (_seenOrder.Count >= MaxSeenIds)
            {
                var oldest = _seenOrder.Dequeue();
                _seenIds.Remove(oldest);
            }
            _seenIds.Add(id);
            _seenOrder.Enqueue(id);
            return false;
        }
    }

    internal void ForgetId(string id)
    {
        lock (_gate)
        {
            if (_seenIds.Remove(id))
            {
                var kept = _seenOrder.Where(x => x != id).ToArray();
                _seenOrder.Clear();
                foreach (var x in kept) _seenOrder.Enqueue(x);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (token.IsCancellationRequested) break;
                continue;
            }

            if (Interlocked.Increment(ref _active) > MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                client.Dispose();                       // too many at once: drop the extra one
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, token), token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(RequestTimeout);          // a slow or silent client cannot hold a slot

                var stream = client.GetStream();
                var (header, body) = await ReadRequestAsync(stream, timeout.Token);
                if (header is null) return;                   // too big, cut off or empty: just hang up

                var answer = Evaluate(header, _config, _status, body, _sink, IsDuplicateAndRecord, ForgetId, AcceptedEvent, _reports, _log, _ui);
                if (answer.Status is 400 or 500 && header.StartsWith("POST ", StringComparison.Ordinal))
                    _log?.Write($"Browser event refused: HTTP {answer.Status} {answer.Json}");   // a reason code only, never event content
                await stream.WriteAsync(BuildResponse(answer), timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // a client that disappears or times out is not our problem
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <returns>The request line and headers (without the final blank line) plus body, or null if invalid.</returns>
    private static async Task<(string? Header, string? Body)> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        var end = -1;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), token);
            if (read == 0) return (null, null);
            total += read;

            end = IndexOfHeaderEnd(buffer, total);
            if (end >= 0) break;
        }

        if (end < 0) return (null, null);

        var header = Encoding.Latin1.GetString(buffer, 0, end);
        var bodyStart = end + 4;
        var bodyAlreadyRead = total - bodyStart;

        var contentLength = ParseContentLength(header);
        if (contentLength < 0 || contentLength > MaxBodyBytes)
        {
            return (header, null);
        }

        if (contentLength == 0)
        {
            return (header, "");
        }

        var bodyBytes = new byte[contentLength];
        var copied = Math.Min(bodyAlreadyRead, contentLength);
        if (copied > 0)
        {
            buffer.AsSpan(bodyStart, copied).CopyTo(bodyBytes);
        }

        var remaining = contentLength - copied;
        var offset = copied;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, remaining), token);
            if (read == 0) return (null, null);
            offset += read;
            remaining -= read;
        }

        var body = Encoding.UTF8.GetString(bodyBytes);
        return (header, body);
    }

    private static int ParseContentLength(string header)
    {
        var lines = header.Split("\r\n");
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                var val = line[(colon + 1)..].Trim();
                if (int.TryParse(val, out var len) && len >= 0) return len;
                return -1;
            }
        }
        return 0;
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    // ---- event schema validation -----------------------------------------------------------------------------------

    private static readonly HashSet<string> Platforms = new(StringComparer.Ordinal)
        { "youtube", "instagram", "tiktok", "reddit", "facebook", "whatsapp", "spotify", "netflix", "github", "google", "unknown" };

    private static readonly HashSet<string> ActivityTypes = new(StringComparer.Ordinal)
        { "webpage", "search", "video", "short_video", "reel", "music", "post", "article", "document", "social_feed", "messaging", "meeting", "shopping", "game", "unknown" };

    internal static bool TryValidateExtensionEvent(string json, out string? eventId, out string? error)
    {
        eventId = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "expected_json_object";
                return false;
            }

            // Must match schema_version "1"
            if (!root.TryGetProperty("schema_version", out var sv) || sv.ValueKind != JsonValueKind.String || sv.GetString() != "1")
            {
                error = "invalid_schema_version";
                return false;
            }

            // Must be source "extension"
            if (!root.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.String || src.GetString() != "extension")
            {
                error = "invalid_source";
                return false;
            }

            // id: string 1-64 chars
            if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            {
                error = "missing_id";
                return false;
            }
            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                error = "invalid_id";
                return false;
            }
            eventId = id;

            // start & end: UTC timestamps
            if (!root.TryGetProperty("start", out var startProp) || startProp.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("end", out var endProp) || endProp.ValueKind != JsonValueKind.String)
            {
                error = "missing_timestamps";
                return false;
            }

            var startStr = startProp.GetString();
            var endStr = endProp.GetString();
            if (!DateTimeOffset.TryParse(startStr, out var startDto) || !DateTimeOffset.TryParse(endStr, out var endDto))
            {
                error = "invalid_timestamp_format";
                return false;
            }

            if (endDto < startDto)
            {
                error = "end_before_start";
                return false;
            }

            // duration_seconds: int >= 0
            if (!root.TryGetProperty("duration_seconds", out var durProp) ||
                !durProp.TryGetInt64(out var duration) || duration < 0)
            {
                error = "invalid_duration";
                return false;
            }

            // domain: required lowercase valid domain
            if (!root.TryGetProperty("domain", out var domProp) || domProp.ValueKind != JsonValueKind.String)
            {
                error = "missing_domain";
                return false;
            }
            var domain = domProp.GetString();
            if (string.IsNullOrWhiteSpace(domain) || !DomainShape().IsMatch(domain))
            {
                error = "invalid_domain";
                return false;
            }

            // url: required sanitized string or null
            if (!root.TryGetProperty("url", out var urlProp))
            {
                error = "missing_url";
                return false;
            }
            if (urlProp.ValueKind == JsonValueKind.String)
            {
                var url = urlProp.GetString()!;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    error = "invalid_url_scheme";
                    return false;
                }
                if (url.Contains('?') || url.Contains('#'))
                {
                    error = "url_contains_query_or_fragment";
                    return false;
                }
            }
            else if (urlProp.ValueKind != JsonValueKind.Null)
            {
                error = "invalid_url_type";
                return false;
            }

            // title: string or null
            if (root.TryGetProperty("title", out var titleProp))
            {
                if (titleProp.ValueKind != JsonValueKind.String && titleProp.ValueKind != JsonValueKind.Null)
                {
                    error = "invalid_title";
                    return false;
                }
            }

            // platform and activity_type (universal browser event): optional for older extension versions, a known value when present
            if (root.TryGetProperty("platform", out var platformProp) &&
                !(platformProp.ValueKind == JsonValueKind.String && Platforms.Contains(platformProp.GetString()!)))
            {
                error = "invalid_platform";
                return false;
            }
            if (root.TryGetProperty("activity_type", out var activityProp) &&
                !(activityProp.ValueKind == JsonValueKind.String && ActivityTypes.Contains(activityProp.GetString()!)))
            {
                error = "invalid_activity_type";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "malformed_json";
            return false;
        }
    }

    // ---- the decision (pure, so it can be tested without a socket) -------------------------------------------------

    internal static StatusAnswer Evaluate(
        string headerBlock,
        AgentLinkConfig config,
        IStatusSource status,
        string? body = null,
        IEventSink? sink = null,
        Func<string, bool>? isDuplicate = null,
        Action<string>? forgetId = null,
        Func<string, IReadOnlyList<string>>? enrich = null,
        Reports.IReportService? reports = null,
        IAgentLog? log = null,
        UiAccess? ui = null)
    {
        var forbidden = new StatusAnswer(403, null, null);

        var lines = headerBlock.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3) return forbidden;
        var (method, target, version) = (requestLine[0], requestLine[1], requestLine[2]);
        if (version is not ("HTTP/1.1" or "HTTP/1.0")) return forbidden;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0 || line[0] is ' ' or '\t') return forbidden;   // empty or folded header lines are refused
            var colon = line.IndexOf(':');
            if (colon <= 0) return forbidden;

            var name = line[..colon];
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (!IsToken(name) || value.Any(c => c < 0x20 && c != '\t' || c > 0x7E)) return forbidden;
            if (!headers.TryAdd(name, value)) return forbidden;                 // a repeated header is refused
        }

        if (!headers.TryGetValue("Host", out var host) || host != $"127.0.0.1:{config.Port}") return forbidden;

        // The UI's secret opens the four report GET routes and nothing else. It is checked first and decides alone: an extension id or an
        // Origin sent next to it never widens it, and a request from a web page (it has an Origin) can never use it.
        if (headers.TryGetValue(UiAccess.TokenHeader, out var uiToken))
        {
            if (ui is null || headers.ContainsKey("Origin") || !ui.Matches(uiToken)) return forbidden;
            return method == "GET" ? ReportAnswer(target, status, reports, log, corsOrigin: null, allowDashboard: true) ?? forbidden : forbidden;
        }

        // Caller authorization: allowlisted Origin or allowlisted X-DLA-Extension-Id
        string? corsOrigin = null;
        if (headers.TryGetValue("Origin", out var originHeader))
        {
            if (!config.TryGetAllowedOrigin(originHeader, out var origin)) return forbidden;
            corsOrigin = origin;
        }
        else if (!headers.TryGetValue(ExtensionIdHeader, out var idHeader) || !config.TryGetAllowedId(idHeader, out _))
        {
            return forbidden;
        }

        // Endpoint routing
        if (method == "GET" && target == "/status")
        {
            return new StatusAnswer(200, status.GetStatus().ToJson(), corsOrigin);
        }

        if (method == "GET" && target.StartsWith("/reports/", StringComparison.Ordinal))
        {
            return ReportAnswer(target, status, reports, log, corsOrigin) ?? forbidden;
        }

        if (method == "OPTIONS" && target == "/events")
        {
            return corsOrigin is not null ? new StatusAnswer(204, null, corsOrigin) : forbidden;
        }

        if (method == "POST" && target == "/events")
        {
            var currentStatus = status.GetStatus();
            if (!currentStatus.Consented || !currentStatus.TrackingEnabled)
            {
                return forbidden;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return new StatusAnswer(400, "{\"error\":\"missing_body\"}", corsOrigin);
            }

            if (!TryValidateExtensionEvent(body, out var eventId, out var error))
            {
                return new StatusAnswer(400, $"{{\"error\":\"{error ?? "invalid_event"}\"}}", corsOrigin);
            }

            if (isDuplicate != null && isDuplicate(eventId!))
            {
                return new StatusAnswer(200, "{\"ok\":true,\"duplicate\":true}", corsOrigin);
            }

            var lines2 = enrich is null ? [body] : enrich(body);
            if (sink is not null && !sink.AppendEvents(lines2))
            {
                // The line did not reach the file. Forget the id so the extension's retry is not mistaken for a duplicate.
                forgetId?.Invoke(eventId!);
                return new StatusAnswer(500, "{\"error\":\"write_failed\"}", corsOrigin);
            }
            return new StatusAnswer(200, "{\"ok\":true}", corsOrigin);
        }

        return forbidden;
    }

    /// <summary>
    /// A report request. Same checks as every other request (already passed by now), plus consent: no report is given before the user agreed.
    /// Null means "not a report path". A failure answers with a short reason code only, never a stack trace or a database message.
    /// </summary>
    private static StatusAnswer? ReportAnswer(string target, IStatusSource status, Reports.IReportService? reports, IAgentLog? log, string? corsOrigin, bool allowDashboard = false)
    {
        Func<Reports.IReportService, string>? get = target switch
        {
            "/reports/weekly/current" => r => Reports.ReportJson.Serialize(r.GetCurrentWeeklyReport()),
            "/reports/weekly/previous" => r => Reports.ReportJson.Serialize(r.GetPreviousWeeklyReport()),
            "/reports/monthly/current" => r => Reports.ReportJson.Serialize(r.GetCurrentMonthlyReport()),
            "/reports/monthly/previous" => r => Reports.ReportJson.Serialize(r.GetPreviousMonthlyReport()),
            "/dashboard/today" when allowDashboard => r => Reports.ReportJson.Serialize(r.GetTodayDashboard()),
            _ => null,
        };
        if (get is null) return null;

        var current = status.GetStatus();
        if (!current.Consented || !current.TrackingEnabled) return null;
        if (reports is null) return new StatusAnswer(500, "{\"error\":\"reports_unavailable\"}", corsOrigin);

        try
        {
            return new StatusAnswer(200, get(reports), corsOrigin);
        }
        catch (Exception ex)
        {
            log?.Write($"A report could not be built ({ex.GetType().Name}).");   // the type only, never the message or any data
            return new StatusAnswer(500, "{\"error\":\"report_failed\"}", corsOrigin);
        }
    }

    private static bool IsToken(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '.' or '^' or '`' or '|' or '~');

    private static byte[] BuildResponse(StatusAnswer answer)
    {
        var text = new StringBuilder();
        if (answer.Status == 200)
        {
            var body = Encoding.UTF8.GetBytes(answer.Json ?? "{\"ok\":true}");
            text.Append("HTTP/1.1 200 OK\r\n");
            text.Append("Content-Type: application/json; charset=utf-8\r\n");
            text.Append($"Content-Length: {body.Length}\r\n");
            if (answer.CorsOrigin is not null)
                text.Append($"Access-Control-Allow-Origin: {answer.CorsOrigin}\r\n");
            text.Append("Vary: Origin\r\n");
            text.Append("Cache-Control: no-store\r\n");
            text.Append("X-Content-Type-Options: nosniff\r\n");
            text.Append("Connection: close\r\n\r\n");
            return [.. Encoding.ASCII.GetBytes(text.ToString()), .. body];
        }

        if (answer.Status == 204)
        {
            text.Append("HTTP/1.1 204 No Content\r\n");
            text.Append("Content-Length: 0\r\n");
            if (answer.CorsOrigin is not null)
            {
                text.Append($"Access-Control-Allow-Origin: {answer.CorsOrigin}\r\n");
                text.Append("Access-Control-Allow-Methods: POST, OPTIONS\r\n");
                text.Append("Access-Control-Allow-Headers: Content-Type, X-DLA-Extension-Id\r\n");
                text.Append("Access-Control-Max-Age: 86400\r\n");
            }
            text.Append("Vary: Origin\r\n");
            text.Append("Cache-Control: no-store\r\n");
            text.Append("Connection: close\r\n\r\n");
            return Encoding.ASCII.GetBytes(text.ToString());
        }

        if (answer.Status == 400)
        {
            var body = Encoding.UTF8.GetBytes(answer.Json ?? "{\"error\":\"bad_request\"}");
            text.Append("HTTP/1.1 400 Bad Request\r\n");
            text.Append("Content-Type: application/json; charset=utf-8\r\n");
            text.Append($"Content-Length: {body.Length}\r\n");
            if (answer.CorsOrigin is not null)
                text.Append($"Access-Control-Allow-Origin: {answer.CorsOrigin}\r\n");
            text.Append("Cache-Control: no-store\r\n");
            text.Append("X-Content-Type-Options: nosniff\r\n");
            text.Append("Connection: close\r\n\r\n");
            return [.. Encoding.ASCII.GetBytes(text.ToString()), .. body];
        }

        if (answer.Status == 500)
        {
            var body = Encoding.UTF8.GetBytes(answer.Json ?? "{\"error\":\"server_error\"}");
            text.Append("HTTP/1.1 500 Internal Server Error\r\n");
            text.Append("Content-Type: application/json; charset=utf-8\r\n");
            text.Append($"Content-Length: {body.Length}\r\n");
            if (answer.CorsOrigin is not null)
                text.Append($"Access-Control-Allow-Origin: {answer.CorsOrigin}\r\n");
            text.Append("Cache-Control: no-store\r\n");
            text.Append("X-Content-Type-Options: nosniff\r\n");
            text.Append("Connection: close\r\n\r\n");
            return [.. Encoding.ASCII.GetBytes(text.ToString()), .. body];
        }

        text.Append("HTTP/1.1 403 Forbidden\r\n");
        text.Append("Content-Length: 0\r\n");
        text.Append("Cache-Control: no-store\r\n");
        text.Append("X-Content-Type-Options: nosniff\r\n");
        text.Append("Connection: close\r\n\r\n");   // no CORS headers, no body
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
