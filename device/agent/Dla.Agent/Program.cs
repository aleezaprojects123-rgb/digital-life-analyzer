using System.Windows.Forms;
using Dla.Agent.Consent;
using Dla.Agent.Tray;
using Dla.Agent.Ui;

namespace Dla.Agent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Only one copy at a time: no second tray icon and no second consent window.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return 0;

        ApplicationConfiguration.Initialize();

        var store = new ConsentStore(ConsentStore.DefaultDataDir());
        var gate = new FileConsentGate(store);
        var state = new AgentState();

        var onboarding = new OnboardingFlow(
            gate, store,
            askUser: ShowConsentWindow,
            log: new FileAgentLog(store.DataDir),
            utcNow: () => DateTimeOffset.UtcNow);

        // The tray is created by AgentHost, and only after the consent gate says yes.
        using var host = new AgentHost(gate, () => new TrayController(state));

        var launcher = new AgentLauncher(onboarding, host, RunUntilQuit);
        var result = launcher.Launch();

        if (result.Outcome == OnboardingOutcome.SaveFailed)
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

    /// <summary>Lives in the tray with no window until the user chooses Quit.</summary>
    private static void RunUntilQuit(AgentHost host)
    {
        using var context = new AgentApplicationContext(host);
        Application.Run(context);
    }
}
