using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dla.Agent.Consent;
using Dla.Agent.Link;
using Dla.Agent.Startup;
using Dla.Agent.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Dla.Agent.Tests;

// No browser is started and no key or mouse is touched. Components are fakes, or the real link on 127.0.0.1 with a throwaway data folder.

internal sealed class FakeStartupEntry : IStartupEntry
{
    public string? Value;
    public int Writes, Deletes;
    public bool Throws;
    public string? Read() => Throws ? throw new UnauthorizedAccessException() : Value;
    public void Write(string command) { if (Throws) throw new UnauthorizedAccessException(); Value = command; Writes++; }
    public void Delete() { if (Throws) throw new UnauthorizedAccessException(); Value = null; Deletes++; }
}

internal sealed class FakeRegistration : IStartupRegistration
{
    public int Ensures, Removes;
    public bool Ensure() { Ensures++; return true; }
    public void Remove() => Removes++;
}

internal sealed class FakeLink : IAgentLink
{
    public int StartCalls, DisposeCalls;
    public bool IsListening { get; private set; }
    public bool Start() { StartCalls++; IsListening = true; return true; }
    public void Stop() => IsListening = false;
    public void Dispose() { DisposeCalls++; IsListening = false; }
}

public class StartupRegistrationTests
{
    private const string Exe = @"C:\Users\Me\AppData\Local\Programs\DLA\Dla.Agent.exe";

