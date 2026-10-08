using Dla.Agent.Core;
using Dla.Agent.Supervision;
using Dla.Agent.Tray;

namespace Dla.Agent;

internal static class Program
{
    /// <summary>
    /// Dla.Agent.exe            -> supervisor (watchdog). This is what auto-start and the user run.
    /// Dla.Agent.exe --agent .. -> the agent itself, launched only by the supervisor.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);

        return args.Contains("--agent") ? RunAgent(args) : RunSupervisor();
    }

    private static int RunSupervisor()
    {
        using var instance = SingleInstance.TryAcquire(@"Local\DLA.Supervisor");
        if (instance is null) return ExitCodes.Clean; // a second copy does nothing

        using var context = new SupervisorContext();
        Application.Run(context);
        return ExitCodes.Clean;
    }

    private static int RunAgent(string[] args)
    {
        using var instance = SingleInstance.TryAcquire(@"Local\DLA.Agent");
        if (instance is null) return ExitCodes.AlreadyRunning;

        var sessionId = ArgValue(args, "--session") ?? Guid.NewGuid().ToString("N");
        var context = AgentContext.Create(sessionId, restarted: args.Contains("--restarted"));
        if (context is null) return ExitCodes.Clean; // consent declined: do nothing

        using (context) Application.Run(context);
        return ExitCodes.Clean;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Crash(Exception? ex)
    {
        Log.Write($"UNHANDLED: {ex}");
        Environment.Exit(ExitCodes.Crash); // no clean marker -> the supervisor restarts the agent
    }
}
