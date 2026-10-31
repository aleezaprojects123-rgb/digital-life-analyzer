using System.Security.Principal;

namespace Dla.Agent;

/// <summary>
/// The one guard for the whole product: only one copy of the agent runs for a Windows user, so two tray icons (or
/// two consent windows) can never appear. A second copy finds the name taken and exits quietly.
///
/// The name depends only on WHO is logged in: "Local\" (one per Windows session) plus the user's SID. It never
/// depends on the data folder, the working directory, the build configuration or a test setting, so every launch by
/// the same user, from anywhere, asks for the same name. Two users, or two sessions, never block each other.
///
/// The guard that the program itself holds lives in a STATIC field for as long as the process runs, so the garbage
/// collector can never release it. The mutex is not "owned" (nothing waits on it), so there is no abandoned-mutex
/// case; Windows frees the name when the guard is disposed or the process ends, even if it is killed.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Prefix = @"Local\DigitalLifeAnalyzer.Agent";

    /// <summary>The name of the guard for the current Windows user in the current session.</summary>
    public static readonly string AgentName = BuildAgentName();

    private static readonly object Sync = new();
    private static SingleInstance? s_processGuard;   // the guard this process holds: a static field, so it is never collected

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>"Local\DigitalLifeAnalyzer.Agent.&lt;user SID&gt;". Depends on nothing but the logged-in user.</summary>
    public static string BuildAgentName()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            return string.IsNullOrEmpty(sid) ? Prefix : $"{Prefix}.{sid}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Prefix;
        }
    }

    /// <summary>True while this process holds the guard it got from <see cref="AcquireForProcess"/>.</summary>
    public static bool ProcessGuardHeld
    {
        get { lock (Sync) return s_processGuard is not null; }
    }

    /// <returns>A guard if this is the first copy, otherwise null. (Not kept in the static field: used by tests and by AcquireForProcess.)</returns>
    public static SingleInstance? TryAcquire(string? name = null)
    {
        var mutex = new Mutex(initiallyOwned: false, name ?? AgentName, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// What Program calls, first thing. Gets the guard and keeps it in the static field until it is disposed.
    /// Returns null (and changes nothing) if another copy, or an earlier call in this process, already holds it.
    /// </summary>
    public static SingleInstance? AcquireForProcess(string? name = null)
    {
        lock (Sync)
        {
            if (s_processGuard is not null) return null;
            var guard = TryAcquire(name);
            s_processGuard = guard;
            return guard;
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (ReferenceEquals(s_processGuard, this)) s_processGuard = null;
        }
        _mutex.Dispose();   // frees the name; also freed by Windows when the process ends
    }
}
