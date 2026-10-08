using System.Text.Json;

namespace Dla.Agent.Core;

/// <summary>Process exit codes. The supervisor restarts the agent on anything except Clean.</summary>
public static class ExitCodes
{
    public const int Clean = 0;          // end-session, or the user declined consent
    public const int Crash = 1;          // unhandled exception
    public const int AlreadyRunning = 3; // another agent holds the single-instance mutex
}

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\DLA, or DLA_DATA_DIR when set (used by tests and manual experiments).</summary>
    public static string DataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("DLA_DATA_DIR");
            var dir = string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLA")
                : overrideDir;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

/// <summary>Process-wide single-instance guard (named mutex, no ownership needed).</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <returns>A guard if this is the first instance, otherwise null.</returns>
    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}

/// <summary>Writes JSON atomically so a crash mid-write cannot corrupt state files.</summary>
public static class AtomicFile
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static void WriteJson<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // unreadable state is treated as "not set" -> the safe default (nothing recorded)
        }
    }
}

/// <summary>Tiny diagnostic log. Never contains activity data.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "agent.log");
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the agent */ }
    }
}
