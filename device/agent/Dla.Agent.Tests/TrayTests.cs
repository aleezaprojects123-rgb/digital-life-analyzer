using System.Windows.Forms;
using Dla.Agent.Consent;
using Dla.Agent.Tray;
using Dla.Agent.Ui;

namespace Dla.Agent.Tests;

public class AgentStateTests
{
    [Fact]
    public void Tracking_is_ON_by_default()
    {
        Assert.True(new AgentState().TrackingEnabled);
    }

    [Fact]
    public void Toggle_flips_the_value_each_time_and_reports_the_new_one()
    {
        var state = new AgentState();
        Assert.False(state.Toggle());
        Assert.False(state.TrackingEnabled);
        Assert.True(state.Toggle());
        Assert.True(state.TrackingEnabled);
    }

    [Fact]
    public void Changed_fires_only_when_the_value_really_changes()
    {
        var state = new AgentState();
        var count = 0;
        state.Changed += (_, _) => count++;

        state.SetTrackingEnabled(true);    // already ON: no change
        Assert.Equal(0, count);
        state.SetTrackingEnabled(false);
        state.SetTrackingEnabled(false);   // already OFF: no change
        state.Toggle();
        Assert.Equal(2, count);
    }

    [Fact]
    public void The_state_is_only_a_value_it_holds_nothing_about_consent_or_activity()
    {
        var members = typeof(AgentState).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => !n.StartsWith("get_") && !n.StartsWith("set_") && !n.StartsWith("add_") && !n.StartsWith("remove_") && n != ".ctor")
            .OrderBy(n => n).ToArray();
        Assert.Equal(["Changed", "SetTrackingEnabled", "Toggle", "TrackingEnabled"], members);
    }
}

/// <summary>Tests the real TrayController without ever showing the icon (it stays hidden until Show() is called).</summary>
public class TrayControllerTests
{
    private static ToolStripItem[] Items(TrayController tray) => tray.Menu.Items.Cast<ToolStripItem>().ToArray();

    [Fact]
    public void The_menu_has_exactly_three_items_tracking_dashboard_and_quit()
    {
        using var tray = new TrayController(new AgentState());
        var items = Items(tray);
        Assert.Equal(["Tracking: ON", "Open dashboard (coming later)", "Quit"], items.Select(i => i.Text));
    }

    [Fact]
    public void The_dashboard_item_is_disabled_because_it_does_nothing_yet()
    {
        using var tray = new TrayController(new AgentState());
        Assert.False(Items(tray)[1].Enabled);
        Assert.True(Items(tray)[0].Enabled);
        Assert.True(Items(tray)[2].Enabled);
    }

    [Fact]
    public void Clicking_tracking_toggles_the_pause_state_and_the_label_shows_it()
    {
        var state = new AgentState();
        using var tray = new TrayController(state);
        var tracking = (ToolStripMenuItem)Items(tray)[0];

        tracking.PerformClick();
        Assert.False(state.TrackingEnabled);
        Assert.Equal("Tracking: OFF", tracking.Text);

        tracking.PerformClick();
        Assert.True(state.TrackingEnabled);
        Assert.Equal("Tracking: ON", tracking.Text);
    }

    [Fact]
    public void The_label_follows_the_state_even_when_something_else_changes_it()
    {
        var state = new AgentState();
        using var tray = new TrayController(state);
        state.SetTrackingEnabled(false);       // for example the next task changing it from outside the menu
        Assert.Equal("Tracking: OFF", Items(tray)[0].Text);
    }

    [Fact]
    public void Quit_raises_the_quit_event_once_per_click()
    {
        using var tray = new TrayController(new AgentState());
        var quits = 0;
        tray.QuitRequested += (_, _) => quits++;
        ((ToolStripMenuItem)Items(tray)[2]).PerformClick();
        Assert.Equal(1, quits);
    }

    [Fact]
    public void Pausing_does_not_change_consent_and_does_not_quit()
    {
        using var t = new TempData();
        new ConsentStore(t.DataDir).SaveAcceptance(DateTimeOffset.UtcNow);
        var before = File.ReadAllText(t.ConsentFile);
        var state = new AgentState();
        using var tray = new TrayController(state);
        var quits = 0;
        tray.QuitRequested += (_, _) => quits++;

        ((ToolStripMenuItem)Items(tray)[0]).PerformClick();

        Assert.Equal(before, File.ReadAllText(t.ConsentFile));
        Assert.True(new FileConsentGate(new ConsentStore(t.DataDir)).HasConsent);
        Assert.Equal(0, quits);
    }

    [Fact]
    public void The_tooltip_does_not_claim_that_tracking_works()
    {
        Assert.Contains("tracking not built yet", TrayController.ToolTip);
    }

    [Fact]
    public void The_placeholder_icons_are_drawn_in_code_for_on_and_off()
    {
        using var on = TrayIconFactory.Create(true);
        using var off = TrayIconFactory.Create(false);
        Assert.True(on.Width > 0 && off.Width > 0);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var tray = new TrayController(new AgentState());
        tray.Dispose();
        tray.Dispose();
    }
}

