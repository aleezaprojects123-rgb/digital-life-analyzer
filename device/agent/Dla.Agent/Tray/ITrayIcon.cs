using System.Drawing;
using System.Windows.Forms;

namespace Dla.Agent.Tray;

/// <summary>
/// The few things TrayController needs from a Windows notification icon. It exists so the "exactly one icon per
/// process" rule can be tested with a fake, without ever putting a real icon in the tray.
/// </summary>
internal interface ITrayIcon : IDisposable
{
    bool Visible { get; set; }
    Icon? Icon { get; set; }
    string Text { get; set; }
    ContextMenuStrip? ContextMenuStrip { get; set; }
}

/// <summary>The real thing: a WinForms NotifyIcon. WinForms re-adds it by itself if Explorer restarts (TaskbarCreated).</summary>
internal sealed class WinFormsTrayIcon : ITrayIcon
{
    private readonly NotifyIcon _icon = new() { Visible = false };

    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }
    public Icon? Icon { get => _icon.Icon; set => _icon.Icon = value; }
    public string Text { get => _icon.Text; set => _icon.Text = value; }
    public ContextMenuStrip? ContextMenuStrip { get => _icon.ContextMenuStrip; set => _icon.ContextMenuStrip = value; }

    public void Dispose() => _icon.Dispose();
}
