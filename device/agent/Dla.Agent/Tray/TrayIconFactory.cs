using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Dla.Agent.Tray;

/// <summary>
/// Draws the placeholder tray icon in code, so no image file and no download is needed.
/// A blue circle with a white ring while tracking is ON, grey while it is OFF (paused).
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Color On = Color.FromArgb(37, 99, 235);
    private static readonly Color Off = Color.FromArgb(130, 130, 130);

    public static Icon Create(bool trackingOn)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(trackingOn ? On : Off);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var ring = new Pen(Color.White, 3);
            g.DrawEllipse(ring, 9, 9, 14, 14);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();   // a copy we own; the native handle can be released now
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
