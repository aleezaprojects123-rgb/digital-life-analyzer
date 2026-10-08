using Dla.Agent.Consent;
using Dla.Agent.Core;
using Dla.Agent.Extension;
using Dla.Agent.Lifecycle;
using Dla.Agent.Supervision;
using Microsoft.Win32;

namespace Dla.Agent.Tray;

/// <summary>
/// The agent: tray icon, consent, auto-start, lifecycle markers. It deliberately has no Quit command; the process
/// ends only when Windows ends the session (shutdown, restart, logoff). Phase 0 records no activity.
/// </summary>
public sealed class AgentContext : ApplicationContext
{
    private readonly string _sessionId;
    private readonly ConsentStore _consentStore;
    private readonly AgentSettingsStore _settingsStore;
    private readonly LifecycleLog _log;
    private readonly AutoStart _autoStart = CreateAutoStart();
    private readonly ExtensionLink _extension;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon _iconRecording = TrayIcons.Make(TrayIcons.Recording);
    private readonly Icon _iconPaused = TrayIcons.Make(TrayIcons.Paused);
    private readonly Icon _iconInactive = TrayIcons.Make(TrayIcons.Inactive);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15_000 };
    private ConsentRecord _consent;
    private AgentSettings _settings;

    private AgentContext(string sessionId, ConsentStore consentStore, AgentSettingsStore settingsStore, LifecycleLog log, ConsentRecord consent)
    {
        _sessionId = sessionId;
        _consentStore = consentStore;
        _settingsStore = settingsStore;
        _log = log;
        _consent = consent;
        _settings = settingsStore.Load();
        _extension = new ExtensionLink(log, sessionId);

        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        var dashboard = new ToolStripMenuItem("Open dashboard", null, (_, _) => MessageBox.Show(
            "The dashboard is a placeholder in Phase 0. It will open the web dashboard in a later phase.",
            "Digital Life Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information));
        var privacy = new ToolStripMenuItem("Consent and privacy", null, (_, _) => ShowConsentAndPrivacy());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_pauseItem, dashboard, privacy]); // intentionally no Quit

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowConsentAndPrivacy();

        SystemEvents.SessionEnded += OnSessionEnded;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _timer.Tick += (_, _) => _extension.Check(DateTimeOffset.Now, RecordingGate.CanRecord(_consent, _settings));
        _timer.Start();

        if (_consent.IsValidAccepted) _autoStart.Enable(Environment.ProcessPath!);
        RefreshUi();
    }

    // DLA_AUTOSTART_SUBKEY redirects the Run entry to a scratch registry key (dev/test only; never set in production).
    public static AutoStart CreateAutoStart() =>
        Environment.GetEnvironmentVariable("DLA_AUTOSTART_SUBKEY") is { Length: > 0 } sub ? new AutoStart(sub) : new AutoStart();

    /// <summary>
    /// What Decline does: save the decision, remove any auto-start entry (so a decline never starts at login, even if
    /// an earlier acceptance left one behind), and write the markers. Removing the entry must never stop the decline
    /// from completing.
    /// </summary>
    public static void RecordDecline(ConsentStore consentStore, LifecycleLog log, string sessionId, AutoStart autoStart)
    {
        consentStore.Decline();
        try
        {
            autoStart.Disable();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not remove the auto-start entry after Decline: {ex.Message}");
        }
        log.Append(MarkerKind.Start, sessionId, "declined");
        log.Append(MarkerKind.CleanShutdown, sessionId, "declined");
        Log.Write("Consent declined; auto-start removed; exiting.");
    }

    /// <summary>
    /// Starts the agent. Returns null when the user declines consent (the process then exits, does nothing,
    /// and nothing is registered to start at login). <paramref name="restarted"/> is true when the supervisor
    /// relaunched the agent after a crash: the consent window is not shown again in that case.
    /// </summary>
    public static AgentContext? Create(string sessionId, bool restarted)
    {
        var dir = AppPaths.DataDir;
        var consentStore = new ConsentStore(dir);
        var settingsStore = new AgentSettingsStore(dir);
        var log = new LifecycleLog(dir);

        log.Trim();
        var previous = log.PreviousRunStatus(sessionId);
        var consent = consentStore.Load();

        if (!consent.IsValidAccepted && !restarted)
        {
            using var form = new ConsentForm(ConsentFormMode.FirstRun, consent);
            form.ShowDialog();
            if (form.Choice != ConsentChoice.Accept)
            {
                RecordDecline(consentStore, log, sessionId, CreateAutoStart());
                return null;
            }
            consent = consentStore.Accept();
        }

        log.Append(MarkerKind.Start, sessionId, restarted ? "restarted-by-watchdog" : null);
        if (previous == PreviousRun.Crashed) log.Append(MarkerKind.CrashDetected, sessionId, "previous run ended without a clean-shutdown marker");
        Log.Write($"Agent started. consent={consent.State} previousRun={previous} restarted={restarted}");

        return new AgentContext(sessionId, consentStore, settingsStore, log, consent);
    }

    private void TogglePause()
    {
        _settings = _settings with { Paused = !_settings.Paused };
        _settingsStore.Save(_settings);
        Log.Write(_settings.Paused ? "Paused by user." : "Resumed by user.");
        RefreshUi();
    }

    private void ShowConsentAndPrivacy()
    {
        var mode = _consent.IsValidAccepted ? ConsentFormMode.ReviewAccepted : ConsentFormMode.ReviewInactive;
        using var form = new ConsentForm(mode, _consent);
        form.ShowDialog();

        if (form.Choice == ConsentChoice.Withdraw)
        {
            var sure = MessageBox.Show(
                "Withdraw consent?\n\nRecording stops now and DLA will no longer start when you log in.",
                "Digital Life Analyzer", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (sure != DialogResult.Yes) return;
            _consent = _consentStore.Withdraw();
            _autoStart.Disable();
            Log.Write("Consent withdrawn; recording stopped; auto-start removed.");
        }
        else if (form.Choice == ConsentChoice.Accept)
        {
            _consent = _consentStore.Accept();
            _autoStart.Enable(Environment.ProcessPath!);
            Log.Write("Consent accepted from tray; auto-start registered.");
        }
        RefreshUi();
    }

    private void RefreshUi()
    {
        var canRecord = RecordingGate.CanRecord(_consent, _settings);
        _extension.Check(DateTimeOffset.Now, canRecord);

        _pauseItem.Enabled = _consent.IsValidAccepted;
        _pauseItem.Text = _settings.Paused ? "Resume" : "Pause";

        if (!_consent.IsValidAccepted)
        {
            _tray.Icon = _iconInactive;
            _tray.Text = "Digital Life Analyzer — not recording (no consent)";
        }
        else if (_settings.Paused)
        {
            _tray.Icon = _iconPaused;
            _tray.Text = "Digital Life Analyzer — paused";
        }
        else
        {
            _tray.Icon = _iconRecording;
            _tray.Text = "Digital Life Analyzer — running";
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _log.Append(MarkerKind.Suspend, _sessionId);
        else if (e.Mode == PowerModes.Resume) _log.Append(MarkerKind.Resume, _sessionId);
    }

    /// <summary>
    /// Raised only when Windows is really ending the session (shutdown, restart, logoff), not on a cancelable query.
    /// Write the marker first, then leave. Windows may terminate the process right after this returns.
    /// </summary>
    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        try
        {
            _log.Append(MarkerKind.CleanShutdown, _sessionId, e.Reason.ToString());
            Log.Write($"Session ending ({e.Reason}); clean-shutdown marker written.");
            _tray.Visible = false;
        }
        finally
        {
            Environment.Exit(ExitCodes.Clean);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnded -= OnSessionEnded;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
