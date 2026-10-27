namespace Dla.Agent;

/// <summary>
/// Makes sure only one copy of the agent runs for this Windows user session, so two tray icons (or two consent
/// windows) can never appear. A second copy that starts finds the name taken and exits quietly.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name of the guard. "Local\" means one per Windows session.</summary>
    public const string AgentName = @"Local\DigitalLifeAnalyzer.Agent";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <returns>A guard if this is the first copy, otherwise null.</returns>
    public static SingleInstance? TryAcquire(string name = AgentName)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();   // the name is freed when the process ends or this is disposed
}