    [Fact]
    public void The_entry_is_the_full_path_of_the_program_in_quotes()
    {
        var reg = new StartupRegistration(new FakeStartupEntry(), () => Exe);
        Assert.Equal($"\"{Exe}\"", reg.Command());
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Python\python.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_the_real_program_file_is_ever_registered(string? path)
    {
        var entry = new FakeStartupEntry();
        var reg = new StartupRegistration(entry, () => path);
        Assert.False(reg.Ensure());
        Assert.Equal(0, entry.Writes);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void Ensure_writes_one_value_once_and_repeating_it_changes_nothing()
    {
        var entry = new FakeStartupEntry();
        var reg = new StartupRegistration(entry, () => Exe);
        Assert.True(reg.Ensure());
        Assert.True(reg.Ensure());
        Assert.True(reg.Ensure());
        Assert.Equal(1, entry.Writes);
        Assert.Equal($"\"{Exe}\"", entry.Value);
    }

    [Fact]
    public void A_moved_program_updates_the_same_single_entry()
    {
        var entry = new FakeStartupEntry { Value = "\"C:\\Old\\Dla.Agent.exe\"" };
        var reg = new StartupRegistration(entry, () => Exe);
        Assert.True(reg.Ensure());
        Assert.Equal($"\"{Exe}\"", entry.Value);
        Assert.Equal(1, entry.Writes);
    }

    [Fact]
    public void Remove_deletes_the_entry_and_removing_twice_is_harmless()
    {
        var entry = new FakeStartupEntry { Value = $"\"{Exe}\"" };
        var reg = new StartupRegistration(entry, () => Exe);
        reg.Remove();
        reg.Remove();
        Assert.Null(entry.Value);
        Assert.Equal(1, entry.Deletes);
    }

    [Fact]
    public void A_failing_registry_never_throws_and_is_logged_without_details()
    {
        var log = new FakeLog();
        var reg = new StartupRegistration(new FakeStartupEntry { Throws = true }, () => Exe, log);
        Assert.False(reg.Ensure());
        reg.Remove();
        Assert.All(log.Lines, l => Assert.DoesNotContain("Users", l));
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void The_product_uses_the_current_users_Run_key_and_one_value_name()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RunKeyEntry.RunKeyPath);
        Assert.Equal("DigitalLifeAnalyzer", RunKeyEntry.DefaultValueName);
        Assert.Equal("Dla.Agent.exe", StartupRegistration.ProgramFileName);
    }

    [Fact]
    public void The_real_registry_entry_round_trips_on_a_throwaway_key_and_never_touches_the_real_Run_key()
    {
        var root = @"Software\DigitalLifeAnalyzer.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var entry = new RunKeyEntry(root);
            var reg = new StartupRegistration(entry, () => Exe);
            Assert.Null(entry.Read());
            Assert.True(reg.Ensure());
            Assert.Equal($"\"{Exe}\"", entry.Read());
            using (var key = Registry.CurrentUser.OpenSubKey(root))
                Assert.Equal(new[] { "DigitalLifeAnalyzer" }, key!.GetValueNames());          // exactly one value
            reg.Remove();
            Assert.Null(entry.Read());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\DigitalLifeAnalyzer.Tests");
            if (parent is { SubKeyCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\DigitalLifeAnalyzer.Tests", throwOnMissingSubKey: false);
        }
    }
}

public class AutomaticStartupFlowTests
{
    private static (AgentHost Host, FakeGate Gate, FakeTray Tray, FakeLink Link, FakeRegistration Startup, AgentState State, int[] Created)
        Make(bool consent, FakeLog? log = null)
    {
        var gate = new FakeGate { HasConsent = consent };
        var tray = new FakeTray();
        var link = new FakeLink();
        var startup = new FakeRegistration();
        var state = new AgentState();
        var created = new int[2];   // tray, link
        var host = new AgentHost(gate, () => { created[0]++; return tray; }, () => { created[1]++; return link; }, log, startup);
        return (host, gate, tray, link, startup, state, created);
    }

    [Fact]
    public void Before_consent_nothing_is_created_started_or_registered()
    {
        var (host, _, tray, link, startup, _, created) = Make(consent: false);
        Assert.False(host.TryStart());
        Assert.Equal(new[] { 0, 0 }, created);
        Assert.Equal((0, 0, 0), (tray.ShowCalls, link.StartCalls, startup.Ensures));
        Assert.Equal(1, startup.Removes);                                    // and no start-with-Windows entry is left behind
    }

    [Fact]
    public void After_consent_the_tray_the_link_for_the_extension_and_start_with_Windows_come_up_once()
    {
        var (host, _, tray, link, startup, _, created) = Make(consent: true);
        Assert.True(host.TryStart());
        Assert.Equal(new[] { 1, 1 }, created);
        Assert.Equal((1, 1, 1), (tray.ShowCalls, link.StartCalls, startup.Ensures));
        Assert.Equal(0, startup.Removes);
    }

    [Fact]
    public void Asking_the_host_to_start_again_creates_nothing_new()
    {
        var (host, _, _, _, startup, _, created) = Make(consent: true);
        for (var i = 0; i < 5; i++) Assert.True(host.TryStart());
        Assert.Equal(new[] { 1, 1 }, created);
        Assert.Equal(1, startup.Ensures);
    }

    [Fact]
    public void Existing_consent_is_not_asked_for_again()
    {
        var asked = 0;
        using var data = new TempData();
        var store = new ConsentStore(data.DataDir);
        store.SaveAcceptance(DateTimeOffset.UtcNow);
        var flow = new OnboardingFlow(new FileConsentGate(store), store, () => { asked++; return ConsentDecision.Declined; }, new FakeLog(), () => DateTimeOffset.UtcNow);
        Assert.Equal(OnboardingOutcome.ConsentOnRecord, flow.Run());
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Without_saved_consent_the_user_is_asked_and_a_decline_starts_nothing()
    {
        var asked = 0;
        using var data = new TempData();
        var store = new ConsentStore(data.DataDir);
        var flow = new OnboardingFlow(new FileConsentGate(store), store, () => { asked++; return ConsentDecision.Declined; }, new FakeLog(), () => DateTimeOffset.UtcNow);
        Assert.Equal(OnboardingOutcome.Declined, flow.Run());
        Assert.Equal(1, asked);
        Assert.Empty(data.FilesCreated());                                  // not even a data folder
    }

    [Fact]
    public void Restarting_after_a_shutdown_brings_everything_back_with_still_one_start_with_Windows_entry()
    {
        var entry = new FakeStartupEntry();
        var reg = new StartupRegistration(entry, () => @"C:\DLA\Dla.Agent.exe");
        var gate = new FakeGate { HasConsent = true };
        var links = new List<FakeLink>();
        AgentHost NewHost() => new(gate, () => new FakeTray(), () => { var l = new FakeLink(); links.Add(l); return l; }, startup: reg);

        var first = NewHost();
        Assert.True(first.TryStart());
        Assert.True(links[0].IsListening);
        first.Stop();
        Assert.False(links[0].IsListening);                                  // shutdown closed the link
        first.Stop();                                                        // and stopping twice is harmless

        var second = NewHost();
        Assert.True(second.TryStart());
        Assert.True(links[1].IsListening);
        Assert.Equal(1, entry.Writes);                                       // the entry was written once, not once per start
        second.Stop();
    }

    [Fact]
    public void Pausing_from_the_tray_tells_the_extension_not_to_record_and_resuming_tells_it_to_record_again()
    {
        var gate = new FakeGate { HasConsent = true };
        var state = new AgentState();
        var status = new AgentStatusProvider(gate, state);
        Assert.True(status.GetStatus().TrackingEnabled);
        state.SetTrackingEnabled(false);
        Assert.False(status.GetStatus().TrackingEnabled);
        Assert.False(status.GetStatus().Consented);                          // the extension records only when both are true
        state.SetTrackingEnabled(true);
        Assert.True(status.GetStatus().TrackingEnabled);
    }

    [Fact]
    public void Withdrawn_consent_means_the_browser_is_told_not_to_record()
    {
        var gate = new FakeGate { HasConsent = true };
        var state = new AgentState();
        var status = new AgentStatusProvider(gate, state);
        Assert.True(status.GetStatus().TrackingEnabled);
        gate.HasConsent = false;
        var now = status.GetStatus();
        Assert.Equal((false, false, 0), (now.Consented, now.TrackingEnabled, now.ConsentVersion));
    }

    [Fact]
    public void Nothing_in_the_program_starts_or_looks_for_any_other_process()
    {
        // the extension is the only collector: the program has no process launcher, no tracker and no foreground-window code
        var agent = typeof(AgentHost).Assembly;
        var names = string.Join("\n", agent.GetTypes().Select(t => t.FullName));
        foreach (var gone in new[] { "Tracker", "Supervisor", "Python", "Foreground" })
            Assert.DoesNotContain(gone, names, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(agent.GetTypes().SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)),
            m => m.Name.Contains("GetForegroundWindow", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("GetLastInputInfo", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The automatically started pieces working together on 127.0.0.1: link, consent status, session, SQLite. No browser is involved.</summary>
public class AutomaticCollectionTests : IDisposable
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly TempData _t = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_t.Root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Jsonl => Path.Combine(_t.Root, "events.jsonl");
    private string DbPath => Path.Combine(_t.DataDir, EventStorageFactory.DatabaseFileName);

    private static string Event(string id) =>
        $$"""{"schema_version":"1","id":"{{id}}","start":"2026-10-31T10:00:00Z","end":"2026-10-31T10:00:20Z","duration_seconds":20,"source":"extension","platform":"unknown","domain":"example.org","url":"https://example.org/p","title":"Example","activity_type":"webpage","content_count":0,"session_id":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";

    private static async Task<HttpStatusCode> Post(int port, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/events");
        request.Headers.TryAddWithoutValidation("X-DLA-Extension-Id", LinkTestData.ChromeId);   // how the extension identifies itself
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        return response.StatusCode;
    }

    private (LocalStatusLink Link, IDisposable? Upkeep, int Port) StartLink(FakeGate gate, AgentState state, FakeLog? log = null)
    {
        var port = LinkTestData.FreePort();
        var (sink, upkeep) = EventStorageFactory.Create(_t.DataDir, Jsonl, log);
        var link = new LocalStatusLink(LinkTestData.Config(port, LinkTestData.ChromeId), new AgentStatusProvider(gate, state), log, sink, owned: upkeep);
        Assert.True(link.Start());
        return (link, upkeep, port);
    }

    private List<string> Rows(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetValue(0)?.ToString() ?? "");
        return list;
    }

    [Fact]
    public void Nothing_exists_before_consent_the_database_is_created_only_when_the_storage_is_opened()
    {
        Assert.False(File.Exists(DbPath));
        Assert.False(Directory.Exists(_t.DataDir));
        var (sink, upkeep) = EventStorageFactory.Create(_t.DataDir, Jsonl);
        Assert.IsType<SqliteEventSink>(sink);
        Assert.True(File.Exists(DbPath));
        upkeep!.Dispose();
    }

    /// <summary>Copies ONLY dla.db (not the -wal file) and reads the copy, like a person or tool that copies or opens the .db file by itself.</summary>
    private List<string> TablesInTheMainFileOnly()
    {
        var copy = Path.Combine(_t.Root, "copy-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(DbPath, copy);
        Assert.False(File.Exists(copy + "-wal"));
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    [Fact]
    public void A_fresh_start_creates_a_non_empty_database_with_the_events_and_meta_tables_before_any_event_arrives()
    {
        var (sink, upkeep) = EventStorageFactory.Create(_t.DataDir, Jsonl);          // what the agent does when it starts after consent: no event posted
        Assert.IsType<SqliteEventSink>(sink);
        Assert.True(new FileInfo(DbPath).Length > 0);                                // not a 0-byte file
        Assert.Equal(new[] { "events", "meta" }, TablesInTheMainFileOnly());         // and the tables are in dla.db itself, not only in the -wal file
        Assert.Equal(new[] { "events", "meta" }, Rows("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"));
        Assert.Equal("0", Rows("SELECT COUNT(*) FROM events;").Single());
        upkeep!.Dispose();
    }

    [Fact]
    public void Opening_the_store_directly_also_leaves_the_tables_in_the_main_file_and_a_reopen_changes_nothing()
    {
        using (var store = SqliteEventStore.Open(DbPath)) Assert.Equal(0, store.Count());
        var size = new FileInfo(DbPath).Length;
        Assert.True(size > 0);
        Assert.Equal(new[] { "events", "meta" }, TablesInTheMainFileOnly());
        using (var again = SqliteEventStore.Open(DbPath)) Assert.Equal(0, again.Count());   // the schema is created once, never twice
        Assert.Equal(new[] { "events", "meta" }, TablesInTheMainFileOnly());
    }

    private static JsonElement LoadContract()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "contracts", "event.schema.json");
            if (File.Exists(file)) return JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
        }
        throw new FileNotFoundException("contracts/event.schema.json was not found above the test folder");
    }

    [Fact]
    public void The_shared_contract_describes_exactly_the_browser_event_the_pipeline_accepts_and_stores()
    {
        var contract = LoadContract();
        var required = contract.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        var allowed = contract.GetProperty("properties").EnumerateObject().Select(x => x.Name).ToHashSet();

        // the contract has no desktop, idle, presence or switch-count field, and only the extension as a source
        foreach (var gone in new[] { "app", "idle", "browser_idle", "presence", "switched_count", "switch_count" })
            Assert.DoesNotContain(gone, allowed);
        Assert.Equal("extension", contract.GetProperty("properties").GetProperty("source").GetProperty("const").GetString());
        Assert.False(contract.GetProperty("additionalProperties").GetBoolean());

        // an event as the pipeline expects it has every required field and nothing the contract does not know
        var fields = JsonDocument.Parse(Event("c-1")).RootElement.EnumerateObject().Select(x => x.Name).ToHashSet();
        Assert.Empty(required.Except(fields));
        Assert.Empty(fields.Except(allowed));
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(Event("c-1"), out _, out var error), error);

        // and what comes back out of SQLite has the same shape
        using var store = SqliteEventStore.Open(DbPath);
        store.Append([Event("c-1")]);
        var back = JsonDocument.Parse(Assert.Single(store.Read())).RootElement.EnumerateObject().Select(x => x.Name).ToHashSet();
        Assert.Empty(required.Except(back));
        Assert.Empty(back.Except(allowed));
    }

    [Fact]
    public async Task The_extension_connects_through_the_status_link_and_its_event_lands_in_SQLite_with_the_recording_session()
    {
        using var link = StartLink(new FakeGate { HasConsent = true }, new AgentState(), out var port);
        var status = await Client.SendAsync(StatusRequest(port));
        Assert.Equal($$"""{"consented":true,"trackingEnabled":true,"consentVersion":{{ConsentText.Version}}}""", await status.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, await Post(port, Event("auto-1")));
        Assert.Equal(HttpStatusCode.OK, await Post(port, Event("auto-2")));

        var sessions = Rows("SELECT DISTINCT session_id FROM events;");
        var session = Assert.Single(sessions);
        Assert.Matches("^[0-9a-f]{32}$", session);                          // a recording session was created by itself
        Assert.Equal(new[] { "auto-1", "auto-2" }, Rows("SELECT id FROM events ORDER BY id;"));
        Assert.Equal(2, File.ReadAllLines(Jsonl).Length);                   // and the older log still gets its copy
    }

    private LocalStatusLink StartLink(FakeGate gate, AgentState state, out int port)
    {
        var (link, _, p) = StartLink(gate, state);
        port = p;
        return link;
    }

    private static HttpRequestMessage StatusRequest(int port)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        request.Headers.TryAddWithoutValidation("X-DLA-Extension-Id", LinkTestData.ChromeId);
        return request;
    }

    [Fact]
    public async Task An_agent_restart_keeps_every_row_adds_no_duplicate_and_starts_a_new_session()
    {
        var gate = new FakeGate { HasConsent = true };
        string firstSession;
        using (var first = StartLink(gate, new AgentState(), out var port1))
        {
            Assert.Equal(HttpStatusCode.OK, await Post(port1, Event("restart-1")));
            firstSession = Assert.Single(Rows("SELECT DISTINCT session_id FROM events;"));
        }                                                                   // the agent stops: link, upkeep and database are closed

        using (var second = StartLink(gate, new AgentState(), out var port2))
        {
            Assert.Equal(HttpStatusCode.OK, await Post(port2, Event("restart-1")));      // the extension re-sends the same event after the restart
            Assert.Equal(HttpStatusCode.OK, await Post(port2, Event("restart-2")));
        }

        Assert.Equal(new[] { "restart-1", "restart-2" }, Rows("SELECT id FROM events ORDER BY id;"));   // one row each
        Assert.Equal("ok", Rows("PRAGMA quick_check;").Single());
        var sessions = Rows("SELECT session_id FROM events ORDER BY id;");
        Assert.Equal(firstSession, sessions[0]);                            // the stored row keeps the session it was recorded in
        Assert.NotEqual(firstSession, sessions[1]);                         // the new run has its own recording session
    }

    [Fact]
    public async Task Withdrawn_consent_or_a_pause_makes_the_link_refuse_events_and_nothing_is_stored()
    {
        var gate = new FakeGate { HasConsent = true };
        var state = new AgentState();
        using var link = StartLink(gate, state, out var port);
        Assert.Equal(HttpStatusCode.OK, await Post(port, Event("ok-1")));

        state.SetTrackingEnabled(false);
        Assert.Equal(HttpStatusCode.Forbidden, await Post(port, Event("paused")));
        state.SetTrackingEnabled(true);
        gate.HasConsent = false;
        Assert.Equal(HttpStatusCode.Forbidden, await Post(port, Event("revoked")));

        Assert.Equal(new[] { "ok-1" }, Rows("SELECT id FROM events;"));
    }

    [Fact]
    public async Task The_unified_timeline_reads_what_was_collected_automatically()
    {
        using (var link = StartLink(new FakeGate { HasConsent = true }, new AgentState(), out var port))
        {
            Assert.Equal(HttpStatusCode.OK, await Post(port, Event("tl-1")));
        }
        using var store = SqliteEventStore.Open(DbPath);
        var timeline = UnifiedTimeline.FromStore(store);
        Assert.Equal(20, timeline.ActiveSeconds);
        Assert.Equal("tl-1", Assert.Single(timeline.Entries).Id);
    }
}
