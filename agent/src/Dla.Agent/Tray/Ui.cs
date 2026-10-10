using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Dla.Agent.Tray;

public static class TrayIcons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    public static readonly Color Recording = Color.FromArgb(34, 160, 90);
    public static readonly Color Paused = Color.FromArgb(230, 160, 20);
    public static readonly Color Inactive = Color.FromArgb(130, 130, 130);

    /// <summary>Draws a simple round icon in code (no image assets needed in Phase 0).</summary>
    public static Icon Make(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3);
            g.DrawArc(pen, 9, 9, 14, 14, 200, 280); // a stylised "D" ring
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally { DestroyIcon(handle); }
    }
}
