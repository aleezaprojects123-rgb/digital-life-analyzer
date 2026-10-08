using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Dla.Agent.Consent;

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

public enum ConsentFormMode
{
    /// <summary>First run: Accept / Decline. Closing the window counts as Decline.</summary>
    FirstRun,
    /// <summary>Consent is active: Withdraw consent / Close.</summary>
    ReviewAccepted,
    /// <summary>Consent not active (withdrawn): Accept / Close.</summary>
    ReviewInactive
}

public enum ConsentChoice { Accept, Decline, Withdraw, Close }

public sealed class ConsentForm : Form
{
    public ConsentChoice Choice { get; private set; }

    public ConsentForm(ConsentFormMode mode, ConsentRecord record)
    {
        Text = ConsentText.Title;
        Icon = TrayIcons.Make(TrayIcons.Recording);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(560, 560);
        Font = new Font("Segoe UI", 10f);

        var heading = new Label
        {
            Text = ConsentText.Title,
            Font = new Font("Segoe UI Semibold", 14f),
            AutoSize = false,
            Location = new Point(16, 12),
            Size = new Size(528, 32)
        };

        var status = new Label
        {
            AutoSize = false,
            Location = new Point(16, 46),
            Size = new Size(528, 22),
            ForeColor = Color.DimGray,
            Text = StatusLine(mode, record)
        };

        var body = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Location = new Point(16, 74),
            Size = new Size(528, 410),
            Text = ConsentText.Body.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            TabStop = false
        };
        body.Select(0, 0);

        var primary = new Button { Size = new Size(160, 36), Location = new Point(ClientSize.Width - 16 - 160 - 12 - 120, 510) };
        var secondary = new Button { Size = new Size(120, 36), Location = new Point(ClientSize.Width - 16 - 120, 510) };

        switch (mode)
        {
            case ConsentFormMode.FirstRun:
                primary.Text = "Accept";
                secondary.Text = "Decline";
                primary.Click += (_, _) => Finish(ConsentChoice.Accept);
                secondary.Click += (_, _) => Finish(ConsentChoice.Decline);
                Choice = ConsentChoice.Decline;
                AcceptButton = primary;
                break;
            case ConsentFormMode.ReviewAccepted:
                primary.Text = "Withdraw consent";
                secondary.Text = "Close";
                primary.Click += (_, _) => Finish(ConsentChoice.Withdraw);
                secondary.Click += (_, _) => Finish(ConsentChoice.Close);
                Choice = ConsentChoice.Close;
                AcceptButton = secondary;
                break;
            default:
                primary.Text = "Accept";
                secondary.Text = "Close";
                primary.Click += (_, _) => Finish(ConsentChoice.Accept);
                secondary.Click += (_, _) => Finish(ConsentChoice.Close);
                Choice = ConsentChoice.Close;
                AcceptButton = secondary;
                break;
        }

        Controls.AddRange([heading, status, body, primary, secondary]);
    }

    private void Finish(ConsentChoice choice)
    {
        Choice = choice;
        Close();
    }

    public static string StatusLine(ConsentFormMode mode, ConsentRecord r)
    {
        // Accepted an earlier wording: consent is no longer valid until the new text is accepted.
        var outdated = r.State == ConsentState.Accepted && r.TextVersion != ConsentText.Version;
        return mode switch
        {
            ConsentFormMode.FirstRun => outdated
                ? "The privacy text has changed since you accepted it. Please read it again."
                : "Please read this before DLA starts.",
            ConsentFormMode.ReviewAccepted => $"You accepted on {r.AcceptedAt:f} (text version {r.TextVersion}). Recording is allowed.",
            _ when r.State == ConsentState.Withdrawn => $"You withdrew consent on {r.WithdrawnAt:f}. Nothing is being recorded.",
            _ when outdated => $"The privacy text changed after you accepted it (you accepted version {r.TextVersion}). Nothing is being recorded until you accept the new text.",
            _ => "Consent has not been given. Nothing is being recorded."
        };
    }
}
