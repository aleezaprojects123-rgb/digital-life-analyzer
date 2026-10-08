using System.Text.RegularExpressions;
using Dla.Agent.Consent;
using Dla.Agent.Data;
using Microsoft.Data.Sqlite;

namespace Dla.Agent.Tests;

public sealed class TestDb : IDisposable
{
    private readonly TempDir _dir = new();
    public DlaDatabase Db { get; }
    public FakeClock Clock { get; } = new();
    public string FilePath => Path.Combine(_dir.Path, "dla.db");

    public TestDb() => Db = DlaDatabase.OpenAndMigrate(FilePath);

    public SettingsRepository Settings => new(Db, Clock.Func);
    public EventRepository Events => new(Db);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    public static long Scalar(DlaDatabase db, string sql)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static void Exec(DlaDatabase db, string sql)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

public class MigrationTests
{
    private static readonly string[] ExpectedTables =
        ["events", "personal_rules", "settings", "exclusions", "consent_records", "ocr_audit_log", "upload_queue", "lifecycle_markers", "schema_migrations"];

    [Fact]
    public void Migrations_run_from_an_empty_database()
    {
        using var t = new TestDb();
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));

        foreach (var expected in ExpectedTables) Assert.Contains(expected, tables);
        Assert.Equal(Migrations.All.Max(m => m.Version), TestDb.Scalar(t.Db, "SELECT MAX(version) FROM schema_migrations"));
    }

    [Fact]
    public void Running_migrations_again_changes_nothing()
    {
        using var t = new TestDb();
        t.Settings.RetentionDays = 45;
        var again = DlaDatabase.OpenAndMigrate(t.FilePath);   // simulates the next agent start

        Assert.Equal(Migrations.All.Count, TestDb.Scalar(again, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(45, new SettingsRepository(again).RetentionDays); // data survived
    }

    [Fact]
    public void A_database_from_a_newer_agent_is_refused_not_modified()
    {
        using var t = new TestDb();
        TestDb.Exec(t.Db, "INSERT INTO schema_migrations (version, name, applied_at) VALUES (999, 'future', 0)");
        Assert.Throws<SchemaTooNewException>(() => DlaDatabase.OpenAndMigrate(t.FilePath));
    }

    [Fact]
    public void Migration_numbers_are_unique_and_consecutive()
    {
        var versions = Migrations.All.Select(m => m.Version).ToArray();
        Assert.Equal(Enumerable.Range(1, versions.Length), versions);
    }

    [Fact]
    public void A_failing_migration_rolls_back_completely()
    {
        using var t = new TestDb();
        var bad = new List<Migration>(Migrations.All)
        {
            new(Migrations.All.Count + 1, "broken", "CREATE TABLE half_done (x INTEGER); THIS IS NOT SQL;")
        };
        using var c = t.Db.Open();
        Assert.ThrowsAny<SqliteException>(() => MigrationRunner.Apply(c, bad));
        Assert.Equal(0, TestDb.Scalar(t.Db, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done'"));
        Assert.Equal(Migrations.All.Count, TestDb.Scalar(t.Db, "SELECT COUNT(*) FROM schema_migrations"));
    }

    [Fact]
    public void Defaults_are_30_days_retention_3_minutes_idle_not_paused()
    {
        using var t = new TestDb();
        Assert.Equal(30, t.Settings.RetentionDays);
        Assert.Equal(TimeSpan.FromMinutes(3), t.Settings.IdleThreshold);
        Assert.False(t.Settings.Paused);
        Assert.Empty(t.Settings.Exclusions());
        Assert.Null(t.Settings.LatestConsent());
    }

    [Fact]
    public void Wal_and_foreign_keys_are_on()
    {
        using var t = new TestDb();
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA foreign_keys"; Assert.Equal(1L, cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA secure_delete"; Assert.Equal(1L, cmd.ExecuteScalar());
    }
}

public class PrivacySchemaTests
{
    private static readonly Regex Banned = new("keystroke|keylog|typed|typing|screenshot|image|pixel|bitmap|ocr_text|raw_text|clipboard", RegexOptions.IgnoreCase);

    [Fact]
    public void No_table_or_column_can_hold_keystrokes_or_screenshots()
    {
        using var t = new TestDb();
        using var c = t.Db.Open();
        using var tables = c.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>();
        using (var r = tables.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));

        var blobColumns = new List<string>();
        foreach (var table in names)
        {
            Assert.DoesNotMatch(Banned, table);
            using var cols = c.CreateCommand();
            cols.CommandText = $"PRAGMA table_info('{table}')";
            using var r = cols.ExecuteReader();
            while (r.Read())
            {
                var column = r.GetString(1);
                var type = r.GetString(2);
                Assert.DoesNotMatch(Banned, column);
                if (type.Equals("BLOB", StringComparison.OrdinalIgnoreCase) || type.Length == 0) blobColumns.Add($"{table}.{column}");
            }
        }
        // the only binary column is the already-encrypted summary waiting to be uploaded
        Assert.Equal(["upload_queue.payload"], blobColumns);
    }
}

public class EventRepositoryTests
{
    private static NewEvent AppEvent(DateTimeOffset start, DateTimeOffset? end = null, double? conf = 0.9, string? cat = "Work") =>
        new(EventSource.Agent, start, end, App: "code.exe", Title: "main.cs - DLA", Category: cat, Confidence: conf);

    [Fact]
    public void Event_round_trips_with_millisecond_precision()
    {
        using var t = new TestDb();
        var start = new DateTimeOffset(2026, 10, 9, 9, 15, 30, 123, TimeSpan.Zero);
        var end = start.AddMilliseconds(4567);
        var id = t.Events.Insert(new(EventSource.Extension, start, end, Site: "docs.python.org", Title: "Python docs", Category: "Study", Confidence: 0.82));

        var row = t.Events.Get(id)!;
        Assert.Equal(EventSource.Extension, row.Source);
        Assert.Equal(start, row.Start);
        Assert.Equal(end, row.End);
        Assert.Equal("docs.python.org", row.Site);
        Assert.Equal("Study", row.Category);
        Assert.Equal(0.82, row.Confidence);
        Assert.False(row.IsUnknown);
    }

    [Fact]
    public void Unknown_gaps_are_stored_with_a_marker_and_no_category()
    {
        using var t = new TestDb();
        var s = t.Clock.Now;
        var id = t.Events.Insert(new(EventSource.Agent, s, s.AddMinutes(7), IsUnknown: true, UnknownReason: "extension_silent"));
        var row = t.Events.Get(id)!;
        Assert.True(row.IsUnknown);
        Assert.Equal("extension_silent", row.UnknownReason);
        Assert.Null(row.Category);
        Assert.Throws<SqliteException>(() => t.Events.Insert(new(EventSource.Agent, s, s, IsUnknown: true, Category: "Work", Confidence: 0.9)));
    }

    [Fact]
    public void Database_rejects_bad_data()
    {
        using var t = new TestDb();
        var s = t.Clock.Now;
        Assert.Throws<SqliteException>(() => t.Events.Insert(AppEvent(s, conf: 1.5)));                     // confidence out of range
        Assert.Throws<SqliteException>(() => t.Events.Insert(AppEvent(s, cat: "Gaming")));                // not a spec category
        Assert.Throws<SqliteException>(() => t.Events.Insert(AppEvent(s, end: s.AddSeconds(-1))));        // ends before it starts
        TestDb.Exec(t.Db, "SELECT 1");
        Assert.Throws<SqliteException>(() => TestDb.Exec(t.Db,
            "INSERT INTO events (source, start_ts) VALUES ('screenshotter', 1)"));                         // unknown source
        Assert.Equal(0, t.Events.Count());
    }

    [Fact]
    public void Needs_review_lists_only_labels_under_60_percent()
    {
        using var t = new TestDb();
        var s = t.Clock.Now;
        t.Events.Insert(AppEvent(s, conf: 0.59));
        t.Events.Insert(AppEvent(s, conf: 0.60));
        t.Events.Insert(AppEvent(s, conf: 0.95));
        t.Events.Insert(new(EventSource.Agent, s, s.AddMinutes(1), IsUnknown: true));
        var review = t.Events.NeedsReview();
        Assert.Single(review);
        Assert.Equal(0.59, review[0].Confidence);
    }

    [Fact]
    public void Range_returns_overlapping_events_and_open_events_can_be_closed()
    {
        using var t = new TestDb();
        var s = t.Clock.Now;
        var open = t.Events.Insert(AppEvent(s));                       // still running
        t.Events.Insert(AppEvent(s.AddHours(-3), s.AddHours(-2)));     // outside the window
        Assert.Single(t.Events.Range(s.AddMinutes(-1), s.AddMinutes(1)));

        t.Events.SetEnd(open, s.AddMinutes(5));
        Assert.Equal(s.AddMinutes(5), t.Events.Get(open)!.End);
        t.Events.SetCategory(open, "Study", 0.7);
        Assert.Equal("Study", t.Events.Get(open)!.Category);
    }
}

public class OtherRepositoryTests
{
    [Fact]
    public void Personal_rules_are_unique_case_insensitively_and_upsert_changes_the_category()
    {
        using var t = new TestDb();
        var rules = new RuleRepository(t.Db, t.Clock.Func);
        rules.Upsert("site", "YouTube.com", "Entertainment");
        rules.Upsert("site", "youtube.com", "Study");   // user corrected it
        var list = rules.List();
        Assert.Single(list);
        Assert.Equal("Study", list[0].Category);
        Assert.Throws<SqliteException>(() => rules.Upsert("site", "x.com", "Fun"));
        rules.Delete(list[0].Id);
        Assert.Empty(rules.List());
    }

    [Fact]
    public void Settings_validate_and_persist()
    {
        using var t = new TestDb();
        var s = t.Settings;
        s.RetentionDays = 7;
        s.IdleThreshold = TimeSpan.FromMinutes(5);
        s.Paused = true;
        Assert.Throws<ArgumentOutOfRangeException>(() => s.RetentionDays = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.IdleThreshold = TimeSpan.FromSeconds(5));

        var reopened = new SettingsRepository(DlaDatabase.OpenAndMigrate(t.FilePath));
        Assert.Equal(7, reopened.RetentionDays);
        Assert.Equal(TimeSpan.FromMinutes(5), reopened.IdleThreshold);
        Assert.True(reopened.Paused);
    }

    [Fact]
    public void Exclusions_ignore_duplicates_and_case()
    {
        using var t = new TestDb();
        t.Settings.AddExclusion("app", "KeePass.exe");
        t.Settings.AddExclusion("app", "keepass.EXE");
        t.Settings.AddExclusion("site", "bank.example.com");
        Assert.Equal(2, t.Settings.Exclusions().Count);
        t.Settings.RemoveExclusion("app", "keepass.exe");
        Assert.Single(t.Settings.Exclusions());
    }

    [Fact]
    public void Consent_record_keeps_history_and_newest_wins()
    {
        using var t = new TestDb();
        var s = t.Settings;
        s.AppendConsent(ConsentState.Accepted, ConsentText.Version);
        t.Clock.Advance(TimeSpan.FromDays(1));
        s.AppendConsent(ConsentState.Withdrawn, ConsentText.Version);

        var latest = s.LatestConsent()!;
        Assert.Equal(ConsentState.Withdrawn, latest.State);
        Assert.Equal(t.Clock.Now, latest.At);
        Assert.Equal(2, TestDb.Scalar(t.Db, "SELECT COUNT(*) FROM consent_records"));
        Assert.Throws<ArgumentException>(() => s.AppendConsent(ConsentState.None, 1));
    }

    [Fact]
    public void Upload_queue_never_holds_duplicates_and_tracks_sending()
    {
        using var t = new TestDb();
        var q = new UploadQueueRepository(t.Db, t.Clock.Func);
        var payload = new byte[] { 1, 2, 3, 4 };
        Assert.True(q.Enqueue(payload, t.Clock.Now.AddHours(-1), t.Clock.Now));
        Assert.False(q.Enqueue(payload, t.Clock.Now.AddHours(-1), t.Clock.Now));  // same summary again
        Assert.True(q.Enqueue([9, 9], t.Clock.Now, t.Clock.Now));

        var pending = q.Pending();
        Assert.Equal(2, pending.Count);
        Assert.Equal(payload, pending[0].Payload);

        q.MarkFailed(pending[0].Id);
        Assert.Equal(1, q.Pending().First(p => p.Id == pending[0].Id).Attempts);
        q.MarkSent(pending[0].Id);
        Assert.Single(q.Pending());
    }

    [Fact]
    public void Ocr_audit_log_stores_counts_and_label_only()
    {
        using var t = new TestDb();
        var audit = new OcrAuditRepository(t.Db);
        audit.Add(new(t.Clock.Now, "game.exe", "Level 3", "title-unclear", 412, "Entertainment", 0.74, 180));
        var e = audit.List().Single();
        Assert.Equal(412, e.CharsRead);
        Assert.Equal("Entertainment", e.Label);
        Assert.Equal(180, e.CaptureDestroyedAfterMs);
    }

    [Fact]
    public void Lifecycle_markers_are_stored_in_order_and_unknown_kinds_are_rejected()
    {
        using var t = new TestDb();
        var m = new LifecycleRepository(t.Db);
        m.Add(t.Clock.Now, "start", "s1");
        m.Add(t.Clock.Now.AddMinutes(1), "suspend", "s1");
        m.Add(t.Clock.Now.AddMinutes(2), "clean_shutdown", "s1", "SystemShutdown");
        Assert.Equal(["start", "suspend", "clean_shutdown"], m.List().Select(x => x.Kind));
        Assert.Throws<SqliteException>(() => m.Add(t.Clock.Now, "keylog", "s1"));
    }
}

public class RetentionPurgeTests
{
    private static long Add(TestDb t, TimeSpan age, TimeSpan? duration = null)
    {
        var start = t.Clock.Now - age;
        return t.Events.Insert(new(EventSource.Agent, start, start + (duration ?? TimeSpan.FromMinutes(5)), App: "a.exe", Category: "Work", Confidence: 0.9));
    }

    [Fact]
    public void Purge_removes_events_older_than_30_days_and_keeps_newer_ones()
    {
        using var t = new TestDb();
        var old1 = Add(t, TimeSpan.FromDays(31));
        var old2 = Add(t, TimeSpan.FromDays(90));
        var recent1 = Add(t, TimeSpan.FromDays(29));
        var recent2 = Add(t, TimeSpan.FromHours(1));

        var result = new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge();

        Assert.Equal(2, result.Events);
        Assert.Null(t.Events.Get(old1));
        Assert.Null(t.Events.Get(old2));
        Assert.NotNull(t.Events.Get(recent1));
        Assert.NotNull(t.Events.Get(recent2));
    }

    [Fact]
    public void Purge_follows_the_retention_setting()
    {
        using var t = new TestDb();
        Add(t, TimeSpan.FromDays(10));
        Add(t, TimeSpan.FromDays(2));

        t.Settings.RetentionDays = 7;
        Assert.Equal(1, new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge().Events);
        Assert.Equal(1, t.Events.Count());

        t.Settings.RetentionDays = 1;
        Assert.Equal(1, new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge().Events);
        Assert.Equal(0, t.Events.Count());
    }

    [Fact]
    public void An_event_that_ended_inside_the_window_is_kept_even_if_it_started_before()
    {
        using var t = new TestDb();
        // started 30 days + 1 h ago, ran for 3 hours: ended 29 days + 22 h ago, so inside the 30-day window
        var longEvent = Add(t, TimeSpan.FromDays(30) + TimeSpan.FromHours(1), TimeSpan.FromHours(3));
        new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge();
        Assert.NotNull(t.Events.Get(longEvent));
    }

    [Fact]
    public void An_open_event_is_judged_by_its_start_and_unknown_gaps_age_out_too()
    {
        using var t = new TestDb();
        var oldOpen = t.Events.Insert(new(EventSource.Agent, t.Clock.Now.AddDays(-40), null, App: "a.exe"));
        var oldGap = t.Events.Insert(new(EventSource.Agent, t.Clock.Now.AddDays(-40), t.Clock.Now.AddDays(-39), IsUnknown: true));
        var newOpen = t.Events.Insert(new(EventSource.Agent, t.Clock.Now.AddMinutes(-1), null, App: "a.exe"));

        new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge();

        Assert.Null(t.Events.Get(oldOpen));
        Assert.Null(t.Events.Get(oldGap));
        Assert.NotNull(t.Events.Get(newOpen));
    }

    [Fact]
    public void Purge_also_ages_out_audit_markers_and_sent_uploads_but_never_unsent_uploads()
    {
        using var t = new TestDb();
        var audit = new OcrAuditRepository(t.Db);
        var markers = new LifecycleRepository(t.Db);
        var q = new UploadQueueRepository(t.Db, () => t.Clock.Now.AddDays(-40));

        audit.Add(new(t.Clock.Now.AddDays(-40), "g.exe", null, "x", 1, null, null, 100));
        audit.Add(new(t.Clock.Now.AddDays(-1), "g.exe", null, "x", 1, null, null, 100));
        markers.Add(t.Clock.Now.AddDays(-40), "start", "old");
        markers.Add(t.Clock.Now.AddDays(-1), "start", "new");
        q.Enqueue([1], t.Clock.Now.AddDays(-41), t.Clock.Now.AddDays(-40));   // old and sent
        q.Enqueue([1], t.Clock.Now.AddDays(-41), t.Clock.Now.AddDays(-40));   // same payload again, ignored
        q.Enqueue([3], t.Clock.Now.AddDays(-41), t.Clock.Now.AddDays(-40));   // old but never sent
        q.MarkSent(q.Pending().First(p => p.Payload[0] == 1).Id);

        var result = new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge();

        Assert.Equal(new PurgeResult(0, 1, 1, 1), result);
        Assert.Single(audit.List());
        Assert.Single(markers.List());
        Assert.Single(q.Pending());                       // the unsent summary survives
    }

    [Fact]
    public void Purge_on_an_empty_database_is_a_no_op_and_purge_actually_shrinks_row_content()
    {
        using var t = new TestDb();
        Assert.Equal(0, new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge().Total);

        var id = t.Events.Insert(new(EventSource.Agent, t.Clock.Now.AddDays(-60), t.Clock.Now.AddDays(-60).AddMinutes(1), App: "a.exe", Title: "SECRET-TITLE-MARKER"));
        new RetentionPurger(t.Db, t.Settings, t.Clock.Func).Purge();
        Assert.Null(t.Events.Get(id));

        // secure_delete + WAL checkpoint: the purged title must not remain readable in the database file
        SqliteConnection.ClearAllPools();
        var bytes = new List<byte>();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(t.FilePath)!, "dla.db*"))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes.AddRange(ms.ToArray());
        }
        var text = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        Assert.DoesNotContain("SECRET-TITLE-MARKER", text);
    }
}
