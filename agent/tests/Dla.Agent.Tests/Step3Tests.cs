using Dla.Agent.Consent;
using Dla.Agent.Core;
using Dla.Agent.Extension;
using Dla.Agent.Lifecycle;
using Dla.Agent.Supervision;
using Microsoft.Win32;

namespace Dla.Agent.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dla-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan t) => Now += t;
    public Func<DateTimeOffset> Func => () => Now;
}

public class ConsentTests
{
    [Fact]
    public void Nothing_is_recorded_before_accept()
    {
        using var d = new TempDir();
        var store = new ConsentStore(d.Path);
        Assert.Equal(ConsentState.None, store.Load().State);
        Assert.False(RecordingGate.CanRecord(store.Load(), new AgentSettings(false)));
    }

    [Fact]
    public void Accept_saves_timestamp_and_text_version()
    {
        using var d = new TempDir();
        var clock = new FakeClock();
        var store = new ConsentStore(d.Path, clock.Func);
        store.Accept();

        var loaded = new ConsentStore(d.Path).Load();
        Assert.Equal(ConsentState.Accepted, loaded.State);
        Assert.Equal(ConsentText.Version, loaded.TextVersion);
        Assert.Equal(clock.Now, loaded.AcceptedAt);
        Assert.True(RecordingGate.CanRecord(loaded, new AgentSettings(false)));
    }

    [Fact]
    public void Decline_and_withdraw_stop_recording()
    {
        using var d = new TempDir();
        var store = new ConsentStore(d.Path);
        store.Decline();
        Assert.False(RecordingGate.CanRecord(store.Load(), new AgentSettings(false)));

        store.Accept();
        var withdrawn = store.Withdraw();
        Assert.Equal(ConsentState.Withdrawn, withdrawn.State);
        Assert.NotNull(withdrawn.WithdrawnAt);
        Assert.False(RecordingGate.CanRecord(withdrawn, new AgentSettings(false)));
    }

    [Fact]
    public void Paused_blocks_recording_even_with_consent()
    {
        using var d = new TempDir();
        var consent = new ConsentStore(d.Path).Accept();
        Assert.False(RecordingGate.CanRecord(consent, new AgentSettings(Paused: true)));
    }

    [Fact]
    public void Older_consent_text_version_is_not_valid()
    {
        var old = new ConsentRecord(ConsentState.Accepted, ConsentText.Version - 1, DateTimeOffset.Now, null, null);
        Assert.False(old.IsValidAccepted);
    }

    [Fact]
    public void Corrupt_consent_file_means_no_consent()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "consent.json"), "{ not json");
        Assert.Equal(ConsentState.None, new ConsentStore(d.Path).Load().State);
    }

    [Fact]
    public void Pause_flag_persists()
    {
        using var d = new TempDir();
        new AgentSettingsStore(d.Path).Save(new AgentSettings(true));
        Assert.True(new AgentSettingsStore(d.Path).Load().Paused);
    }
}

public class LifecycleTests
{
    [Fact]
    public void First_run_has_no_previous()
    {
        using var d = new TempDir();
        Assert.Equal(PreviousRun.FirstRun, new LifecycleLog(d.Path).PreviousRunStatus("s1"));
    }

