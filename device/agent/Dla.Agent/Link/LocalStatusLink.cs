using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dla.Agent.Link;

/// <summary>The decision for one request: 200 with the JSON and the CORS origin to echo, or 403 with nothing.</summary>
internal readonly record struct StatusAnswer(int Status, string? Json, string? CorsOrigin);

/// <summary>
/// A tiny, read-only local web endpoint for the extension. It answers exactly ONE request:
///
///   GET /status   ->   {"consented":true,"trackingEnabled":true,"consentVersion":1}
///
/// and nothing else: no other path, no other method, no data transfer, nothing about what the user did.
///
/// It listens on a plain socket bound to 127.0.0.1, so other computers on the network cannot reach it.
/// (Windows' built-in HttpListener would not give that guarantee, which is why this is hand-written.)
///
/// A request is answered with 200 only if ALL of these hold; anything else gets 403 with no CORS headers:
///   * method GET and path /status,
///   * a Host header of exactly 127.0.0.1:port (this blocks DNS-rebinding tricks),
///   * an allowlisted extension: either an Origin that is an allowlisted extension origin, or no Origin but an
///     X-DLA-Extension-Id header with an allowlisted id (web pages are refused: they always send an Origin, or need a
///     preflight to send the header, and the preflight is refused).
///
/// It is created and started only by AgentHost, only after consent. A bind failure (port taken) never crashes
/// the agent: <see cref="Start"/> just returns false.
/// </summary>
public sealed class LocalStatusLink : IAgentLink
{
    private const int MaxHeaderBytes = 8192;
    private const int MaxConnections = 8;
    public const string ExtensionIdHeader = "X-DLA-Extension-Id";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly AgentLinkConfig _config;
    private readonly IStatusSource _status;
    private readonly IAgentLog? _log;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _active;

    public LocalStatusLink(AgentLinkConfig config, IStatusSource status, IAgentLog? log = null)
    {
        _config = config;
        _status = status;
        _log = log;
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
            _log?.Write($"Status link listening on 127.0.0.1:{_config.Port} (status only).");
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
    }

    public void Dispose() => Stop();

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
                var header = await ReadHeaderAsync(stream, timeout.Token);
                if (header is null) return;                   // too big, cut off or empty: just hang up

                var answer = Evaluate(header, _config, _status);
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

    /// <returns>The request line and headers (without the final blank line), or null if the request is too big or incomplete.</returns>
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), token);
            if (read == 0) return null;
            total += read;

            var end = IndexOfHeaderEnd(buffer, total);
            if (end >= 0) return Encoding.Latin1.GetString(buffer, 0, end);
        }
        return null;
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    // ---- the decision (pure, so it can be tested without a socket) -------------------------------------------------

    internal static StatusAnswer Evaluate(string headerBlock, AgentLinkConfig config, IStatusSource status)
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
            if (!headers.TryAdd(name, value)) return forbidden;                 // a repeated header (for example two Origins) is refused
        }

        if (method != "GET" || target != "/status") return forbidden;
        if (!headers.TryGetValue("Host", out var host) || host != $"127.0.0.1:{config.Port}") return forbidden;

        // Who is asking? Either an allowlisted extension Origin (answer gets a CORS header for exactly that origin),
        // or, when the browser sends no Origin at all (an extension GET with a host permission), the extension's
        // own id in X-DLA-Extension-Id (no CORS header: the extension does not need one). A request WITH an Origin
        // that is not allowlisted is refused whatever else it says.
        if (headers.TryGetValue("Origin", out var originHeader))
        {
            if (!config.TryGetAllowedOrigin(originHeader, out var origin)) return forbidden;
            return new StatusAnswer(200, status.GetStatus().ToJson(), origin);
        }

        if (headers.TryGetValue(ExtensionIdHeader, out var idHeader) && config.TryGetAllowedId(idHeader, out _))
            return new StatusAnswer(200, status.GetStatus().ToJson(), null);

        return forbidden;
    }

    private static bool IsToken(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '.' or '^' or '`' or '|' or '~');

    private static byte[] BuildResponse(StatusAnswer answer)
    {
        var text = new StringBuilder();
        if (answer.Status == 200)
        {
            var body = Encoding.UTF8.GetBytes(answer.Json!);
            text.Append("HTTP/1.1 200 OK\r\n");
            text.Append("Content-Type: application/json; charset=utf-8\r\n");
            text.Append($"Content-Length: {body.Length}\r\n");
            if (answer.CorsOrigin is not null)
                text.Append($"Access-Control-Allow-Origin: {answer.CorsOrigin}\r\n");   // only for an allowed extension origin
            text.Append("Vary: Origin\r\n");
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
