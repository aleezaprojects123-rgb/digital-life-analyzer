using System.Drawing;
using System.Windows.Forms;
using Dla.Agent.Consent;

namespace Dla.Agent.Ui;

/// <summary>
/// The consent window: a title, the terms and privacy text (scrollable), an UNCHECKED checkbox
/// "I have read and agree", and two buttons. Accept stays disabled until the box is ticked.
///
/// The decision is Declined unless the user presses Accept. Closing the window (the X, Alt+F4 or Esc) is a
/// Decline. There is no default button, so pressing Enter can never accept by accident.
/// This window only reports the decision. It does not save anything.
/// </summary>
public sealed class ConsentForm : Form
{
    internal readonly TextBox TermsBox;
    internal readonly CheckBox AgreeBox;
    internal readonly Button AcceptControl;
    internal readonly Button DeclineControl;

    public ConsentDecision Decision { get; private set; } = ConsentDecision.Declined;

    public ConsentForm()
    {
        Text = ConsentText.WindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(640, 640);

        var heading = new Label
        {
            Text = ConsentText.Heading,
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = false,
            Location = new Point(16, 12),
            Size = new Size(608, 32),
        };

        TermsBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = SystemColors.Window,
            Location = new Point(16, 52),
            Size = new Size(608, 450),
            Text = ConsentText.Body.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            TabStop = true,
        };

        AgreeBox = new CheckBox
        {
            Text = ConsentText.AgreeLabel,
            Checked = false,   // never ticked for the user
            AutoSize = true,
            Location = new Point(16, 516),
        };

        AcceptControl = new Button { Text = "Accept", Enabled = false, Size = new Size(140, 38), Location = new Point(344, 584) };
        DeclineControl = new Button { Text = "Decline", Size = new Size(140, 38), Location = new Point(484, 584) };

        AgreeBox.CheckedChanged += (_, _) => AcceptControl.Enabled = AgreeBox.Checked;

        AcceptControl.Click += (_, _) =>
        {
            if (!AgreeBox.Checked) return;   // belt and braces: Accept only counts with the box ticked
            Decision = ConsentDecision.Accepted;
            DialogResult = DialogResult.OK;
            Close();
        };

        DeclineControl.Click += (_, _) =>
        {
            Decision = ConsentDecision.Declined;
            DialogResult = DialogResult.Cancel;
            Close();
        };

        AcceptButton = null;             // no default button: Enter never accepts
        CancelButton = DeclineControl;   // Esc declines

        Controls.AddRange([heading, TermsBox, AgreeBox, AcceptControl, DeclineControl]);

        Shown += (_, _) => Activate();
    }
}