    [Fact]
    public void Session_without_clean_marker_is_a_crash()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        Assert.Equal(PreviousRun.Crashed, log.PreviousRunStatus("s2"));
        Assert.False(log.HasCleanShutdown("s1"));
    }

    [Fact]
    public void Clean_shutdown_marker_is_a_clean_stop()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        log.Append(MarkerKind.CleanShutdown, "s1", "SystemShutdown");
        Assert.Equal(PreviousRun.Clean, log.PreviousRunStatus("s2"));
        Assert.True(log.HasCleanShutdown("s1"));
    }

    [Fact]
    public void Suspend_then_resume_then_crash_is_a_crash()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        log.Append(MarkerKind.Suspend, "s1");
        Assert.Equal(PreviousRun.EndedWhileSuspended, log.PreviousRunStatus("s2"));
        log.Append(MarkerKind.Resume, "s1");
        Assert.Equal(PreviousRun.Crashed, log.PreviousRunStatus("s2"));
    }

    [Fact]
    public void Extension_gap_markers_do_not_change_the_previous_run_verdict()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        log.Append(MarkerKind.CleanShutdown, "s1");
        log.Append(MarkerKind.ExtensionSilentEnd, "s1");
        Assert.Equal(PreviousRun.Clean, log.PreviousRunStatus("s2"));
    }

    [Fact]
    public void Torn_last_line_is_ignored()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        File.AppendAllText(Path.Combine(d.Path, "lifecycle.jsonl"), "{\"At\":\"2026-");
        Assert.Single(log.ReadAll());
    }

    [Fact]
    public void Each_record_is_one_json_object_followed_by_a_single_newline()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        log.Append(MarkerKind.Suspend, "s1", "detail with \"quotes\" and \nnewline");
        log.Append(MarkerKind.CleanShutdown, "s1");

        var text = File.ReadAllText(Path.Combine(d.Path, "lifecycle.jsonl"));
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        var lines = text.Split('\n');
        Assert.Equal(4, lines.Length);          // 3 records + the empty piece after the final newline
        Assert.Equal("", lines[^1]);
        foreach (var line in lines.Take(3))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line); // each line is exactly one JSON value
            Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        }
        Assert.Equal(3, log.ReadAll().Count);
    }

    [Fact]
    public void File_without_trailing_newline_gets_one_before_the_next_record()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "lifecycle.jsonl");
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\n')); // simulate a record cut off before its newline

        log.Append(MarkerKind.CleanShutdown, "s1");

        var text = File.ReadAllText(path);
        Assert.EndsWith("\n", text);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal([MarkerKind.Start, MarkerKind.CleanShutdown], log.ReadAll().Select(m => m.Kind));
    }

    [Fact]
    public void Torn_line_does_not_swallow_the_next_record()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "lifecycle.jsonl");
        var log = new LifecycleLog(d.Path);
        log.Append(MarkerKind.Start, "s1");
        File.AppendAllText(path, "{\"At\":\"2026-10-09T"); // crash mid-write, no newline

        log.Append(MarkerKind.CleanShutdown, "s2");

        var kinds = log.ReadAll().Select(m => m.Kind).ToArray();
        Assert.Equal([MarkerKind.Start, MarkerKind.CleanShutdown], kinds); // torn line skipped, new record intact
    }

    [Fact]
    public void Appending_creates_the_file_and_each_call_adds_exactly_one_line()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "lifecycle.jsonl");
        var log = new LifecycleLog(d.Path);
        Assert.False(File.Exists(path));
        for (var i = 1; i <= 5; i++)
        {
            log.Append(MarkerKind.Resume, "s1");
            Assert.Equal(i, File.ReadAllLines(path).Length);
        }
    }

    [Fact]
    public void Trim_keeps_only_recent_lines()
    {
        using var d = new TempDir();
        var log = new LifecycleLog(d.Path);
        for (var i = 0; i < 30; i++) log.Append(MarkerKind.Resume, "s" + i);
        log.Trim(keepLast: 10, triggerAt: 20);
        Assert.Equal(10, log.ReadAll().Count);
        Assert.Equal("s29", log.ReadAll().Last().SessionId);
    }
}

public class RestartPolicyTests
{
    [Fact]
    public void Backoff_is_2_5_30_30_then_cooldown_on_fifth_rapid_crash()
    {
        var clock = new FakeClock();
        var p = new RestartPolicy(clock.Func);
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            p.OnStarted();
            clock.Advance(TimeSpan.FromSeconds(1)); // each run dies after 1s
            delays.Add(p.OnCrash());
            clock.Advance(delays[^1] < RestartPolicy.Cooldown ? delays[^1] : TimeSpan.Zero);
        }
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)], delays);
        Assert.True(p.InCooldown);
    }

    [Fact]
    public void Cooldown_keeps_retrying_every_five_minutes_and_never_gives_up()
    {
        var clock = new FakeClock();
        var p = new RestartPolicy(clock.Func);
        for (var i = 0; i < 5; i++) { p.OnStarted(); clock.Advance(TimeSpan.FromSeconds(1)); p.OnCrash(); }
        Assert.True(p.InCooldown);

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(RestartPolicy.Cooldown);
            p.OnStarted();
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(RestartPolicy.Cooldown, p.OnCrash());
        }
    }

    [Fact]
    public void A_stable_run_resets_the_policy()
    {
        var clock = new FakeClock();
        var p = new RestartPolicy(clock.Func);
        for (var i = 0; i < 5; i++) { p.OnStarted(); clock.Advance(TimeSpan.FromSeconds(1)); p.OnCrash(); }
        Assert.True(p.InCooldown);

        p.OnStarted();
        clock.Advance(TimeSpan.FromMinutes(3)); // ran fine for 3 minutes
        Assert.Equal(TimeSpan.FromSeconds(2), p.OnCrash());
        Assert.False(p.InCooldown);
    }
}

