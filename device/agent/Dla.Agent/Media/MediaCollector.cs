namespace Dla.Agent.Media;

/// <summary>
/// Keeps the <see cref="MediaTimeline"/> up to date while recording is allowed. When consent is withdrawn or tracking is
/// paused it records nothing and closes whatever was open. A slow timer is only a safety net; changes arrive as events.
/// </summary>
public sealed class MediaCollector : IDisposable
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(3);

    private readonly IMediaSource _source;
    private readonly Func<bool> _allowed;
    private readonly IAgentLog? _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private System.Threading.Timer? _timer;
    private int _dirty;
    private volatile bool _ready;
    private int _lastCount = -1;

    public MediaTimeline Timeline { get; } = new();

    public MediaCollector(IMediaSource source, Func<bool> allowed, IAgentLog? log = null, Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _allowed = allowed;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ = Task.Run(async () =>
        {
            _ready = await _source.InitializeAsync();
            _log?.Write(_ready ? "Media controls: listening." : "Media controls: not available on this PC.");
            if (!_ready) return;
            _source.Changed += () => _ = RefreshAsync();
            await RefreshAsync();
            _timer?.Change(Safety, Safety);
        });
    }

    /// <summary>For tests: start without a background task.</summary>
    public async Task StartForTestAsync()
    {
        _ready = await _source.InitializeAsync();
        if (_ready) _source.Changed += () => _ = RefreshAsync();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_ready) return;
        Interlocked.Exchange(ref _dirty, 1);
        while (await _busy.WaitAsync(0))                // one refresh at a time; the running one repeats if dirty
        {
          try
          {
            while (Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                var states = _allowed() ? await _source.SnapshotAsync() : [];
                Timeline.Observe(_clock(), states);
                var count = states.Count(s => s.HasContent);
                if (count != _lastCount)
                {
                    _lastCount = count;
                    _log?.Write($"Media controls: {count} session(s) with media.");   // a number only, never a title
                }
            }
          }
          catch (Exception)
          {
            // never let a media problem touch the rest of the agent
          }
          finally
          {
            _busy.Release();
          }
          if (Volatile.Read(ref _dirty) == 0) return;   // a change that arrived just as the last pass ended is not lost
        }
    }

    /// <summary>The event line(s) to store for one finished browser period: unchanged, or with media attached and split at media changes.</summary>
    public IReadOnlyList<string> Enrich(string eventJson) =>
        MediaEnricher.Enrich(eventJson, Timeline, _clock(), reason => _log?.Write($"Media for browser event: {reason}."));   // a reason code only, never a title

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _source.Dispose();
    }
}
