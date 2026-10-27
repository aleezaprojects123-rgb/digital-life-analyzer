using Microsoft.Win32;
using System.Windows.Forms;

namespace Dla.Agent;

/// <summary>The ways a process can end that are not "the user chose Quit". Real ones in <see cref="SystemExitSource"/>, fakes in tests.</summary>
internal interface IExitSource : IDisposable
{
    /// <summary>Windows is logging off or shutting down.</summary>
    event Action? SessionEnding;
    /// <summary>The message loop has finished (normal application exit).</summary>
    event Action? ApplicationExit;
    /// <summary>The process is about to end.</summary>
    event Action? ProcessExit;
    /// <summary>An unhandled exception, on the UI thread or any other thread. The argument may be null.</summary>
    event Action<Exception?>? Crash;
}

/// <summary>
/// Makes sure the tray icon (and the status link) are removed on EVERY way out, not only on Quit:
///   Quit                         the message loop ends and the application context disposes the host,
///   Windows logoff or shutdown   SessionEnding,
///   an unhandled exception       Crash (UI thread or any other thread),
///   normal application exit      ApplicationExit,
///   process exit                 ProcessExit.
/// Each one stops the host, which is safe to call many times. The only thing no code can do is run when the process
/// is force-ended (Task Manager) or killed: Windows then leaves a ghost icon until the mouse passes over it.
/// </summary>
internal sealed class ExitCleanup : IDisposable
{
    private readonly AgentHost _host;
    private readonly IExitSource _source;
    private readonly IAgentLog? _log;

    /// <summary>Asks the message loop to end (set once the loop exists). Used when Windows is ending the session.</summary>
    public Action? RequestExit { get; set; }

    public ExitCleanup(AgentHost host, IExitSource source, IAgentLog? log = null)
    {
        _host = host;
        _source = source;
        _log = log;
        source.SessionEnding += OnSessionEnding;
        source.ApplicationExit += Stop;
        source.ProcessExit += Stop;
        source.Crash += OnCrash;
    }

    private void OnSessionEnding()
    {
        Safely(() => RequestExit?.Invoke());
        Stop();
    }

    private void OnCrash(Exception? error)
    {
        // Only the kind of error is logged, never its message (which could contain paths or text).
        if (_host.IsRunning) Safely(() => _log?.Write($"Agent stopping after an unhandled error ({error?.GetType().Name ?? "unknown"})."));
        Stop();
    }

    private void Stop() => Safely(_host.Stop);

    private static void Safely(Action action)
    {
        try { action(); }
        catch (Exception) { /* cleanup must never throw while the process is going down */ }
    }

    public void Dispose()
    {
        _source.SessionEnding -= OnSessionEnding;
        _source.ApplicationExit -= Stop;
        _source.ProcessExit -= Stop;
        _source.Crash -= OnCrash;
    }
}

/// <summary>The real exit signals of Windows, WinForms and .NET.</summary>
internal sealed class SystemExitSource : IExitSource
{
    public event Action? SessionEnding;
    public event Action? ApplicationExit;
    public event Action? ProcessExit;
    public event Action<Exception?>? Crash;

    public SystemExitSource()
    {
        // Must be set before any window exists: errors on the UI thread then come to ThreadException instead of a dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += OnApplicationExit;
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => SessionEnding?.Invoke();
    private void OnApplicationExit(object? sender, EventArgs e) => ApplicationExit?.Invoke();
    private void OnProcessExit(object? sender, EventArgs e) => ProcessExit?.Invoke();
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) => Crash?.Invoke(e.ExceptionObject as Exception);

    private void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        Crash?.Invoke(e.Exception);   // removes the icon first
        Environment.Exit(1);          // an error on the UI thread leaves the agent in an unknown state: end it cleanly
    }

    public void Dispose()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        Application.ApplicationExit -= OnApplicationExit;
        Application.ThreadException -= OnThreadException;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }
}
