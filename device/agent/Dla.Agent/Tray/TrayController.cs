using System.Drawing;
using System.Windows.Forms;

namespace Dla.Agent.Tray;

/// <summary>
/// The future system-tray icon: a NotifyIcon with a right-click menu (Tracking: ON, Open dashboard, Quit).
///
/// INTENTIONALLY NOT USED YET. Nothing creates this class today. Only AgentHost may create it, and only after
/// the consent gate says yes, so no tray icon can appear before the user has explicitly accepted.
/// When it is used later: "Tracking: ON" will toggle tracking and "Open dashboard" will open the website.
/// Today those two items do nothing, and the icon is a placeholder.
/// </summary>
public sealed class TrayController : ITray
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public event EventHandler? QuitRequested;

    public TrayController()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("Tracking: ON"));     // does nothing yet
        _menu.Items.Add(new ToolStripMenuItem("Open dashboard"));   // does nothing yet
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,   // placeholder icon
            Text = "Digital Life Analyzer",
            ContextMenuStrip = _menu,
            Visible = false,                  // stays hidden until Show() is called
        };
    }

    public void Show() => _icon.Visible = true;

    public void Dispose()
    {
        _icon.Visible = false;   // removes the icon from the tray so no ghost icon is left behind
        _icon.Dispose();
        _menu.Dispose();
    }
}