/// <summary>The start-up as a whole: is a tray ever created without consent?</summary>
public class AgentLauncherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 5, 15, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public int TraysCreated, LoopsRun;
        public FakeTray? Tray;
        public AgentLauncher Launcher = null!;
        public ConsentStore Store = null!;
        public int AskedUser;
    }

    private static Rig Make(TempData t, ConsentDecision answer, IConsentGate? hostGate = null)
    {
        var rig = new Rig { Store = new ConsentStore(t.DataDir) };
        var gate = new FileConsentGate(rig.Store);
        var onboarding = new OnboardingFlow(gate, rig.Store, () => { rig.AskedUser++; return answer; }, new FakeLog(), () => Now);
        var host = new AgentHost(hostGate ?? gate, () => { rig.TraysCreated++; return rig.Tray = new FakeTray(); });
        rig.Launcher = new AgentLauncher(onboarding, host, _ => rig.LoopsRun++);
        return rig;
    }

    [Fact]
    public void Decline_creates_no_tray_runs_nothing_and_writes_nothing()
    {
        using var t = new TempData();
        var rig = Make(t, ConsentDecision.Declined);

        var result = rig.Launcher.Launch();

        Assert.Equal(new AgentLaunchResult(OnboardingOutcome.Declined, TrayStarted: false), result);
        Assert.Equal((0, 0), (rig.TraysCreated, rig.LoopsRun));
        Assert.False(Directory.Exists(t.DataDir));
    }

    [Fact]
    public void Closing_the_real_consent_window_creates_no_tray()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);
        var trays = 0;
        var onboarding = new OnboardingFlow(gate, store, () => { using var f = new ConsentForm(); f.Close(); return f.Decision; }, new FakeLog(), () => Now);
        var host = new AgentHost(gate, () => { trays++; return new FakeTray(); });

        var result = new AgentLauncher(onboarding, host, _ => { }).Launch();

        Assert.False(result.TrayStarted);
        Assert.Equal(0, trays);
        Assert.False(Directory.Exists(t.DataDir));
    }

    [Fact]
    public void A_failed_save_creates_no_tray()
    {
        using var t = new TempData();
        File.WriteAllText(t.DataDir, "a file where the folder should be");
        var rig = Make(t, ConsentDecision.Accepted);

        var result = rig.Launcher.Launch();

        Assert.Equal(OnboardingOutcome.SaveFailed, result.Outcome);
        Assert.False(result.TrayStarted);
        Assert.Equal(0, rig.TraysCreated);
    }

    [Fact]
    public void Accept_saves_consent_then_creates_the_tray_once_and_runs_until_quit()
    {
        using var t = new TempData();
        var rig = Make(t, ConsentDecision.Accepted);

        var result = rig.Launcher.Launch();

        Assert.Equal(new AgentLaunchResult(OnboardingOutcome.Accepted, TrayStarted: true), result);
        Assert.True(rig.Store.HasValidConsent());
        Assert.Equal((1, 1), (rig.TraysCreated, rig.LoopsRun));
        Assert.Equal(1, rig.Tray!.ShowCalls);
    }

    [Fact]
    public void Valid_saved_consent_skips_the_window_and_starts_the_tray()
    {
        using var t = new TempData();
        new ConsentStore(t.DataDir).SaveAcceptance(Now.AddDays(-2));
        var rig = Make(t, ConsentDecision.Declined);

        var result = rig.Launcher.Launch();

        Assert.Equal(new AgentLaunchResult(OnboardingOutcome.ConsentOnRecord, TrayStarted: true), result);
        Assert.Equal(0, rig.AskedUser);
        Assert.Equal(1, rig.TraysCreated);
    }

    [Fact]
    public void An_old_consent_version_shows_the_window_again_and_no_tray_until_accepted()
    {
        using var t = new TempData();
        t.WriteConsentFile("""{ "consentVersion": 0, "acceptedAtUtc": "2020-01-01T00:00:00Z" }""");

        var declined = Make(t, ConsentDecision.Declined);
        Assert.False(declined.Launcher.Launch().TrayStarted);
        Assert.Equal((1, 0), (declined.AskedUser, declined.TraysCreated));

        var accepted = Make(t, ConsentDecision.Accepted);
        Assert.True(accepted.Launcher.Launch().TrayStarted);
    }

    [Fact]
    public void The_host_checks_consent_itself_so_a_tray_cannot_start_even_if_onboarding_said_go()
    {
        using var t = new TempData();
        new ConsentStore(t.DataDir).SaveAcceptance(Now);
        var rig = Make(t, ConsentDecision.Accepted, hostGate: new FakeGate { HasConsent = false });   // the host's own gate says no

        var result = rig.Launcher.Launch();

        Assert.False(result.TrayStarted);
        Assert.Equal((0, 0), (rig.TraysCreated, rig.LoopsRun));
    }

    [Fact]
    public void The_message_loop_receives_the_started_host()
    {
        using var t = new TempData();
        new ConsentStore(t.DataDir).SaveAcceptance(Now);
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);
        var host = new AgentHost(gate, () => new FakeTray());
        AgentHost? seen = null;
        bool? runningWhenCalled = null;
        var onboarding = new OnboardingFlow(gate, store, () => ConsentDecision.Declined, new FakeLog(), () => Now);

        new AgentLauncher(onboarding, host, h => { seen = h; runningWhenCalled = h.IsRunning; }).Launch();

        Assert.Same(host, seen);
        Assert.True(runningWhenCalled);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void The_first_copy_gets_the_guard_and_a_second_copy_does_not()
    {
        var name = @"Local\DLA.Test." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void Once_the_first_copy_is_gone_a_new_copy_can_start()
    {
        var name = @"Local\DLA.Test." + Guid.NewGuid().ToString("N");
        SingleInstance.TryAcquire(name)!.Dispose();
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void The_agent_guard_has_a_fixed_per_session_name()
    {
        Assert.Equal(@"Local\DigitalLifeAnalyzer.Agent", SingleInstance.AgentName);
    }
}
