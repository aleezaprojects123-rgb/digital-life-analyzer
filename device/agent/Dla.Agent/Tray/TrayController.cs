using System.Drawing;
using System.Windows.Forms;

namespace Dla.Agent.Tray;

/// <summary>
/// The system-tray icon and its right-click menu:
///   "Tracking: ON" / "Tracking: OFF"  toggles the agent's pause state, and the label shows it,
///   "Open dashboard (coming later)"   disabled until the dashboard exists,
///   "Quit"                            removes the icon and lets the program end.
///
/// Only AgentHost creates this class, and only after the consent gate says yes. Nothing exists in the tray until
/// Show() is called. The toggle changes only <see cref="AgentState"/>: nothing is tracked yet.
///
/// EXACTLY ONE ICON PER PROCESS. The Windows icon is created lazily inside Show(), and a process-wide slot allows
/// only one controller to own it. A second Show() (on the same controller or on another one) does nothing. Dispose
/// hides and disposes the icon and gives the slot back.
/// </summary>
public sealed class TrayController : ITray
{
    public const string DashboardLabel = "Open dashboard (coming later)";
    public const string QuitLabel = "Quit";
    public const string ToolTip = "Digital Life Analyzer (tracking not built yet)";

    private static int s_iconOwners;   // 0 or 1: whether some controller in this process owns the one tray icon

    private readonly AgentState _state;
    private readonly Func<ITrayIcon> _createIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _trackingItem;
    private readonly Icon _iconOn = TrayIconFactory.Create(trackingOn: true);
    private readonly Icon _iconOff = TrayIconFactory.Create(trackingOn: false);
    private readonly object _gate = new();
    private ITrayIcon? _icon;
    private bool _ownsSlot;
    private bool _disposed;

    public event EventHandler? QuitRequested;

    public TrayController(AgentState state) : this(state, () => new WinFormsTrayIcon()) { }

    internal TrayController(AgentState state, Func<ITrayIcon> createIcon)
    {
        _state = state;
        _createIcon = createIcon;

        _trackingItem = new ToolStripMenuItem();
        _trackingItem.Click += (_, _) => _state.Toggle();

        var dashboard = new ToolStripMenuItem(DashboardLabel) { Enabled = false };
        var quit = new ToolStripMenuItem(QuitLabel);
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([_trackingItem, dashboard, quit]);

        _state.Changed += OnStateChanged;
        Refresh();
    }

    /// <summary>The menu label for a tracking value.</summary>
    public static string TrackingLabel(bool trackingEnabled) => $"Tracking: {(trackingEnabled ? "ON" : "OFF")}";

    internal ContextMenuStrip Menu => _menu;

    /// <summary>True while this process owns a tray icon (for tests).</summary>
    internal static bool ProcessOwnsIcon => Volatile.Read(ref s_iconOwners) == 1;

    /// <summary>Puts the one icon in the tray. Does nothing if it is already shown, or if another controller owns the process's icon.</summary>
    public void Show()
    {
        lock (_gate)
        {
            if (_disposed || _icon is not null) return;
            if (Interlocked.CompareExchange(ref s_iconOwners, 1, 0) != 0) return;   // the process already has its one icon
            _ownsSlot = true;

            ITrayIcon? icon = null;
            try
            {
                icon = _createIcon();
                icon.Text = ToolTip;
                icon.ContextMenuStrip = _menu;
                icon.Icon = _state.TrackingEnabled ? _iconOn : _iconOff;
                icon.Visible = true;
                _icon = icon;
            }
            catch
            {
                icon?.Dispose();
                ReleaseSlot();
                throw;
            }
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        _trackingItem.Text = TrackingLabel(_state.TrackingEnabled);
        lock (_gate)
        {
            if (_icon is not null && !_disposed) _icon.Icon = _state.TrackingEnabled ? _iconOn : _iconOff;
        }
    }

    private void ReleaseSlot()
    {
        if (!_ownsSlot) return;
        _ownsSlot = false;
        Interlocked.Exchange(ref s_iconOwners, 0);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _state.Changed -= OnStateChanged;
            if (_icon is not null)
            {
                try { _icon.Visible = false; }   // removes the icon from the tray so no ghost icon is left behind
                finally { _icon.Dispose(); }
                _icon = null;
            }
            ReleaseSlot();
        }
        _menu.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
    }
}
