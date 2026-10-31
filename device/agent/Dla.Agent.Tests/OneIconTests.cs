using System.Drawing;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using Dla.Agent.Consent;
using Dla.Agent.Tray;

namespace Dla.Agent.Tests;

// These tests prove "one instance, one tray icon". They use fakes only: no real tray icon is ever shown, no real
// agent is started, and the real single-instance name is never taken (every test uses its own unique name).
// The process-wide pieces (the guard slot and the one-icon slot) are shared, so these classes do not run in parallel.

[CollectionDefinition("ProcessWide", DisableParallelization = true)]
public class ProcessWideCollection;

/// <summary>A stand-in for the Windows notification icon. Counts what happened to it.</summary>
internal sealed class FakeTrayIcon : ITrayIcon
{
    public bool Visible { get; set; }
    public Icon? Icon { get; set; }
    public string Text { get; set; } = "";
    public ContextMenuStrip? ContextMenuStrip { get; set; }
    public bool Disposed;
    public int DisposeCalls;
    public bool EverVisible;

    public void Dispose() { Disposed = true; DisposeCalls++; }
}

/// <summary>Hands out fake icons and remembers every one it made.</summary>
internal sealed class FakeIconFactory
{
    public List<FakeTrayIcon> Made { get; } = [];
    public bool Throws;

    public FakeTrayIcon Create()
    {
        if (Throws) throw new InvalidOperationException("cannot create the icon");
        var icon = new FakeTrayIcon();
        Made.Add(icon);
        return icon;
    }

    /// <summary>How many fake icons are visible in the fake tray right now.</summary>
    public int VisibleNow => Made.Count(i => i.Visible && !i.Disposed);
}

[Collection("ProcessWide")]
public class ProcessGuardTests
{
    private static string UniqueName() => @"Local\DLA.Test." + Guid.NewGuid().ToString("N");

