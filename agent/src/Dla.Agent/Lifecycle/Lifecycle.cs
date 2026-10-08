using System.Text.Json;

namespace Dla.Agent.Lifecycle;

public static class MarkerKind
{
    public const string Start = "start";
    public const string CleanShutdown = "clean_shutdown";
    public const string Suspend = "suspend";
    public const string Resume = "resume";
    public const string CrashDetected = "crash_detected";
    public const string ExtensionSilentStart = "extension_silent_start";
    public const string ExtensionSilentEnd = "extension_silent_end";
}

public sealed record LifecycleMarker(DateTimeOffset At, string Kind, string SessionId, string? Detail);

public enum PreviousRun { FirstRun, Clean, EndedWhileSuspended, Crashed }

/// <summary>
/// Append-only marker log (JSON lines) so Phase 1 can tell a clean stop from a crash.
/// Phase 0 keeps it in a file; Step 5 moves the same records into the SQLite lifecycle_markers table.
/// </summary>
public sealed class LifecycleLog
{
    private static readonly object Gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;

    public LifecycleLog(string dataDir, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(dataDir, "lifecycle.jsonl");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Append(string kind, string sessionId, string? detail = null, DateTimeOffset? at = null)
    {
        // One compact JSON object + "\n". If a previous crash left a torn last line with no newline, terminate it
        // first so this record starts on its own line instead of being glued onto (and lost with) the torn one.
        var json = JsonSerializer.Serialize(new LifecycleMarker(at ?? _now(), kind, sessionId, detail));
        lock (Gate)
        {
            using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var needsNewline = false;
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                needsNewline = fs.ReadByte() != '\n';
            }
            fs.Seek(0, SeekOrigin.End);
            var bytes = new System.Text.UTF8Encoding(false).GetBytes((needsNewline ? "\n" : "") + json + "\n");
            fs.Write(bytes, 0, bytes.Length); // single write: one record is never split across calls
        }
    }

    public IReadOnlyList<LifecycleMarker> ReadAll()
    {
        if (!File.Exists(_path)) return [];
        var result = new List<LifecycleMarker>();
        string[] lines;
        lock (Gate)
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            lines = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        foreach (var line in lines)
        {
            try
            {
                var m = JsonSerializer.Deserialize<LifecycleMarker>(line);
                if (m is not null) result.Add(m);
            }
            catch (JsonException) { /* a torn last line after a crash is ignored */ }
        }
        return result;
    }

    public bool HasCleanShutdown(string sessionId) =>
        ReadAll().Any(m => m.SessionId == sessionId && m.Kind == MarkerKind.CleanShutdown);

    /// <summary>How the most recent earlier session ended, judged only by its own markers.</summary>
    public PreviousRun PreviousRunStatus(string currentSessionId)
    {
        foreach (var m in ReadAll().AsEnumerable().Reverse())
        {
            if (m.SessionId == currentSessionId) continue;
            switch (m.Kind)
            {
                case MarkerKind.CleanShutdown: return PreviousRun.Clean;
                case MarkerKind.Suspend: return PreviousRun.EndedWhileSuspended;
                case MarkerKind.Start:
                case MarkerKind.Resume: return PreviousRun.Crashed;
            }
        }
        return PreviousRun.FirstRun;
    }

    /// <summary>Keeps the file small. Called once at agent start.</summary>
    public void Trim(int keepLast = 2000, int triggerAt = 5000)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(_path)) return;
                var lines = File.ReadAllLines(_path);
                if (lines.Length <= triggerAt) return;
                File.WriteAllLines(_path, lines.Skip(lines.Length - keepLast));
            }
        }
        catch (IOException) { }
    }
}
