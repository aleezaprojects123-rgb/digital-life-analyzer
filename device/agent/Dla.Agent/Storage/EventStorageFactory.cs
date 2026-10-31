using Dla.Agent.Link;

namespace Dla.Agent.Storage;

/// <summary>
/// Opens the event database and its upkeep, once, when the link starts (so only after consent). SQLite is the main store
/// (%LocalAppData%\DigitalLifeAnalyzer\dla.db). If the database cannot be opened at all, events go to the older JSONL file instead of being lost.
/// </summary>
public static class EventStorageFactory
{
    public const string DatabaseFileName = "dla.db";

    public static (IEventSink Sink, IDisposable? Upkeep) Create(string dataDir, string jsonlPath, IAgentLog? log = null, Func<DateTimeOffset>? clock = null)
    {
        var jsonl = new JsonlEventSink(jsonlPath);
        try
        {
            var db = SqliteEventStore.Open(Path.Combine(dataDir, DatabaseFileName), clock, log);
            var upkeep = new StorageService(db, jsonl.FilePath, log, clock);
            upkeep.Start();
            return (new SqliteEventSink(db, jsonl, log), upkeep);
        }
        catch (Exception ex)
        {
            log?.Write($"The event database could not be opened ({ex.GetType().Name}); events go to the older log file for now.");
            return (jsonl, null);
        }
    }
}