public class ExtensionLinkTests
{
    private static (ExtensionLink link, LifecycleLog log, FakeClock clock, TempDir dir) Make()
    {
        var dir = new TempDir();
        var clock = new FakeClock();
        var log = new LifecycleLog(dir.Path, clock.Func);
        return (new ExtensionLink(log, "s1"), log, clock, dir);
    }

    [Fact]
    public void Silence_opens_a_gap_starting_at_the_last_heartbeat_and_a_heartbeat_closes_it()
    {
        var (link, log, clock, dir) = Make();
        using var _ = dir;

        link.Check(clock.Now, canRecord: true);             // starts watching
        var lastBeat = clock.Now;
        clock.Advance(TimeSpan.FromSeconds(30));
        link.OnHeartbeat(clock.Now);
        lastBeat = clock.Now;

        clock.Advance(TimeSpan.FromSeconds(60));
        link.Check(clock.Now, true);
        Assert.False(link.InGap);                           // 60s < 90s, still fine

        clock.Advance(TimeSpan.FromSeconds(60));
        link.Check(clock.Now, true);
        Assert.True(link.InGap);
        var start = log.ReadAll().Single(m => m.Kind == MarkerKind.ExtensionSilentStart);
        Assert.Equal(lastBeat, start.At);

        clock.Advance(TimeSpan.FromSeconds(40));
        link.OnHeartbeat(clock.Now);
        Assert.False(link.InGap);
        Assert.Contains(log.ReadAll(), m => m.Kind == MarkerKind.ExtensionSilentEnd && m.At == clock.Now);
    }

    [Fact]
    public void Never_started_extension_is_reported_as_a_gap_not_guessed()
    {
        var (link, log, clock, dir) = Make();
        using var _ = dir;
        link.Check(clock.Now, true);
        clock.Advance(TimeSpan.FromMinutes(5));
        link.Check(clock.Now, true);
        Assert.True(link.InGap);
        Assert.Single(log.ReadAll(), m => m.Kind == MarkerKind.ExtensionSilentStart);
    }

    [Fact]
    public void No_gap_is_tracked_while_paused_or_without_consent()
    {
        var (link, log, clock, dir) = Make();
        using var _ = dir;
        link.Check(clock.Now, canRecord: false);
        clock.Advance(TimeSpan.FromMinutes(30));
        link.Check(clock.Now, canRecord: false);
        Assert.Empty(log.ReadAll());
    }

    [Fact]
    public void Open_gap_is_closed_when_recording_stops()
    {
        var (link, log, clock, dir) = Make();
        using var _ = dir;
        link.Check(clock.Now, true);
        clock.Advance(TimeSpan.FromMinutes(5));
        link.Check(clock.Now, true);
        link.Check(clock.Now, canRecord: false); // user paused
        Assert.False(link.InGap);
        Assert.Contains(log.ReadAll(), m => m.Kind == MarkerKind.ExtensionSilentEnd && m.Detail == "recording-stopped");
    }
}

public class AutoStartAndInstanceTests
{
    [Fact]
    public void Enable_writes_a_per_user_run_value_and_disable_removes_it()
    {
        var subKey = @"Software\DLA-Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var a = new AutoStart(subKey, "DlaTest");
            Assert.False(a.IsEnabled);
            a.Enable(@"C:\Program Files\DLA\Dla.Agent.exe");
            Assert.Equal("\"C:\\Program Files\\DLA\\Dla.Agent.exe\"", a.GetCommand());
            a.Disable();
            Assert.False(a.IsEnabled);
            a.Disable(); // idempotent
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\DLA-Tests", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Second_instance_cannot_acquire_the_same_name()
    {
        var name = @"Local\DLA.Test." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
    }
}