    private static FieldInfo StaticGuardField() =>
        typeof(SingleInstance).GetField("s_processGuard", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void The_guard_name_depends_only_on_the_logged_in_user_and_session()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;

        Assert.Equal(@"Local\DigitalLifeAnalyzer.Agent." + sid, SingleInstance.AgentName);
        Assert.Equal(SingleInstance.AgentName, SingleInstance.BuildAgentName());
        Assert.StartsWith(@"Local\", SingleInstance.AgentName);   // per session: two sessions never block each other

        // No parameter and no setting can change it: not the data folder, working directory, build mode or a test switch.
        Assert.Empty(typeof(SingleInstance).GetMethod(nameof(SingleInstance.BuildAgentName))!.GetParameters());
        foreach (var folder in new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppContext.BaseDirectory })
            Assert.DoesNotContain(folder.TrimEnd('\\'), SingleInstance.AgentName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_second_guard_cannot_be_acquired_while_the_first_is_held_whatever_the_data_folder()
    {
        var name = UniqueName();
        using var first = SingleInstance.AcquireForProcess(name);
        Assert.NotNull(first);

        for (var i = 0; i < 3; i++)
        {
            using var folder = new TempData();                                    // a different data folder each time
            _ = new FileConsentGate(new ConsentStore(folder.DataDir));
            Assert.Null(SingleInstance.AcquireForProcess(name));                  // this process already holds one
            Assert.Null(SingleInstance.TryAcquire(name));                         // and no other holder can take the name
        }
    }

    [Fact]
    public void The_guard_is_released_after_exit_so_a_new_copy_can_start()
    {
        var name = UniqueName();
        var first = SingleInstance.AcquireForProcess(name);
        Assert.True(SingleInstance.ProcessGuardHeld);

        first!.Dispose();

        Assert.False(SingleInstance.ProcessGuardHeld);
        using var next = SingleInstance.AcquireForProcess(name);
        Assert.NotNull(next);
    }

    [Fact]
    public void The_guard_is_kept_in_a_static_field_for_the_life_of_the_process_and_survives_garbage_collection()
    {
        var name = UniqueName();
        AcquireAndForget(name);                                                    // no local variable keeps it alive

        Assert.NotNull(StaticGuardField().GetValue(null));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        Assert.True(SingleInstance.ProcessGuardHeld);
        Assert.Null(SingleInstance.TryAcquire(name));                              // still refused after a full collection

        ((SingleInstance)StaticGuardField().GetValue(null)!).Dispose();
        Assert.Null(StaticGuardField().GetValue(null));
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    private static void AcquireAndForget(string name) => SingleInstance.AcquireForProcess(name);

    [Fact]
    public void A_copy_in_a_different_name_space_is_not_blocked_by_this_one()
    {
        using var mine = SingleInstance.AcquireForProcess(UniqueName());
        using var other = SingleInstance.TryAcquire(UniqueName());                // another user or session has another name
        Assert.NotNull(other);
    }
}

[Collection("ProcessWide")]
public class StartupOrderTests
{
    private static string UniqueName() => @"Local\DLA.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_refused_copy_exits_with_0_and_never_runs_anything_else()
    {
        var name = UniqueName();
        using var firstCopy = SingleInstance.TryAcquire(name);                    // "another agent is already running"
        var ran = 0;

        var exitCode = AgentStartup.Run(() => SingleInstance.TryAcquire(name), () => { ran++; return 7; });

        Assert.Equal(0, exitCode);
        Assert.Equal(0, ran);                                                     // no consent window, tray, listener or file code ran
    }

    [Fact]
    public void The_guard_is_taken_first_held_while_the_agent_runs_and_released_afterwards()
    {
        var name = UniqueName();
        var steps = new List<string>();
        bool? heldWhileRunning = null;

        var exitCode = AgentStartup.Run(
            () => { steps.Add("guard"); return SingleInstance.TryAcquire(name); },
            () => { steps.Add("agent"); heldWhileRunning = SingleInstance.TryAcquire(name) is null; return 5; });

        Assert.Equal(5, exitCode);
        Assert.Equal(["guard", "agent"], steps);
        Assert.True(heldWhileRunning);
        using var afterwards = SingleInstance.TryAcquire(name);
        Assert.NotNull(afterwards);                                               // released after the agent ended
    }

    [Fact]
    public void The_guard_is_released_even_if_the_agent_throws()
    {
        var name = UniqueName();
        Assert.Throws<InvalidOperationException>(() =>
            AgentStartup.Run(() => SingleInstance.TryAcquire(name), () => throw new InvalidOperationException("boom")));
        using var afterwards = SingleInstance.TryAcquire(name);
        Assert.NotNull(afterwards);
    }

    [Fact]
    public void The_process_guard_taken_through_the_startup_is_released_when_it_ends()
    {
        var name = UniqueName();
        AgentStartup.Run(() => SingleInstance.AcquireForProcess(name), () =>
        {
            Assert.True(SingleInstance.ProcessGuardHeld);
            return 0;
        });
        Assert.False(SingleInstance.ProcessGuardHeld);
    }
}

[Collection("ProcessWide")]
public class OneTrayIconTests
{
    private static TrayController Make(FakeIconFactory icons, AgentState? state = null) => new(state ?? new AgentState(), icons.Create);

    [Fact]
    public void Creating_a_controller_makes_no_icon_at_all()
    {
        var icons = new FakeIconFactory();
        using var tray = Make(icons);
        Assert.Empty(icons.Made);
        Assert.False(TrayController.ProcessOwnsIcon);
    }

    [Fact]
    public void Showing_the_same_tray_twice_leaves_one_icon()
    {
        var icons = new FakeIconFactory();
        using var tray = Make(icons);

        tray.Show();
        tray.Show();
        tray.Show();

        Assert.Single(icons.Made);
        Assert.Equal(1, icons.VisibleNow);
        Assert.True(icons.Made[0].Visible);
        Assert.NotNull(icons.Made[0].ContextMenuStrip);
        Assert.NotNull(icons.Made[0].Icon);
    }

    [Fact]
    public void A_second_tray_in_the_same_process_does_nothing_and_does_not_hide_the_first_icon()
    {
        var icons = new FakeIconFactory();
        using var first = Make(icons);
        using var second = Make(icons);

        first.Show();
        second.Show();                                                            // refused quietly

        Assert.Single(icons.Made);
        Assert.Equal(1, icons.VisibleNow);

        second.Dispose();                                                         // the refused one never owned the icon
        Assert.True(icons.Made[0].Visible);
        Assert.False(icons.Made[0].Disposed);
    }

    [Fact]
    public void Disposing_the_owner_hides_and_disposes_the_icon_and_frees_the_slot_for_a_restart()
    {
        var icons = new FakeIconFactory();
        var first = Make(icons);
        first.Show();

        first.Dispose();
        first.Dispose();                                                          // harmless twice

        Assert.False(icons.Made[0].Visible);
        Assert.Equal(1, icons.Made[0].DisposeCalls);
        Assert.False(TrayController.ProcessOwnsIcon);

        using var restarted = Make(icons);
        restarted.Show();
        Assert.Equal(2, icons.Made.Count);                                        // a new icon only because the old one is gone
        Assert.Equal(1, icons.VisibleNow);
    }

    [Fact]
    public void A_tray_that_is_disposed_cannot_be_shown_again()
    {
        var icons = new FakeIconFactory();
        var tray = Make(icons);
        tray.Dispose();
        tray.Show();
        Assert.Empty(icons.Made);
        Assert.False(TrayController.ProcessOwnsIcon);
    }

    [Fact]
    public void If_creating_the_icon_fails_nothing_is_left_behind_and_a_later_show_can_work()
    {
        var icons = new FakeIconFactory { Throws = true };
        using var tray = Make(icons);

        Assert.Throws<InvalidOperationException>(() => tray.Show());
        Assert.False(TrayController.ProcessOwnsIcon);                              // the slot was given back

        icons.Throws = false;
        tray.Show();
        Assert.Equal(1, icons.VisibleNow);
    }

    [Fact]
    public void Pausing_changes_the_icon_picture_but_never_adds_an_icon()
    {
        var icons = new FakeIconFactory();
        var state = new AgentState();
        using var tray = Make(icons, state);
        tray.Show();
        var on = icons.Made[0].Icon;

        state.SetTrackingEnabled(false);
        var off = icons.Made[0].Icon;
        state.SetTrackingEnabled(true);

        Assert.NotSame(on, off);
        Assert.Same(on, icons.Made[0].Icon);
        Assert.Single(icons.Made);
    }

    [Fact]
    public void Asking_the_host_to_start_many_times_still_gives_one_icon()
    {
        var icons = new FakeIconFactory();
        var state = new AgentState();
        using var host = new AgentHost(new FakeGate { HasConsent = true }, () => Make(icons, state));

        for (var i = 0; i < 5; i++) Assert.True(host.TryStart());

        Assert.Single(icons.Made);
        Assert.Equal(1, icons.VisibleNow);
    }

    [Fact]
    public void Two_hosts_in_one_process_still_show_only_one_icon()
    {
        var icons = new FakeIconFactory();
        using var hostA = new AgentHost(new FakeGate { HasConsent = true }, () => Make(icons));
        using var hostB = new AgentHost(new FakeGate { HasConsent = true }, () => Make(icons));

        hostA.TryStart();
        hostB.TryStart();

        Assert.Single(icons.Made);
        Assert.Equal(1, icons.VisibleNow);
    }

    [Fact]
    public void Stopping_and_restarting_the_host_never_leaves_two_icons()
    {
        var icons = new FakeIconFactory();
        using var host = new AgentHost(new FakeGate { HasConsent = true }, () => Make(icons));

        host.TryStart();
        host.Stop();
        Assert.Equal(0, icons.VisibleNow);
        host.TryStart();

        Assert.Equal(1, icons.VisibleNow);
        Assert.Equal(2, icons.Made.Count);
    }
}

/// <summary>Every way the process can end must take the icon out of the tray.</summary>
[Collection("ProcessWide")]
public class ExitPathTests
{
    private sealed class FakeExitSource : IExitSource
    {
        public event Action? SessionEnding;
        public event Action? ApplicationExit;
        public event Action? ProcessExit;
        public event Action<Exception?>? Crash;
        public void RaiseSessionEnding() => SessionEnding?.Invoke();
        public void RaiseApplicationExit() => ApplicationExit?.Invoke();
        public void RaiseProcessExit() => ProcessExit?.Invoke();
        public void RaiseCrash(Exception? e) => Crash?.Invoke(e);
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly FakeIconFactory Icons = new();
        public readonly FakeExitSource Source = new();
        public readonly FakeLog Log = new();
        public readonly AgentHost Host;
        public readonly ExitCleanup Cleanup;
        public int ExitRequests;

        public Rig()
        {
            var state = new AgentState();
            Host = new AgentHost(new FakeGate { HasConsent = true }, () => new TrayController(state, Icons.Create));
            Assert.True(Host.TryStart());
            Assert.Equal(1, Icons.VisibleNow);                                    // a real controller, a fake icon, visible
            Cleanup = new ExitCleanup(Host, Source, Log) { RequestExit = () => ExitRequests++ };
        }

        public void AssertIconGone()
        {
            Assert.Equal(0, Icons.VisibleNow);
            Assert.False(Icons.Made[0].Visible);
            Assert.Equal(1, Icons.Made[0].DisposeCalls);                          // hidden and disposed exactly once
            Assert.False(Host.IsRunning);
            Assert.False(TrayController.ProcessOwnsIcon);
        }

        public void Dispose() { Cleanup.Dispose(); Host.Dispose(); }
    }

    [Fact]
    public void Windows_logoff_or_shutdown_removes_the_icon_and_asks_the_loop_to_end()
    {
        using var rig = new Rig();
        rig.Source.RaiseSessionEnding();
        rig.AssertIconGone();
        Assert.Equal(1, rig.ExitRequests);
    }

    [Fact]
    public void An_unhandled_exception_removes_the_icon()
    {
        using var rig = new Rig();
        rig.Source.RaiseCrash(new InvalidOperationException("secret text in a message"));
        rig.AssertIconGone();
    }

    [Fact]
    public void An_unhandled_exception_with_no_exception_object_still_removes_the_icon()
    {
        using var rig = new Rig();
        rig.Source.RaiseCrash(null);
        rig.AssertIconGone();
    }

    [Fact]
    public void Only_the_kind_of_error_is_logged_never_its_message()
    {
        using var rig = new Rig();
        rig.Source.RaiseCrash(new InvalidOperationException("secret text in a message"));
        var line = Assert.Single(rig.Log.Lines);
        Assert.Contains("InvalidOperationException", line);
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public void Normal_application_exit_removes_the_icon()
    {
        using var rig = new Rig();
        rig.Source.RaiseApplicationExit();
        rig.AssertIconGone();
    }

    [Fact]
    public void Process_exit_removes_the_icon()
    {
        using var rig = new Rig();
        rig.Source.RaiseProcessExit();
        rig.AssertIconGone();
    }

    [Fact]
    public void Several_exit_signals_in_a_row_dispose_the_icon_only_once()
    {
        using var rig = new Rig();
        rig.Source.RaiseSessionEnding();
        rig.Source.RaiseApplicationExit();
        rig.Source.RaiseProcessExit();
        rig.Source.RaiseCrash(new Exception());
        rig.AssertIconGone();
    }

    [Fact]
    public void Quit_in_the_menu_ends_the_loop_and_disposing_the_context_removes_the_icon()
    {
        var icons = new FakeIconFactory();
        var state = new AgentState();
        TrayController? controller = null;
        using var host = new AgentHost(new FakeGate { HasConsent = true }, () => controller = new TrayController(state, icons.Create));
        Assert.True(host.TryStart());

        var context = new AgentApplicationContext(host);
        var exited = false;
        context.ThreadExit += (_, _) => exited = true;

        ((ToolStripMenuItem)controller!.Menu.Items[2]).PerformClick();            // the Quit item
        Assert.True(exited);
        context.Dispose();                                                         // what the end of Application.Run does

        Assert.Equal(0, icons.VisibleNow);
        Assert.Equal(1, icons.Made[0].DisposeCalls);
        Assert.False(TrayController.ProcessOwnsIcon);
    }

    [Fact]
    public void A_cleanup_that_has_been_disposed_ignores_later_signals()
    {
        using var rig = new Rig();
        rig.Cleanup.Dispose();
        rig.Source.RaiseProcessExit();
        Assert.Equal(1, rig.Icons.VisibleNow);                                    // not stopped by a detached cleanup
    }

    [Fact]
    public void A_failing_cleanup_step_never_throws_out_of_an_exit_signal()
    {
        var icons = new FakeIconFactory();
        var source = new FakeExitSource();
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => new TrayController(new AgentState(), icons.Create));
        host.TryStart();
        using var cleanup = new ExitCleanup(host, source, new FakeLog()) { RequestExit = () => throw new InvalidOperationException("loop gone") };

        source.RaiseSessionEnding();                                               // must not throw

        Assert.Equal(0, icons.VisibleNow);                                         // the icon was removed anyway
        host.Dispose();
    }
}

/// <summary>The one status line written when the agent starts: after consent only, and never by a refused copy.</summary>
public class StartupLogLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 5, 15, 0, TimeSpan.Zero);

    private static (AgentLauncher launcher, FakeLog launcherLog) Make(TempData t, ConsentDecision answer, bool guardHeld = true, bool hostConsent = true)
    {
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);
        var onboarding = new OnboardingFlow(gate, store, () => answer, new FakeLog(), () => Now);
        var host = new AgentHost(hostConsent ? gate : new FakeGate { HasConsent = false }, () => new FakeTray());
        var log = new FakeLog();
        return (new AgentLauncher(onboarding, host, _ => { }, log, () => guardHeld, () => 4242), log);
    }

