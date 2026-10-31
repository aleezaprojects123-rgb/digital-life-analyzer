using System.Text;
using System.Text.Json;

namespace Dla.Agent.Link;

/// <summary>
/// Appends event lines to a JSONL file (by default device/agent/data/events.jsonl).
/// Uses FileShare.ReadWrite to avoid locking conflicts with other readers/writers.
/// Ensures each event is written as a single line JSON object.
/// </summary>
public sealed class JsonlEventSink : IEventSink
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public JsonlEventSink(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public static string DefaultEventsFilePath(string? deviceDir = null)
    {
        var dir = deviceDir ?? ResolveDeviceDirectory();
        if (dir is null)
        {
            return Path.Combine("agent", "data", "events.jsonl");
        }
        return Path.Combine(dir, "agent", "data", "events.jsonl");
    }

    /// <summary>
    /// Finds the "device" directory of the repository (the one that holds agent/Dla.Agent), so the mirror file stays where it always was:
    /// device/agent/data/events.jsonl. Works from the repo root, from the device folder, or from the build output directory.
    /// </summary>
    public static string? ResolveDeviceDirectory()
    {
        const string marker = "Dla.Agent.csproj";
        var current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "agent", "Dla.Agent", marker))) return current;
        if (File.Exists(Path.Combine(current, "device", "agent", "Dla.Agent", marker))) return Path.Combine(current, "device");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "agent", "Dla.Agent", marker))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "device", "agent", "Dla.Agent", marker))) return Path.Combine(dir.FullName, "device");
            dir = dir.Parent;
        }
        return null;
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public bool AppendEvent(string jsonLine) => AppendEvents([jsonLine]);

    public bool AppendEvents(IReadOnlyList<string> jsonLines)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var text = new StringBuilder();
                foreach (var jsonLine in jsonLines)
                {
                    string singleLine;
                    try
                    {
                        using var doc = JsonDocument.Parse(jsonLine);
                        singleLine = JsonSerializer.Serialize(doc.RootElement, Relaxed);
                    }
                    catch
                    {
                        singleLine = jsonLine.Replace("\r", "").Replace("\n", "");
                    }
                    text.Append(singleLine).Append('\n');
                }

                // One write of all the lines, no byte order mark, so the file stays plain, valid JSON lines.
                var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
