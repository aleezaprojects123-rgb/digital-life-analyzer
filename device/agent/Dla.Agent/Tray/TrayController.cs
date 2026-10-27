using System.Drawing;
using System.Windows.Forms;

namespace Dla.Agent.Tray;

/// <summary>
/// The system-tray icon and its right-click menu:
///   "Tracking: ON" / "Tracking: OFF"  toggles the agent's pause state, and the label shows it,
///   "Open dashboard (coming later)"   disabled until the dashboard exists,
///   "Quit"                            removes the icon and lets the program end.
///
/// Only AgentHost creates this class, and only after the consent gate says yes. The icon stays hidden until
/// Show() is called. The toggle changes only <see cref="AgentState"/>: nothing is tracked yet.
/// </summary>
public sealed class TrayController : ITray
{
    public const string DashboardLabel = "Open dashboard (coming later)";
    public const string QuitLabel = "Quit";
    public const string ToolTip = "Digital Life Analyzer (tracking not built yet)";

    private readonly AgentState _state;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _trackingItem;
    private readonly Icon _iconOn = TrayIconFactory.Create(trackingOn: true);
    private readonly Icon _iconOff = TrayIconFactory.Create(trackingOn: false);
    private bool _disposed;

    public event EventHandler? QuitRequested;

    public TrayController(AgentState state)
    {
        _state = state;

        _trackingItem = new ToolStripMenuItem();
        _trackingItem.Click += (_, _) => _state.Toggle();

        var dashboard = new ToolStripMenuItem(DashboardLabel) { Enabled = false };
        var quit = new ToolStripMenuItem(QuitLabel);
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([_trackingItem, dashboard, quit]);

        _icon = new NotifyIcon { Text = ToolTip, ContextMenuStrip = _menu, Visible = false };   // hidden until Show()

        _state.Changed += OnStateChanged;
        Refresh();
    }

    /// <summary>The menu label for a tracking value.</summary>
    public static string TrackingLabel(bool trackingEnabled) => $"Tracking: {(trackingEnabled ? "ON" : "OFF")}";

    internal ContextMenuStrip Menu => _menu;

    public void Show() => _icon.Visible = true;

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        _trackingItem.Text = TrackingLabel(_state.TrackingEnabled);
        _icon.Icon = _state.TrackingEnabled ? _iconOn : _iconOff;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnStateChanged;
        _icon.Visible = false;   // removes the icon from the tray so no ghost icon is left behind
        _icon.Dispose();
        _menu.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
    }
}
