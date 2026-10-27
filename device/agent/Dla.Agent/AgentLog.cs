namespace Dla.Agent;

/// <summary>Where the agent writes its own status lines. Never activity data.</summary>
public interface IAgentLog
{
    void Write(string message);
}

/// <summary>
/// A tiny status log: %LocalAppData%\DigitalLifeAnalyzer\agent.log. It is written only AFTER the user has
/// consented, it holds only the agent's own state (for example "consent accepted"), and it never contains
/// websites, titles or anything the user did. It keeps itself small and never throws.
/// </summary>
public sealed class FileAgentLog : IAgentLog
{
    private const long MaxBytes = 100 * 1024;
    private readonly string _path;

    public FileAgentLog(string dataDir) => _path = Path.Combine(dataDir, "agent.log");

    public void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) File.Delete(_path);
            File.AppendAllText(_path, $"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // logging must never break the agent
        }
    }
}
