namespace Dla.Agent.Tests;

/// <summary>A throwaway folder standing in for %LocalAppData%\DigitalLifeAnalyzer. Nothing real is ever touched.</summary>
internal sealed class TempData : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dla-agent-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>The data folder inside the throwaway root. It does NOT exist until something creates it.</summary>
    public string DataDir => Path.Combine(Root, "DigitalLifeAnalyzer");

    public string ConsentFile => Path.Combine(DataDir, "consent.json");

    public TempData() => Directory.CreateDirectory(Root);

    public void WriteConsentFile(string content)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConsentFile, content);
    }

    public string[] FilesCreated() =>
        Directory.Exists(DataDir) ? Directory.GetFileSystemEntries(DataDir) : [];

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

internal sealed class FakeLog : IAgentLog
{
    public List<string> Lines { get; } = [];
    public void Write(string message) => Lines.Add(message);
}
