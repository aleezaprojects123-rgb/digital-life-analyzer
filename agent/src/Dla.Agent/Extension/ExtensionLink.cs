using Dla.Agent.Lifecycle;

namespace Dla.Agent.Extension;

/// <summary>
/// Watches for extension heartbeats. When recording is allowed but no heartbeat arrives, the silent period is
/// written as an open "extension_silent" gap (start = last heartbeat) and closed when a heartbeat returns or
/// recording stops. The agent never guesses what happened in the browser during a gap: Phase 1 labels the part
/// of it that overlaps browser-foreground time as "unknown". The agent never tries to re-enable the extension.
/// The transport that delivers heartbeats is a Step 4 / Phase 1 placeholder; nothing calls OnHeartbeat yet.
/// </summary>
public sealed class ExtensionLink
{
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(90); // 3 missed 30-second heartbeats

    private readonly LifecycleLog _log;
    private readonly string _sessionId;
    private bool _watching;
    private bool _inGap;
    private DateTimeOffset _lastBeat;

    public ExtensionLink(LifecycleLog log, string sessionId)
    {
        _log = log;
        _sessionId = sessionId;
    }

    public bool InGap => _inGap;

    public void OnHeartbeat(DateTimeOffset now)
    {
        if (_inGap)
        {
            _log.Append(MarkerKind.ExtensionSilentEnd, _sessionId, "heartbeat-returned", now);
            _inGap = false;
        }
        _lastBeat = now;
        _watching = true;
    }

    /// <param name="canRecord">RecordingGate result: consent valid and not paused.</param>
    public void Check(DateTimeOffset now, bool canRecord)
    {
        if (!canRecord)
        {
            if (_inGap) _log.Append(MarkerKind.ExtensionSilentEnd, _sessionId, "recording-stopped", now);
            _inGap = false;
            _watching = false;
            return;
        }

        if (!_watching)
        {
            _watching = true;
            _lastBeat = now;
            return;
        }

        if (!_inGap && now - _lastBeat > SilentAfter)
        {
            _log.Append(MarkerKind.ExtensionSilentStart, _sessionId, "no-heartbeat", _lastBeat);
            _inGap = true;
        }
    }
}
