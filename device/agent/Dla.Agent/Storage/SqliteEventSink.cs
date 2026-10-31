using Dla.Agent.Link;

namespace Dla.Agent.Storage;

/// <summary>
/// The main destination for finished events: the SQLite store. For now every stored event is also appended to the old JSONL file
/// (best effort, its failure never fails the write) so a rollback keeps working. The answer to the browser
/// is "stored" only when the database has committed, so the extension keeps an event and retries it until it is really saved.
/// </summary>
public sealed class SqliteEventSink : IEventSink
{
    private readonly SqliteEventStore _store;
    private readonly IEventSink? _mirror;
    private readonly IAgentLog? _log;

    public SqliteEventSink(SqliteEventStore store, IEventSink? jsonlMirror = null, IAgentLog? log = null)
    {
        _store = store;
        _mirror = jsonlMirror;
        _log = log;
    }

    public string DatabasePath => _store.Path;

    /// <summary>The open store, so reports can be read from the same database the events are written to.</summary>
    internal SqliteEventStore Store => _store;

    public bool AppendEvent(string jsonLine) => AppendEvents([jsonLine]);

    public bool AppendEvents(IReadOnlyList<string> jsonLines)
    {
        AppendResult result;
        try
        {
            result = _store.Append(jsonLines);
        }
        catch (Exception)
        {
            _log?.Write("An event could not be saved to the database; the browser will send it again.");
            return false;   // nothing was stored (one transaction), so a retry is safe
        }

        if (result.Rejected > 0) _log?.Write($"{result.Rejected} event line(s) were not valid events and were not stored.");   // a count only, never content
        if (result.Inserted > 0) _mirror?.AppendEvents(jsonLines);
        return true;
    }
}
