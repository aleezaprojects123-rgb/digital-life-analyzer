using System.Text;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Storage;
using Microsoft.Data.Sqlite;

namespace Dla.Agent.Tests;

/// <summary>The SQLite event store: every test uses a throwaway folder and a fixed clock. No real time is waited for.</summary>
public class SqliteStorageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly TempData _t = new();
    private string Db => Path.Combine(_t.Root, "dla.db");
    private string Jsonl => Path.Combine(_t.Root, "events.jsonl");
    private DateTimeOffset _now = Now;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_t.Root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private SqliteEventStore Open(string? path = null) => SqliteEventStore.Open(path ?? Db, () => _now);

    private static string At(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>A browser event in the normalized shape.</summary>
    private static string Browser(string id, DateTimeOffset start, int seconds = 10, string session = "sess-A", string platform = "youtube",
        string activity = "video", int count = 1, string domain = "youtube.com",
        string url = "https://www.youtube.com/watch", string? title = "A video", string extra = "") =>
        $$"""{"schema_version":"1","id":"{{id}}","start":"{{At(start)}}","end":"{{At(start.AddSeconds(seconds))}}","duration_seconds":{{seconds}},"source":"extension","platform":"{{platform}}","domain":"{{domain}}","url":"{{url}}","title":{{(title is null ? "null" : "\"" + title + "\"")}},"activity_type":"{{activity}}","content_count":{{count}},"session_id":"{{session}}","category":null,"confidence":null,"uncertain":false,"label_source":null{{extra}}}""";

    /// <summary>A line as the Python desktop tracker writes it.</summary>
    private static string Desktop(DateTimeOffset start, double seconds, string app = "code.exe") =>
        $$"""{"timestamp": "{{start:yyyy-MM-dd'T'HH:mm:ss.ffffffzzz}}", "source": "agent", "app": "{{app}}", "title": null, "duration_seconds": {{seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}""";

    /// <summary>A line written by the OLD tracker, before idle tracking was removed: the same line plus an "idle" flag.</summary>
    private static string LegacyDesktop(DateTimeOffset start, double seconds, string app, bool idle) =>
        Desktop(start, seconds, app)[..^1] + $", \"idle\": {(idle ? "true" : "false")}}}";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string[] Ids(IEnumerable<string> lines) => lines.Select(l => Parse(l).GetProperty("id").GetString()!).ToArray();

    // ---- insert / read --------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_event_is_stored_and_read_back_with_every_field()
    {
        using var s = Open();
        var line = Browser("e1", Now.AddMinutes(-5), 12, extra: ",\"media_title\":\"Song\",\"artist\":\"Band\",\"playback\":\"playing\"");
        Assert.Equal(new AppendResult(1, 0, 0), s.Append([line]));

        var back = Parse(Assert.Single(s.Read()));
        var sent = Parse(line);
        foreach (var p in sent.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(p.Value, back.GetProperty(p.Name)), p.Name);
        Assert.Equal(sent.EnumerateObject().Select(p => p.Name).Order(), back.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void A_browser_event_comes_back_in_exactly_the_normalized_field_order_of_the_extension()
    {
        using var s = Open();
        s.Append([Browser("e1", Now.AddMinutes(-5))]);
        var names = Parse(Assert.Single(s.Read())).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "schema_version", "id", "start", "end", "duration_seconds", "source", "platform", "domain", "url", "title", "activity_type",
            "content_count", "session_id", "category", "confidence", "uncertain", "label_source",
        }, names);
    }

    [Fact]
    public void A_batch_such_as_a_media_split_is_stored_together_and_keeps_its_counts()
    {
        using var s = Open();
        var r = s.Append([Browser("e1", Now.AddMinutes(-5), 20, count: 1), Browser("e1-m1", Now.AddMinutes(-5).AddSeconds(20), 10, count: 0)]);
        Assert.Equal(2, r.Inserted);
        Assert.Equal(new[] { 1, 0 }, s.Read().Select(l => Parse(l).GetProperty("content_count").GetInt32()).ToArray());
    }

    // ---- duplicates / idempotency ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_event_id_twice_is_stored_once()
    {
        using var s = Open();
        var line = Browser("dup", Now.AddMinutes(-5));
        Assert.Equal(new AppendResult(1, 0, 0), s.Append([line]));
        Assert.Equal(new AppendResult(0, 1, 0), s.Append([line]));
        Assert.Equal(1, s.Count());
    }

    [Fact]
    public void A_retry_after_a_restart_does_not_duplicate()
    {
        var line = Browser("retry", Now.AddMinutes(-5));
        using (var s = Open()) s.Append([line]);
        using (var s = Open()) { Assert.Equal(0, s.Append([line]).Inserted); Assert.Equal(1, s.Count()); }
    }

    [Fact]
    public void A_line_that_is_not_an_event_is_counted_as_rejected_and_never_stored()
    {
        using var s = Open();
        var r = s.Append(["not json", "[1]", "{\"id\":\"x\"}", Browser("ok", Now.AddMinutes(-1))]);
        Assert.Equal(new AppendResult(1, 0, 3), r);
        Assert.Equal(1, s.Count());
    }

    // ---- ordering -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Events_come_back_in_time_order_whatever_order_they_arrived_in()
    {
        using var s = Open();
        var t = Now.AddHours(-1);
        s.Append([Browser("c", t.AddSeconds(100)), Browser("a", t), Browser("d", t.AddSeconds(200)), Browser("b", t.AddSeconds(50))]);
        s.Append([Browser("m", t.AddSeconds(75), 5)]);
        Assert.Equal(new[] { "a", "b", "m", "c", "d" }, Ids(s.Read()));
    }

    [Fact]
    public void Events_that_start_in_the_same_second_are_ordered_by_end_then_id()
    {
        using var s = Open();
        var t = Now.AddHours(-1);
        s.Append([Browser("z", t, 5), Browser("b", t, 9), Browser("a", t, 9)]);
        Assert.Equal(new[] { "z", "a", "b" }, Ids(s.Read()));
    }

    [Fact]
    public void A_time_range_returns_the_events_that_overlap_it()
    {
        using var s = Open();
        var t = Now.AddHours(-2);
        s.Append([Browser("before", t, 10), Browser("inside", t.AddSeconds(100), 10), Browser("across", t.AddSeconds(55), 20), Browser("after", t.AddSeconds(500), 10)]);
        Assert.Equal(new[] { "across", "inside" }, Ids(s.Read(new EventQuery(From: t.AddSeconds(60), To: t.AddSeconds(200)))));
    }

    // ---- session / platform / activity ----------------------------------------------------------------------------------------

    [Fact]
    public void The_session_id_is_kept_and_can_be_queried()
    {
        using var s = Open();
        var t = Now.AddHours(-1);
        s.Append([Browser("a", t, session: "S1"), Browser("b", t.AddSeconds(20), session: "S1", platform: "instagram", activity: "reel"), Browser("c", t.AddSeconds(40), session: "S2")]);
        Assert.Equal(new[] { "a", "b" }, Ids(s.Read(new EventQuery(SessionId: "S1"))));
        Assert.All(s.Read(new EventQuery(SessionId: "S2")), l => Assert.Equal("S2", Parse(l).GetProperty("session_id").GetString()));
        Assert.Equal(new[] { "b" }, Ids(s.Read(new EventQuery(Platform: "instagram", ActivityType: "reel"))));
    }

    [Fact]
    public void A_session_is_kept_across_platforms_and_across_a_restart()
    {
        var t = Now.AddHours(-1);
        using (var s = Open()) s.Append([Browser("y", t, session: "S9"), Browser("i", t.AddSeconds(20), session: "S9", platform: "instagram", activity: "social_feed", count: 0), Browser("g", t.AddSeconds(40), session: "S9", platform: "unknown", activity: "webpage", count: 0, domain: "example.com", url: "https://example.com/x")]);
        using (var s = Open()) Assert.Equal(new[] { "y", "i", "g" }, Ids(s.Read(new EventQuery(SessionId: "S9"))));
    }

    // ---- time is stored as it happened; there is no idle ---------------------------------------------------------------------------

    [Fact]
    public void A_browser_event_is_stored_without_any_idle_information()
    {
        using var s = Open();
        s.Append([Browser("a", Now.AddMinutes(-5), 30)]);
        var names = Parse(Assert.Single(s.Read())).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("idle", names);
        Assert.DoesNotContain("browser_idle", names);
        Assert.DoesNotContain("switched_count", names);
    }

    [Fact]
    public void An_event_from_an_older_extension_build_that_still_sends_idle_fields_is_stored_without_them()
    {
        using var s = Open();
        var oldBuild = Browser("old-build-fields", Now.AddMinutes(-5)).Replace("\"content_count\":1,", "\"content_count\":1,\"app\":null,\"presence\":null,\"idle\":false,\"browser_idle\":true,");
        Assert.Equal(1, s.Append([oldBuild]).Inserted);                                    // still accepted ...
        var json = Assert.Single(s.Read());
        Assert.DoesNotContain("idle", json);                                               // ... and the idle information is dropped
        Assert.DoesNotContain("idle", string.Join(",", Query("SELECT name FROM pragma_table_info('events');", "name")));
        Assert.DoesNotContain("\"app\"", json);
        Assert.DoesNotContain("presence", json);
    }

    [Fact]
    public void Durations_are_whole_seconds()
    {
        using var s = Open();
        s.Append([Browser("a", Now.AddHours(-1), 7)]);
        var e = Parse(Assert.Single(s.Read()));
        Assert.Equal(JsonValueKind.Number, e.GetProperty("duration_seconds").ValueKind);
        Assert.Equal("7", e.GetProperty("duration_seconds").GetRawText());
    }

    // ---- the desktop tracker is gone: no desktop event is ever stored -----------------------------------------------------------------

    [Fact]
    public void An_event_from_the_old_desktop_tracker_is_never_stored()
    {
        using var s = Open();
        var t = Now.AddHours(-1);
        var agentStyleNormalized = Browser("agent-1", t, 60).Replace("\"source\":\"extension\"", "\"source\":\"agent\"");
        var result = s.Append([Desktop(t, 60, "Code.exe"), LegacyDesktop(t, 60, "Code.exe", idle: true), agentStyleNormalized, Browser("web-1", t, 60)]);
        Assert.Equal(new AppendResult(1, 0, 3), result);                                      // only the browser event is stored
        Assert.Equal(new[] { "web-1" }, Ids(s.Read()));
    }

    [Fact]
    public void VS_Code_or_any_other_program_name_cannot_be_stored_because_there_is_no_place_for_it()
    {
        using var s = Open();
        s.Append([Browser("web", Now.AddHours(-1), 30).Replace("\"content_count\":1,", "\"content_count\":1,\"app\":\"Code.exe\",")]);
        Assert.DoesNotContain("Code.exe", Assert.Single(s.Read()));
        Assert.DoesNotContain("app", Query("SELECT name FROM pragma_table_info('events');", "name"));
    }

    // ---- older database files: versions 1, 2 and 3 ----------------------------------------------------------------------------------------

    private static readonly string[] CurrentColumns =
    [
        "id", "schema_version", "start_ms", "end_ms", "start_text", "end_text", "duration_seconds", "source", "session_id", "platform",
        "activity_type", "content_count", "domain", "url", "title", "category", "confidence",
        "uncertain", "label_source", "extra_json",
    ];

    /// <summary>
    /// Builds a real older file. Version 1: with app, presence, idle and browser_idle. Version 2: the same plus switched_count. Version 3:
    /// app and presence but no idle columns and no switched_count. With rows: two browser periods and two periods the old desktop tracker wrote.
    /// </summary>
    private void BuildOldDatabase(int version, bool withRows, long offsetMs = 0)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        c.Open();
        void Run(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        var switched = version == 2 ? "switched_count INTEGER NOT NULL DEFAULT 0 CHECK (switched_count >= 0)," : "";
        var idleColumns = version < 3 ? "idle INTEGER NOT NULL CHECK (idle IN (0,1)), browser_idle INTEGER NOT NULL DEFAULT 0 CHECK (browser_idle IN (0,1))," : "";
        Run("PRAGMA auto_vacuum=INCREMENTAL;");
        Run("PRAGMA journal_mode=WAL;");
        Run($"""
            CREATE TABLE events (
              id TEXT NOT NULL PRIMARY KEY, schema_version TEXT NOT NULL DEFAULT '1', start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
              start_text TEXT NOT NULL, end_text TEXT NOT NULL, duration_seconds REAL NOT NULL CHECK (duration_seconds >= 0),
              source TEXT NOT NULL CHECK (source IN ('extension','agent')), session_id TEXT, platform TEXT, activity_type TEXT,
              content_count INTEGER NOT NULL DEFAULT 0 CHECK (content_count >= 0),
              {switched}
              domain TEXT, url TEXT, title TEXT, app TEXT,
              {idleColumns}
              presence TEXT, category TEXT, confidence REAL, uncertain INTEGER NOT NULL DEFAULT 0 CHECK (uncertain IN (0,1)),
              label_source TEXT, extra_json TEXT, CHECK (end_ms >= start_ms));
            """);
        Run("CREATE INDEX idx_events_time ON events (start_ms, end_ms);");
        Run("CREATE INDEX idx_events_end ON events (end_ms);");
        Run("CREATE INDEX idx_events_session ON events (session_id, start_ms);");
        Run("CREATE INDEX idx_events_platform ON events (platform, activity_type, start_ms);");
        Run("CREATE INDEX idx_events_source ON events (source, start_ms);");
        Run("CREATE TABLE meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);");
        if (withRows)
        {
            string Insert(string id, long fromSec, long toSec, string source, string session, string platform, string activity, int count, string domain, string app, int idle, int browserIdle, int switchedValue)
            {
                long from = offsetMs + fromSec * 1000, to = offsetMs + toSec * 1000;
                var cols = "id, start_ms, end_ms, start_text, end_text, duration_seconds, source, session_id, platform, activity_type, content_count, domain, app"
                           + (version < 3 ? ", idle, browser_idle" : "") + (version == 2 ? ", switched_count" : "");
                string Q(string v) => v == "" ? "NULL" : "'" + v + "'";
                var vals = $"'{id}', {from}, {to}, '{At(DateTimeOffset.FromUnixTimeMilliseconds(from))}', '{At(DateTimeOffset.FromUnixTimeMilliseconds(to))}', {toSec - fromSec}, '{source}', "
                           + $"{Q(session)}, {Q(platform)}, {Q(activity)}, {count}, {Q(domain)}, {Q(app)}"
                           + (version < 3 ? $", {idle}, {browserIdle}" : "") + (version == 2 ? $", {switchedValue}" : "");
                return $"INSERT INTO events ({cols}) VALUES ({vals});";
            }
            Run(Insert("old-a", 1, 11, "extension", "S0", "youtube", "video", 1, "youtube.com", "", 0, 0, 1));       // a browser period
            Run(Insert("old-b", 11, 31, "extension", "S0", "instagram", "reel", 1, "instagram.com", "", 0, 1, 1));   // another browser period
            Run(Insert("old-c", 31, 41, "agent", "", "", "", 0, "", "code.exe", 0, 0, 0));                           // a period the old desktop tracker wrote
            Run(Insert("old-d", 41, 61, "agent", "", "", "", 0, "", "winword.exe", 1, 0, 0));                        // another one
            Run("INSERT INTO meta (key, value) VALUES ('jsonl_offset', 'C:\\x\\events.jsonl|1234');");
        }
        Run($"PRAGMA user_version = {version};");
    }

    private long Legacy() => long.Parse(Query($"SELECT COUNT(*) AS n FROM {SqliteEventStore.LegacyTable};", "n").Single());

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void An_older_database_moves_to_version_4_keeps_every_browser_event_and_archives_the_old_desktop_rows(int oldVersion)
    {
        BuildOldDatabase(oldVersion, withRows: true);

        using (var s = Open())
        {
            Assert.Equal(2, s.Count());                                                       // the two browser events are all that is in events
            var all = s.Read().Select(Parse).ToList();
            Assert.Equal(new[] { "old-a", "old-b" }, all.Select(e => e.GetProperty("id").GetString()).ToArray());
            Assert.Equal(new[] { 1, 1 }, all.Select(e => e.GetProperty("content_count").GetInt32()).ToArray());          // content counts untouched
            Assert.Equal(new[] { "S0", "S0" }, all.Select(e => e.GetProperty("session_id").GetString()).ToArray());       // and sessions
            Assert.Equal(new[] { "youtube", "instagram" }, all.Select(e => e.GetProperty("platform").GetString()).ToArray());
            Assert.Equal(new[] { "video", "reel" }, all.Select(e => e.GetProperty("activity_type").GetString()).ToArray());
            Assert.All(all, e =>
            {
                foreach (var gone in new[] { "app", "presence", "idle", "browser_idle", "switched_count" }) Assert.False(e.TryGetProperty(gone, out _), gone);
                Assert.Equal("extension", e.GetProperty("source").GetString());
            });
            Assert.Equal(1, s.Append([Browser("after", Now.AddMinutes(-5))]).Inserted);       // and writing works
        }

        Assert.Equal(4L, long.Parse(Query("PRAGMA user_version;", "user_version").Single()));
        Assert.Equal(CurrentColumns, Query("SELECT name FROM pragma_table_info('events');", "name"));
        Assert.Equal(new[] { "idx_events_end", "idx_events_platform", "idx_events_session", "idx_events_time" }, Query("SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'idx_%' ORDER BY name;", "name"));
        Assert.Equal("jsonl_offset", Query("SELECT key FROM meta;", "key").Single());                                        // the import position survived
        Assert.Equal("ok", Query("PRAGMA integrity_check;", "integrity_check").Single());

        // the old desktop rows are not lost: they sit in their own archive table, with their app name and their time
        Assert.Equal(2, Legacy());
        Assert.Equal(new[] { "code.exe", "winword.exe" }, Query($"SELECT app FROM {SqliteEventStore.LegacyTable} ORDER BY start_ms;", "app"));
        Assert.Equal(new[] { "10", "20" }, Query($"SELECT CAST(duration_seconds AS INTEGER) AS d FROM {SqliteEventStore.LegacyTable} ORDER BY start_ms;", "d"));

        using (var again = Open())                                                            // opening again changes nothing and does not fail
            Assert.Equal(3, again.Count());
        Assert.Equal(4L, long.Parse(Query("PRAGMA user_version;", "user_version").Single()));
        Assert.Equal(2, Legacy());
    }

    [Fact]
    public void The_archived_desktop_rows_are_not_part_of_any_read_or_of_the_timeline()
    {
        BuildOldDatabase(3, withRows: true);
        using var s = Open();
        var timeline = UnifiedTimeline.FromStore(s);
        Assert.Equal(2, timeline.Entries.Count);
        Assert.Equal(30, timeline.ActiveSeconds);                                             // 10 + 20 s of browser time; the desktop rows add nothing
        Assert.DoesNotContain("code.exe", string.Join("", s.Read()));
        Assert.DoesNotContain("winword.exe", string.Join("", s.Read()));
    }

    [Fact]
    public void The_archive_of_old_desktop_rows_follows_the_30_day_rule_and_the_table_disappears_when_it_is_empty()
    {
        var yesterday = Now.AddDays(-1).ToUnixTimeMilliseconds();
        BuildOldDatabase(3, withRows: true, offsetMs: yesterday);
        using var s = Open();
        Assert.Equal(0, s.ApplyRetention());                                                  // nothing is 30 days old yet
        Assert.Equal(2, Legacy());

        _now = Now.AddDays(31);                                                               // a month later the archive is past its 30 days
        Assert.Equal(4, s.ApplyRetention());                                                  // two browser rows and two archived desktop rows
        Assert.Equal(0, s.Count());
        Assert.Empty(Query($"SELECT name FROM sqlite_master WHERE name = '{SqliteEventStore.LegacyTable}';", "name"));   // and the table itself is gone
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void An_empty_older_database_also_moves_to_version_4_without_leaving_an_archive_table_and_a_current_file_is_left_alone(int oldVersion)
    {
        BuildOldDatabase(oldVersion, withRows: false);
        using (var s = Open()) Assert.Equal(0, s.Count());
        Assert.Equal(4L, long.Parse(Query("PRAGMA user_version;", "user_version").Single()));
        Assert.Equal(CurrentColumns, Query("SELECT name FROM pragma_table_info('events');", "name"));
        Assert.Empty(Query($"SELECT name FROM sqlite_master WHERE name = '{SqliteEventStore.LegacyTable}';", "name"));
        using (var s = Open()) Assert.Equal(0, s.Count());                                    // a version-4 file needs no change at all
        Assert.Equal(4L, long.Parse(Query("PRAGMA user_version;", "user_version").Single()));
    }

    [Fact]
    public void A_fresh_database_is_created_directly_at_version_4_with_only_browser_event_columns()
    {
        using (var s = Open()) Assert.Equal(0, s.Count());
        Assert.Equal(4L, long.Parse(Query("PRAGMA user_version;", "user_version").Single()));
        Assert.Equal(4, SqliteEventStore.SchemaVersion);
        Assert.Equal(CurrentColumns, Query("SELECT name FROM pragma_table_info('events');", "name"));
        Assert.Empty(Query($"SELECT name FROM sqlite_master WHERE name = '{SqliteEventStore.LegacyTable}';", "name"));
        Assert.Equal(new[] { "events", "meta" }, Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;", "name"));
    }

    [Fact]
    public void The_table_only_accepts_browser_events()
    {
        using var s = Open();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO events (id, start_ms, end_ms, start_text, end_text, duration_seconds, source) VALUES ('x', 1, 2, 'a', 'b', 1, 'agent');";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());                          // the database itself refuses a desktop row
    }

    [Fact]
    public void An_event_from_an_older_extension_build_that_still_sends_switched_count_is_stored_without_it()
    {
        using var s = Open();
        var line = Browser("old-build", Now.AddMinutes(-5)).Replace("\"content_count\":1,", "\"content_count\":1,\"switched_count\":1,");
        Assert.Equal(1, s.Append([line]).Inserted);                                       // accepted ...
        var json = Assert.Single(s.Read());
        Assert.DoesNotContain("switched_count", json);                                    // ... but never stored (only known fields are)
        Assert.DoesNotContain("switched_count", string.Join(",", Query("SELECT name FROM pragma_table_info('events');", "name")));
    }

    // ---- 30-day retention -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Events_that_ended_more_than_30_days_ago_are_deleted_and_newer_ones_are_kept()
    {
        using var s = Open();
        s.Append([
            Browser("old", Now.AddDays(-31), 60),
            Browser("edge-old", Now.AddDays(-30).AddSeconds(-11), 10),      // ended 1 second before the cutoff
            Browser("edge-kept", Now.AddDays(-30).AddSeconds(-10), 10),     // ended exactly at the cutoff: kept
            Browser("long", Now.AddDays(-31), 2 * 24 * 3600),               // started 31 days ago, ended 29 days ago: kept
            Browser("new", Now.AddDays(-1), 60),
        ]);
        Assert.Equal(2, s.ApplyRetention());
        Assert.Equal(new[] { "long", "edge-kept", "new" }, Ids(s.Read()));
    }

    [Fact]
    public void Cleanup_is_safe_to_repeat_and_with_nothing_old_it_deletes_nothing()
    {
        using var s = Open();
        s.Append([Browser("new", Now.AddDays(-1))]);
        Assert.Equal(0, s.ApplyRetention());
        Assert.Equal(0, s.ApplyRetention());
        Assert.Equal(1, s.Count());
    }

    [Fact]
    public void A_wrong_computer_clock_can_never_wipe_the_history()
    {
        using var s = Open();
        s.Append([Browser("old", Now.AddDays(-31)), Browser("new", Now.AddDays(-1))]);
        _now = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, s.ApplyRetention());
        Assert.Equal(2, s.Count());
        _now = new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);       // a clock far in the future is what really deletes (it is the real "now" then)
        Assert.Equal(2, s.ApplyRetention());
    }

    [Fact]
    public void The_service_cleans_up_by_itself_but_not_more_than_every_6_hours()
    {
        using var s = Open();
        s.Append([Browser("old", Now.AddDays(-31)), Browser("new", Now.AddDays(-1))]);
        using var svc = new StorageService(s, jsonlPath: null, clock: () => _now);
        svc.RunMaintenance();
        Assert.Equal(new[] { "new" }, Ids(s.Read()));

        s.Append([Browser("older", Now.AddDays(-40))]);                    // arrives later (e.g. a delayed import)
        _now = Now.AddHours(5);
        svc.RunMaintenance();                                               // too soon: no cleanup yet
        Assert.Equal(2, s.Count());
        _now = Now.AddHours(6);
        svc.RunMaintenance();
        Assert.Equal(new[] { "new" }, Ids(s.Read()));
    }

    // ---- restart / crash safety -----------------------------------------------------------------------------------------------

    [Fact]
    public void Events_survive_a_normal_restart()
    {
        using (var s = Open()) s.Append([Browser("a", Now.AddMinutes(-3)), Browser("b", Now.AddMinutes(-2))]);
        using (var s = Open()) Assert.Equal(new[] { "a", "b" }, Ids(s.Read()));
    }

    [Fact]
    public void Events_survive_a_crash_where_the_store_was_never_closed()
    {
        var first = Open();
        first.Append([Browser("a", Now.AddMinutes(-3)), Browser("b", Now.AddMinutes(-2))]);
        // no Dispose: the process "died". Committed rows live in the write-ahead log and the next start recovers them.
        using var second = Open();
        Assert.Equal(new[] { "a", "b" }, Ids(second.Read()));
        second.Append([Browser("c", Now.AddMinutes(-1))]);
        Assert.Equal(3, second.Count());
        first.Dispose();
    }

    [Fact]
    public void A_batch_that_failed_halfway_leaves_nothing_behind()
    {
        using var s = Open();
        s.Append([Browser("keep", Now.AddMinutes(-9))]);
        s.Dispose();                                                        // a closed store: the write cannot happen
        Assert.Throws<ObjectDisposedException>(() => s.Append([Browser("x", Now.AddMinutes(-1)), Browser("y", Now.AddMinutes(-1))]));
        using var again = Open();
        Assert.Equal(new[] { "keep" }, Ids(again.Read()));
    }

    [Fact]
    public void A_damaged_database_is_moved_aside_and_a_fresh_one_is_made()
    {
        File.WriteAllBytes(Db, Encoding.ASCII.GetBytes(new string('x', 4096)));
        using var s = Open();
        Assert.Equal(0, s.Count());
        s.Append([Browser("a", Now.AddMinutes(-1))]);
        Assert.Single(Directory.GetFiles(_t.Root, "dla.db.corrupt-*"));
    }

    [Fact]
    public void A_database_from_a_newer_version_is_never_overwritten()
    {
        using (var s = Open()) s.Append([Browser("a", Now.AddMinutes(-1))]);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(Db).Length;
        Assert.Throws<InvalidOperationException>(() => Open());
        Assert.Empty(Directory.GetFiles(_t.Root, "dla.db.corrupt-*"));
        Assert.True(File.Exists(Db) && File.ReadAllBytes(Db).Length >= before);
    }

    // ---- schema, indexes, privacy ---------------------------------------------------------------------------------------------

    private List<string> Query(string sql, string column)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(r.GetOrdinal(column)));
        return list;
    }

    [Fact]
    public void The_table_has_only_the_normalized_event_columns_and_nothing_that_could_hold_page_content()
    {
        using var s = Open();
        var columns = Query("SELECT name FROM pragma_table_info('events');", "name");
        Assert.Equal(new[]
        {
            "id", "schema_version", "start_ms", "end_ms", "start_text", "end_text", "duration_seconds", "source", "session_id", "platform",
            "activity_type", "content_count", "domain", "url", "title", "category", "confidence",
            "uncertain", "label_source", "extra_json",
        }, columns);
    }

    [Fact]
    public void The_indexes_for_time_session_platform_and_activity_exist_and_are_used()
    {
        using var s = Open();
        s.Append([Browser("a", Now.AddMinutes(-5))]);
        var names = Query("SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'idx_%';", "name");
        Assert.Contains("idx_events_time", names);
        Assert.Contains("idx_events_end", names);
        Assert.Contains("idx_events_session", names);
        Assert.Contains("idx_events_platform", names);

        string Plan(string where) => string.Join(" | ", Query($"EXPLAIN QUERY PLAN SELECT * FROM events WHERE {where};", "detail"));
        Assert.Contains("idx_events_session", Plan("session_id = 'x' AND start_ms > 0"));
        Assert.Contains("idx_events_platform", Plan("platform = 'youtube' AND activity_type = 'video' AND start_ms > 0"));
        Assert.Contains("idx_events_time", Plan("start_ms >= 0 AND start_ms < 10"));
        Assert.Contains("idx_events_end", Plan("end_ms < 10"));
    }

    [Fact]
    public void A_query_string_or_fragment_in_a_url_is_cut_and_unknown_fields_are_never_stored()
    {
        using var s = Open();
        s.Append([Browser("a", Now.AddMinutes(-5), url: "https://example.com/p?token=SECRET#frag", extra: ",\"page_text\":\"PASSWORD hunter2\",\"screenshot\":\"AAAA\",\"typed\":\"abc\"")]);
        var json = Assert.Single(s.Read());
        Assert.Equal("https://example.com/p", Parse(json).GetProperty("url").GetString());
        foreach (var banned in new[] { "SECRET", "frag", "hunter2", "PASSWORD", "screenshot", "typed", "page_text" })
            Assert.DoesNotContain(banned, json);
    }

    [Fact]
    public void The_database_file_is_a_local_file_with_wal_and_full_sync()
    {
        using var s = Open();
        Assert.Equal("wal", Query("PRAGMA journal_mode;", "journal_mode").Single());
        Assert.Equal((long)SqliteEventStore.SchemaVersion, long.Parse(Query("PRAGMA user_version;", "user_version").Single()));
        Assert.Equal(4, SqliteEventStore.SchemaVersion);
    }

    // ---- importing the older JSONL file ---------------------------------------------------------------------------------------

    [Fact]
    public void Importing_twice_or_into_a_brand_new_database_never_makes_duplicates()
    {
        var t = Now.AddHours(-2);
        File.WriteAllText(Jsonl, Browser("a", t, 5) + "\n" + Browser("b", t.AddSeconds(5), 5) + "\n");
        using (var s = Open()) { s.ImportJsonl(Jsonl); Assert.Equal(0, s.ImportJsonl(Jsonl).Imported); Assert.Equal(2, s.Count()); }

        var second = Path.Combine(_t.Root, "other.db");                    // a database that lost its progress marker: the ids still match
        using var s2 = Open(second);
        Assert.Equal(2, s2.ImportJsonl(Jsonl).Imported);
        Assert.Equal(Ids(Open().Read()), Ids(s2.Read()));
    }

    [Fact]
    public void Only_new_whole_lines_are_imported_and_a_half_written_line_waits()
    {
        var t = Now.AddHours(-2);
        File.WriteAllText(Jsonl, Browser("a", t, 5) + "\n");
        using var s = Open();
        Assert.Equal(1, s.ImportJsonl(Jsonl).Imported);

        var half = Browser("b", t.AddSeconds(5), 5);
        File.AppendAllText(Jsonl, half[..40]);                              // the writer is in the middle of a line
        Assert.Equal(0, s.ImportJsonl(Jsonl).Imported);
        File.AppendAllText(Jsonl, half[40..] + "\n" + Browser("c", t.AddSeconds(10), 5) + "\n");
        Assert.Equal(2, s.ImportJsonl(Jsonl).Imported);
        Assert.Equal(3, s.Count());
    }

    [Fact]
    public void The_import_skips_lines_that_are_already_past_the_30_days_and_survives_junk_lines()
    {
        File.WriteAllText(Jsonl, string.Join("\n", Browser("old", Now.AddDays(-40), 5), "garbage", "", Browser("new", Now.AddDays(-1), 5)) + "\n");
        using var s = Open();
        var r = s.ImportJsonl(Jsonl);
        Assert.Equal(1, r.Imported);
        Assert.Equal(1, r.Rejected);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("new", Parse(Assert.Single(s.Read())).GetProperty("id").GetString());
    }

    [Fact]
    public void A_replaced_shorter_log_file_is_read_from_the_start_again_without_duplicates()
    {
        var t = Now.AddHours(-2);
        File.WriteAllText(Jsonl, Browser("a", t, 5) + "\n" + Browser("b", t.AddSeconds(5), 5) + "\n");
        using var s = Open();
        s.ImportJsonl(Jsonl);
        File.WriteAllText(Jsonl, Browser("b", t.AddSeconds(5), 5) + "\n");            // shorter than the saved position
        Assert.Equal(0, s.ImportJsonl(Jsonl).Imported);
        Assert.Equal(2, s.Count());
    }

    [Fact]
    public void Lines_the_old_desktop_tracker_wrote_are_not_imported_and_do_not_stop_the_import()
    {
        var t = Now.AddHours(-2);
        File.WriteAllText(Jsonl, string.Join("\n", LegacyDesktop(t, 5.1, "cmd.exe", idle: false), LegacyDesktop(t.AddSeconds(5.1), 56.2, "cmd.exe", idle: true), Browser("web", t.AddMinutes(5), 30)) + "\n");
        using var s = Open();
        var r = s.ImportJsonl(Jsonl);
        Assert.Equal(new SqliteEventStore.ImportResult(1, 0, 2), r);                        // the two desktop lines are rejected, the browser line is imported
        Assert.Equal(new[] { "web" }, Ids(s.Read()));
        Assert.Equal(0, s.ImportJsonl(Jsonl).Imported);                                      // and the next import has nothing new
    }

    [Fact]
    public void Browser_lines_in_the_older_log_are_imported_in_their_full_normalized_shape()
    {
        File.WriteAllText(Jsonl, Browser("b1", Now.AddHours(-1), session: "S1") + "\n");
        using var s = Open();
        s.ImportJsonl(Jsonl);
        var e = Parse(Assert.Single(s.Read()));
        Assert.Equal(("extension", "S1", "youtube"), (e.GetProperty("source").GetString(), e.GetProperty("session_id").GetString(), e.GetProperty("platform").GetString()));
    }

    // ---- the sink, the HTTP path and the service ------------------------------------------------------------------------------

    [Fact]
    public void The_sink_stores_first_and_mirrors_to_the_older_log_only_what_was_new()
    {
        using var s = Open();
        var mirror = new JsonlEventSink(Jsonl);
        var sink = new SqliteEventSink(s, mirror);
        var line = Browser("m1", Now.AddMinutes(-2));
        Assert.True(sink.AppendEvent(line));
        Assert.True(sink.AppendEvent(line));                                // a duplicate is still "stored" for the browser, but written to the log once
        Assert.Single(File.ReadAllLines(Jsonl));
        Assert.Equal(1, s.Count());
    }

    [Fact]
    public void When_the_database_cannot_write_the_sink_says_no_so_the_browser_keeps_and_retries_the_event()
    {
        var s = Open();
        var sink = new SqliteEventSink(s, new JsonlEventSink(Jsonl));
        s.Dispose();
        Assert.False(sink.AppendEvent(Browser("x", Now.AddMinutes(-1))));
        Assert.False(File.Exists(Jsonl), "nothing is written to the log when the database did not commit");
    }

    [Fact]
    public void A_browser_post_goes_through_validation_and_the_session_stamp_into_the_database()
    {
        using var s = Open();
        var sink = new SqliteEventSink(s);
        var config = LinkTestData.Config(49999, LinkTestData.ChromeId);
        var body = Browser("http-1", Now.AddMinutes(-2), session: "null-placeholder", count: 0).Replace("\"session_id\":\"null-placeholder\"", "\"session_id\":null");
        var raw = LinkTestData.Request(49999, method: "POST", path: "/events", origin: "ORIGIN", extra: $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

        var answer = LocalStatusLink.Evaluate(header, config, new FixedStatus(new AgentStatus(true, true, 1)), body, sink, enrich: b => [SessionStamp.Apply(b, "real-session")]);
        Assert.Equal(200, answer.Status);

        var e = Parse(Assert.Single(s.Read()));
        Assert.Equal("real-session", e.GetProperty("session_id").GetString());
        Assert.False(e.TryGetProperty("idle", out _));
        Assert.False(e.TryGetProperty("browser_idle", out _));
    }

    [Fact]
    public void The_service_imports_on_start_and_a_last_time_on_stop()
    {
        var t = Now.AddHours(-2);
        File.WriteAllText(Jsonl, Browser("a", t, 5) + "\n");
        var s = Open();
        var svc = new StorageService(s, Jsonl, clock: () => _now);
        svc.Start();
        Assert.Equal(1, s.Count());
        File.AppendAllText(Jsonl, Browser("b", t.AddSeconds(5), 5) + "\n");
        svc.Dispose();                                                     // the final import also closes the database
        using var again = Open();
        Assert.Equal(2, again.Count());
    }
}