    [Fact]
    public void After_consent_one_line_names_the_process_id_and_says_the_guard_was_acquired()
    {
        using var t = new TempData();
        var (launcher, log) = Make(t, ConsentDecision.Accepted);

        launcher.Launch();

        Assert.Equal(["Agent started. Process id 4242. Single-instance guard acquired: yes."], log.Lines);
    }

    [Fact]
    public void The_line_says_no_if_the_guard_is_not_held()
    {
        using var t = new TempData();
        var (launcher, log) = Make(t, ConsentDecision.Accepted, guardHeld: false);
        launcher.Launch();
        Assert.Equal(["Agent started. Process id 4242. Single-instance guard acquired: no."], log.Lines);
    }

    [Fact]
    public void With_consent_already_on_record_the_line_is_written_without_a_window()
    {
        using var t = new TempData();
        new ConsentStore(t.DataDir).SaveAcceptance(Now);
        var (launcher, log) = Make(t, ConsentDecision.Declined);
        launcher.Launch();
        Assert.Single(log.Lines);
    }

    [Fact]
    public void Decline_or_no_start_writes_no_line()
    {
        using var t = new TempData();
        var (declined, declinedLog) = Make(t, ConsentDecision.Declined);
        declined.Launch();
        Assert.Empty(declinedLog.Lines);

        using var t2 = new TempData();
        var (notStarted, notStartedLog) = Make(t2, ConsentDecision.Accepted, hostConsent: false);
        notStarted.Launch();
        Assert.Empty(notStartedLog.Lines);
    }

    [Fact]
    public void In_the_real_log_file_the_line_exists_only_after_consent()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);
        var log = new FileAgentLog(store.DataDir);

        var declined = new AgentLauncher(new OnboardingFlow(gate, store, () => ConsentDecision.Declined, log, () => Now),
            new AgentHost(gate, () => new FakeTray()), _ => { }, log, () => true, () => 99);
        declined.Launch();
        Assert.False(File.Exists(Path.Combine(t.DataDir, "agent.log")));          // nothing written before consent

        var accepted = new AgentLauncher(new OnboardingFlow(gate, store, () => ConsentDecision.Accepted, log, () => Now),
            new AgentHost(gate, () => new FakeTray()), _ => { }, log, () => true, () => 99);
        accepted.Launch();
        var text = File.ReadAllText(Path.Combine(t.DataDir, "agent.log"));
        Assert.Contains("Agent started. Process id 99. Single-instance guard acquired: yes.", text);
    }
}
