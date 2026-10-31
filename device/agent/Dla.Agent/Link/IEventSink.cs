namespace Dla.Agent.Link;

/// <summary>
/// A destination for completed activity events.
/// Appends single-line JSON representations of events to storage.
/// </summary>
public interface IEventSink
{
    bool AppendEvent(string jsonLine);

    /// <summary>Appends several lines together: all of them or none, so a retry never leaves half a split event.</summary>
    bool AppendEvents(IReadOnlyList<string> jsonLines)
    {
        if (jsonLines.Count == 1) return AppendEvent(jsonLines[0]);
        return jsonLines.All(AppendEvent);
    }
}
