using System.Diagnostics;
using Dla.Agent.Core;
using Dla.Agent.Lifecycle;
using Dla.Agent.Tray;
using Microsoft.Win32;

namespace Dla.Agent.Supervision;

/// <summary>
/// The watchdog. It is what the HKCU Run entry starts. It launches the agent as a child process and relaunches it
/// after a crash. Only an end-session (or a consent decline) counts as a clean stop. While restarts are in the
/// 5-minute cooldown the supervisor shows its own tray icon, so a tray icon stays visible.
/// </summary>
public sealed class SupervisorContext : ApplicationContext
{
    private static readonly TimeSpan AlreadyRunningRetry = TimeSpan.FromSeconds(30);

    private readonly LifecycleLog _log = new(AppPaths.DataDir);
    private readonly RestartPolicy _policy = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private Process? _child;
    private string _sessionId = "";
    private DateTimeOffset _nextLaunch = DateTimeOffset.MinValue;
    private bool _restarted;
    private NotifyIcon? _recoveryTray;
    private Icon? _recoveryIcon;

    public SupervisorContext()
    {
        SystemEvents.SessionEnded += (_, _) => Environment.Exit(ExitCodes.Clean); // the agent writes its own marker
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        if (_child is null)
        {
            if (DateTimeOffset.Now >= _nextLaunch) Launch();
            return;
        }
        if (!_child.HasExited) return;

        var code = _child.ExitCode;
        _child.Dispose();
        _child = null;
        OnChildExited(code);
    }

    private void Launch()
    {
        _sessionId = Guid.NewGuid().ToString("N");
        var args = $"--agent --session {_sessionId}" + (_restarted ? " --restarted" : "");
        try
        {
            _child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
            _policy.OnStarted();
            HideRecoveryTray();
            Log.Write($"Supervisor launched agent session {_sessionId} (restarted={_restarted}).");
        }
        catch (Exception ex)
        {
            Log.Write($"Supervisor could not launch the agent: {ex.Message}");
            ScheduleRestart();
        }
    }

    private void OnChildExited(int code)
    {
        Log.Write($"Agent session {_sessionId} exited with code {code}.");

        if (code == ExitCodes.Clean || _log.HasCleanShutdown(_sessionId))
        {
            ExitThread(); // end-session or consent declined: stop supervising
            return;
        }
        if (code == ExitCodes.AlreadyRunning)
        {
            _nextLaunch = DateTimeOffset.Now + AlreadyRunningRetry; // an agent we did not start is running; keep watching
            return;
        }
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        var delay = _policy.OnCrash();
        _restarted = true;
        _nextLaunch = DateTimeOffset.Now + delay;
        Log.Write($"Agent crashed; restarting in {delay.TotalSeconds:0}s (cooldown={_policy.InCooldown}).");
        if (_policy.InCooldown) ShowRecoveryTray(delay);
    }

    private void ShowRecoveryTray(TimeSpan delay)
    {
        _recoveryIcon ??= TrayIcons.Make(TrayIcons.Inactive);
        _recoveryTray ??= new NotifyIcon { Icon = _recoveryIcon };
        _recoveryTray.Text = $"Digital Life Analyzer — recovering; retrying every {delay.TotalMinutes:0} min";
        _recoveryTray.Visible = true;
    }

    private void HideRecoveryTray()
    {
        if (_recoveryTray is not null) _recoveryTray.Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _recoveryTray?.Dispose();
            _recoveryIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
