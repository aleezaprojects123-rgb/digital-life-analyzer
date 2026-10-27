using System.Windows.Forms;
using Dla.Agent.Consent;
using Dla.Agent.Ui;

namespace Dla.Agent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();

        var store = new ConsentStore(ConsentStore.DefaultDataDir());
        var flow = new OnboardingFlow(
            new FileConsentGate(store),
            store,
            askUser: ShowConsentWindow,
            log: new FileAgentLog(store.DataDir),
            utcNow: () => DateTimeOffset.UtcNow);

        // No tray and no tracking in this version. Task 2 adds the tray, created only after consent.
        var outcome = flow.Run();

        if (outcome == OnboardingOutcome.SaveFailed)
        {
            MessageBox.Show(
                "Your choice could not be saved, so nothing is recorded. Please start the app again.",
                ConsentText.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return 0;
    }

    private static ConsentDecision ShowConsentWindow()
    {
        using var form = new ConsentForm();
        form.ShowDialog();
        return form.Decision;
    }
}
