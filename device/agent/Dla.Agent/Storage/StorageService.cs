namespace Dla.Agent.Storage;

/// <summary>
/// Keeps the database tidy and complete, on its own: it imports new lines of the older events.jsonl (the mirror of browser events) and applies the 30-day retention. Both run when it starts, then on a timer (import every 30 s, cleanup every 6 hours),
/// and a last import runs when it stops. Every step is idempotent and a failure is logged and tried again next time; it never throws.
/// </summary>
public sealed class StorageService : IDisposable
{
    public static readonly TimeSpan ImportEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(6);

    private readonly SqliteEventStore _store;
    private readonly string? _jsonlPath;
    private readonly IAgentLog? _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private bool _disposed;

    public StorageService(SqliteEventStore store, string? jsonlPath, IAgentLog? log = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _jsonlPath = jsonlPath;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Runs one round now, then keeps running on a timer.</summary>
    public void Start()
    {
        RunMaintenance();
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _timer = new System.Threading.Timer(_ => RunMaintenance(), null, ImportEvery, ImportEvery);
        }
    }

    /// <summary>Import new lines, then clean up if the last cleanup was 6 or more hours ago. Public so tests can run it without waiting.</summary>
    public void RunMaintenance()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_jsonlPath is not null)
                {
                    var r = _store.ImportJsonl(_jsonlPath);
                    if (r.Imported > 0) _log?.Write($"Imported {r.Imported} event(s) from the older log into the database.");
                }
                var now = _clock();
                if (now - _lastCleanup >= CleanupEvery)
                {
                    var deleted = _store.ApplyRetention();
                    _lastCleanup = now;
                    if (deleted > 0) _log?.Write($"Removed {deleted} event(s) older than {SqliteEventStore.RetentionDays} days.");
                }
            }
            catch (Exception ex)
            {
                _log?.Write($"Database upkeep failed ({ex.GetType().Name}); it will try again.");
            }
        }
    }

    public void Dispose()
    {
        System.Threading.Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        if (_jsonlPath is not null)
        {
            try { _store.ImportJsonl(_jsonlPath); } catch (Exception) { /* the next start imports it */ }
        }
        lock (_gate) _disposed = true;
        _store.Dispose();
    }
}
