using System.Windows.Forms;
using Dla.Agent.Consent;
using Dla.Agent.Link;
using Dla.Agent.Startup;
using Dla.Agent.Tray;
using Dla.Agent.Ui;

namespace Dla.Agent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // FIRST, before anything else exists: only one copy at a time. A refused copy returns here having created no
        // icon, no window, no listener and no file. The guard is held in a static field until the program ends.
        return AgentStartup.RunForThisProcess(RunAgent);
    }

    private static int RunAgent()
    {
        ApplicationConfiguration.Initialize();

        var store = new ConsentStore(ConsentStore.DefaultDataDir());
        var gate = new FileConsentGate(store);
        var state = new AgentState();
        var log = new FileAgentLog(store.DataDir);

        var onboarding = new OnboardingFlow(
            gate, store,
            askUser: ShowConsentWindow,
            log: log,
            utcNow: () => DateTimeOffset.UtcNow);

        // The tray and the local status link (for the browser extension) are created by AgentHost, and only after the consent gate says yes.
        using var host = new AgentHost(
            gate,
            createTray: () => new TrayController(state),
            createLink: () =>
            {
                var status = new AgentStatusProvider(gate, state);
                var media = new Dla.Agent.Media.MediaCollector(
                    new Dla.Agent.Media.SmtcMediaSource(),
                    allowed: () => status.GetStatus() is { Consented: true, TrackingEnabled: true },
                    log: log);
                var (sink, upkeep) = Dla.Agent.Storage.EventStorageFactory.Create(store.DataDir, JsonlEventSink.DefaultEventsFilePath(), log);
                // Reports are read from the same database the events are written to; with only the older log file there is no database to report from.
                Dla.Agent.Reports.IReportService? reports = sink is Dla.Agent.Storage.SqliteEventSink db
                    ? new Dla.Agent.Reports.ReportService(new Dla.Agent.Reports.ReportGenerator(db.Store))
                    : null;
                var ui = Dla.Agent.Link.UiAccess.Create(store.DataDir, log);     // the secret the separate window reads to ask for reports
                return new LocalStatusLink(AgentLinkConfig.Load(store.DataDir), status, log, sink: sink, media: media, owned: upkeep, reports: reports, ui: ui);
            },
            log: log,
            startup: new StartupRegistration(new RunKeyEntry(), log: log));     // the ONE start-with-Windows entry, set up only after consent

        // The icon is removed and the link is closed on every way out: Quit, logoff or shutdown, a crash, application exit and process exit.
        using var exitSource = new SystemExitSource();
        using var cleanup = new ExitCleanup(host, exitSource, log);

        var launcher = new AgentLauncher(onboarding, host, h => RunUntilQuit(h, cleanup), log);
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
    private static void RunUntilQuit(AgentHost host, ExitCleanup cleanup)
    {
        using var context = new AgentApplicationContext(host);

        // When Windows is ending the session, ask the message loop to finish (on its own thread) as well as stopping the host.
        if (SynchronizationContext.Current is null) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var ui = SynchronizationContext.Current;
        cleanup.RequestExit = () => ui?.Post(_ => context.ExitThread(), null);

        Application.Run(context);
    }
}
