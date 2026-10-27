namespace Dla.Agent;

/// <summary>
/// The very first step of the program, kept apart from Program so it can be tested: take the single-instance guard
/// BEFORE anything else exists. A refused copy gets null here and returns at once: the "run the agent" step is
/// never called, so it creates no icon, no window, no listener and writes no file.
/// </summary>
internal static class AgentStartup
{
    /// <summary>What Program uses: the real guard for this process, then the agent.</summary>
    public static int RunForThisProcess(Func<int> runAgent) => Run(() => SingleInstance.AcquireForProcess(), runAgent);

    /// <returns>0 when this copy is refused. Otherwise whatever the agent returns.</returns>
    public static int Run(Func<SingleInstance?> acquireGuard, Func<int> runAgent)
    {
        using var guard = acquireGuard();
        if (guard is null) return 0;      // another copy is running: exit quietly, nothing else is touched
        return runAgent();                // the guard is released when this returns or throws
    }
}
