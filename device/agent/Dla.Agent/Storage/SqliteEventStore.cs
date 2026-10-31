using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Dla.Agent.Storage;

/// <summary>What to read. Every field is optional; an event is returned when its time span overlaps [From, To).</summary>
public sealed record EventQuery(
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? SessionId = null, string? Platform = null,
    string? ActivityType = null);

public readonly record struct AppendResult(int Inserted, int Duplicates, int Rejected);

/// <summary>
/// The local source of truth: one SQLite file (WAL, fully synced) holding finished, normalized activity periods.
/// One row per finished period, never per mouse move. The id is the primary key, so the same event arriving twice (an extension
/// retry, an agent restart, a re-import of the old JSONL file) is stored once. Nothing here reads a window, a page or an input.
///
/// A damaged file is moved aside (dla.db.corrupt-...) and a fresh one is made; the old JSONL file is then imported again.
/// A file written by a NEWER schema version is never touched or replaced.
/// </summary>
public sealed class SqliteEventStore : IDisposable
{
    // Schema history. 1: the first version. 2: for a short time it had a switched_count column. 3: no idle columns and no switched_count.
    // 4: the browser extension is the only collector, so there are no app and presence columns and only browser events. An older file is
    // brought to version 4 when it is opened (see MoveToCurrentVersion). Every browser row is kept. Rows the old desktop tracker wrote are
    // not deleted: they move to the table legacy_desktop_events, which the 30-day cleanup empties and then removes.
    public const int SchemaVersion = 4;

    public const string LegacyTable = "legacy_desktop_events";
    public const int RetentionDays = 30;

    private readonly object _lock = new();
    private readonly SqliteConnection _db;
    private readonly Func<DateTimeOffset> _clock;
    private bool _disposed;

    public string Path { get; }

    private SqliteEventStore(string path, SqliteConnection db, Func<DateTimeOffset> clock)
    {
        Path = path;
        _db = db;
        _clock = clock;
    }

    public static SqliteEventStore Open(string path, Func<DateTimeOffset>? clock = null, IAgentLog? log = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            return OpenOnce(path, clock);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)   // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            return Quarantine(path, clock, log, ex.Message);
        }
        catch (CorruptDatabaseException ex)
        {
            return Quarantine(path, clock, log, ex.Message);
        }
    }

    private sealed class CorruptDatabaseException(string message) : Exception(message);

    private static SqliteEventStore Quarantine(string path, Func<DateTimeOffset> clock, IAgentLog? log, string why)
    {
        var aside = $"{path}.corrupt-{clock():yyyyMMddHHmmss}";
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var from = path + suffix;
            if (File.Exists(from)) File.Move(from, aside + suffix, overwrite: true);
        }
        log?.Write("The event database was damaged and was moved aside; a new one was created and the old log is imported again.");
        _ = why;   // never written: only the fact that it happened
        return OpenOnce(path, clock);
    }

    private static SqliteEventStore OpenOnce(string path, Func<DateTimeOffset> clock)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            db.Open();
            Exec(db, "PRAGMA busy_timeout=5000;");

            var version = Scalar<long>(db, "PRAGMA user_version;");
            if (version > SchemaVersion)
                throw new InvalidOperationException($"The event database has schema version {version}, newer than this program ({SchemaVersion}). It is left untouched.");

            if (version == 0) Exec(db, "PRAGMA auto_vacuum=INCREMENTAL;");   // only takes effect on a brand-new file
            Exec(db, "PRAGMA journal_mode=WAL;");
            Exec(db, "PRAGMA synchronous=FULL;");

            if (Scalar<string>(db, "PRAGMA quick_check;") != "ok") throw new CorruptDatabaseException("quick_check failed");

            if (version is >= 1 and < SchemaVersion)
            {
                MoveToCurrentVersion(db);
                version = SchemaVersion;
            }
            if (version == 0)
            {
                CreateSchema(db);
                // Write the new tables into dla.db itself right away. Otherwise they would sit only in the -wal file until the first
                // automatic checkpoint, and a tool that opens dla.db alone (or a copy of it) would see an empty database.
                Exec(db, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            return new SqliteEventStore(path, db, clock);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Brings a version-1, 2 or 3 file to the current version, in ONE transaction (a crash leaves the file as it was and the next start
    /// does it again):
    ///  1. the periods the old desktop tracker wrote (source 'agent') are COPIED to legacy_desktop_events, so they are not lost,
    ///  2. the events table is rebuilt with only the columns of a browser event, holding only the browser rows (every one of them is kept,
    ///     with its time, session, platform, activity type and content count),
    ///  3. the indexes are made again and the version is set.
    /// Nothing is read from a page, and no browser row is changed.
    /// </summary>
    private static void MoveToCurrentVersion(SqliteConnection db)
    {
        const string kept = "id, schema_version, start_ms, end_ms, start_text, end_text, duration_seconds, source, session_id, platform, activity_type, " +
                            "content_count, domain, url, title, category, confidence, uncertain, label_source, extra_json";
        using var tx = db.BeginTransaction();
        void Run(string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Run($"CREATE TABLE IF NOT EXISTS {LegacyTable} (id TEXT NOT NULL PRIMARY KEY, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, " +
            "start_text TEXT NOT NULL, end_text TEXT NOT NULL, duration_seconds REAL NOT NULL, app TEXT, title TEXT);");
        Run($"INSERT OR IGNORE INTO {LegacyTable} (id, start_ms, end_ms, start_text, end_text, duration_seconds, app, title) " +
            "SELECT id, start_ms, end_ms, start_text, end_text, duration_seconds, app, title FROM events WHERE source = 'agent';");

        Run(EventsTableSql("events_new"));
        Run($"INSERT INTO events_new ({kept}) SELECT {kept} FROM events WHERE source = 'extension';");
        Run("DROP TABLE events;");                      // its indexes go with it
        Run("ALTER TABLE events_new RENAME TO events;");
        foreach (var index in IndexSql) Run(index);

        // nothing was ever archived: do not leave an empty table behind
        using (var count = db.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = $"SELECT COUNT(*) FROM {LegacyTable};";
            if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) Run($"DROP TABLE {LegacyTable};");
        }

        Run($"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    private static void CreateSchema(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        foreach (var sql in Schema())
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string EventsTableSql(string name) => $"""
        CREATE TABLE {name} (
          id               TEXT    NOT NULL PRIMARY KEY,
          schema_version   TEXT    NOT NULL DEFAULT '1',
          start_ms         INTEGER NOT NULL,
          end_ms           INTEGER NOT NULL,
          start_text       TEXT    NOT NULL,
          end_text         TEXT    NOT NULL,
          duration_seconds REAL    NOT NULL CHECK (duration_seconds >= 0),
          source           TEXT    NOT NULL CHECK (source = 'extension'),
          session_id       TEXT,
          platform         TEXT,
          activity_type    TEXT,
          content_count    INTEGER NOT NULL DEFAULT 0 CHECK (content_count >= 0),
          domain           TEXT,
          url              TEXT,
          title            TEXT,
          category         TEXT,
          confidence       REAL,
          uncertain        INTEGER NOT NULL DEFAULT 0 CHECK (uncertain IN (0,1)),
          label_source     TEXT,
          extra_json       TEXT,
          CHECK (end_ms >= start_ms)
        );
        """;

    private static readonly string[] IndexSql =
    [
        "CREATE INDEX idx_events_time     ON events (start_ms, end_ms);",
        "CREATE INDEX idx_events_end       ON events (end_ms);",
        "CREATE INDEX idx_events_session  ON events (session_id, start_ms);",
        "CREATE INDEX idx_events_platform  ON events (platform, activity_type, start_ms);",
    ];

    // user_version is set last, in the same transaction as the tables: a crash while creating leaves an empty (version 0) file.
    private static string[] Schema() =>
    [
        EventsTableSql("events"),
        .. IndexSql,
        "CREATE TABLE meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);",
        $"PRAGMA user_version = {SchemaVersion};",
    ];

    // ---- writing ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stores several event lines together: all of them or none. A line that is already stored (same id) is skipped, not an error.
    /// A line that is not an event is counted as rejected and the rest are stored. A database error throws, and then nothing is stored.
    /// </summary>
    public AppendResult Append(IReadOnlyList<string> jsonLines)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction();
            var result = InsertAll(jsonLines, tx, minEndMs: null);
            tx.Commit();
            return result;
        }
    }

    private AppendResult InsertAll(IEnumerable<string> lines, SqliteTransaction tx, long? minEndMs)
    {
        int inserted = 0, duplicates = 0, rejected = 0;
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertSql;
        foreach (var name in InsertParameters) cmd.Parameters.Add(new SqliteParameter(name, null));

        foreach (var line in lines)
        {
            if (!EventMapper.TryParse(line, out var row, out _)) { rejected++; continue; }
            if (minEndMs is { } floor && row.EndMs < floor) { duplicates++; continue; }   // older than the retention window: not stored
            Bind(cmd, row);
            if (cmd.ExecuteNonQuery() == 1) inserted++; else duplicates++;
        }
        return new AppendResult(inserted, duplicates, rejected);
    }

    private static readonly string[] InsertParameters =
        ["$id", "$sv", "$sms", "$ems", "$st", "$et", "$dur", "$src", "$sid", "$plat", "$act", "$cc", "$dom", "$url", "$title",
         "$cat", "$conf", "$unc", "$ls", "$extra"];

    private const string InsertSql = """
        INSERT OR IGNORE INTO events
          (id, schema_version, start_ms, end_ms, start_text, end_text, duration_seconds, source, session_id, platform, activity_type,
           content_count, domain, url, title, category, confidence, uncertain, label_source, extra_json)
        VALUES
          ($id, $sv, $sms, $ems, $st, $et, $dur, $src, $sid, $plat, $act, $cc, $dom, $url, $title,
           $cat, $conf, $unc, $ls, $extra);
        """;

    private static void Bind(SqliteCommand cmd, EventRow r)
    {
        object V(object? o) => o ?? DBNull.Value;
        var p = cmd.Parameters;
        p["$id"].Value = r.Id; p["$sv"].Value = r.SchemaVersion; p["$sms"].Value = r.StartMs; p["$ems"].Value = r.EndMs;
        p["$st"].Value = r.StartText; p["$et"].Value = r.EndText; p["$dur"].Value = r.DurationSeconds; p["$src"].Value = r.Source;
        p["$sid"].Value = V(r.SessionId); p["$plat"].Value = V(r.Platform); p["$act"].Value = V(r.ActivityType); p["$cc"].Value = r.ContentCount;
        p["$dom"].Value = V(r.Domain); p["$url"].Value = V(r.Url); p["$title"].Value = V(r.Title);
        p["$cat"].Value = V(r.Category);
        p["$conf"].Value = V(r.Confidence); p["$unc"].Value = r.Uncertain ? 1 : 0; p["$ls"].Value = V(r.LabelSource); p["$extra"].Value = V(r.ExtraJson);
    }

    // ---- reading ------------------------------------------------------------------------------------------------------------

    /// <summary>Events as normalized JSON lines, in time order (start, then end, then id).</summary>
    public IReadOnlyList<string> Read(EventQuery? query = null)
    {
        query ??= new EventQuery();
        var where = new List<string>();
        var args = new List<(string, object)>();
        if (query.From is { } f) { where.Add("end_ms > $from"); args.Add(("$from", f.ToUnixTimeMilliseconds())); }
        if (query.To is { } t) { where.Add("start_ms < $to"); args.Add(("$to", t.ToUnixTimeMilliseconds())); }
        if (query.SessionId is { } s) { where.Add("session_id = $sid"); args.Add(("$sid", s)); }
        if (query.Platform is { } pl) { where.Add("platform = $plat"); args.Add(("$plat", pl)); }
        if (query.ActivityType is { } a) { where.Add("activity_type = $act"); args.Add(("$act", a)); }

        var sql = "SELECT * FROM events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY start_ms, end_ms, id;";
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(ToJson(reader));
            return list;
        }
    }

    public long Count()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return Scalar<long>(_db, "SELECT COUNT(*) FROM events;");
        }
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The normalized browser event again, with every contract field (a value that does not apply is null).</summary>
    private static string ToJson(SqliteDataReader r)
    {
        string? S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
        var o = new JsonObject
        {
            ["schema_version"] = S("schema_version"),
            ["id"] = S("id"),
            ["start"] = S("start_text"),
            ["end"] = S("end_text"),
            ["duration_seconds"] = DurationNode(r.GetDouble(r.GetOrdinal("duration_seconds"))),
            ["source"] = S("source"),
            ["platform"] = S("platform"),
            ["domain"] = S("domain"),
            ["url"] = S("url"),
            ["title"] = S("title"),
            ["activity_type"] = S("activity_type"),
            ["content_count"] = r.GetInt32(r.GetOrdinal("content_count")),
            ["session_id"] = S("session_id"),
            ["category"] = S("category"),
            ["confidence"] = r.IsDBNull(r.GetOrdinal("confidence")) ? null : JsonValue.Create(r.GetDouble(r.GetOrdinal("confidence"))),
            ["uncertain"] = r.GetInt32(r.GetOrdinal("uncertain")) == 1,
            ["label_source"] = S("label_source"),
        };
        if (S("extra_json") is { } extra && JsonNode.Parse(extra) is JsonObject media)
            foreach (var (k, v) in media) o[k] = v?.DeepClone();
        return o.ToJsonString(Compact);
    }

    // whole seconds stay integers (the contract says integer)
    private static JsonNode DurationNode(double d) => d == Math.Floor(d) && d < long.MaxValue ? JsonValue.Create((long)d) : JsonValue.Create(d);

    // ---- retention ----------------------------------------------------------------------------------------------------------

    /// <summary>The oldest end time that is kept: now minus 30 days.</summary>
    public DateTimeOffset RetentionCutoff() => _clock().AddDays(-RetentionDays);

    /// <summary>
    /// Deletes periods that ENDED more than 30 days ago, in one transaction (all or nothing). It never deletes anything newer, and it does
    /// nothing at all when the computer's clock is obviously wrong (before 2024), so a bad clock can never wipe the history.
    /// The archive of old desktop rows (legacy_desktop_events) follows the same 30-day rule, and the table is removed once it is empty.
    /// </summary>
    public int ApplyRetention()
    {
        if (_clock() < new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)) return 0;
        var cutoff = RetentionCutoff().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            ThrowIfDisposed();
            int deleted;
            using (var tx = _db.BeginTransaction())
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM events WHERE end_ms < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                deleted = cmd.ExecuteNonQuery();

                using var archived = _db.CreateCommand();
                archived.Transaction = tx;
                archived.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{LegacyTable}';";
                if (Convert.ToInt64(archived.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
                {
                    using var old = _db.CreateCommand();
                    old.Transaction = tx;
                    old.CommandText = $"DELETE FROM {LegacyTable} WHERE end_ms < $cutoff;";
                    old.Parameters.AddWithValue("$cutoff", cutoff);
                    deleted += old.ExecuteNonQuery();

                    using var left = _db.CreateCommand();
                    left.Transaction = tx;
                    left.CommandText = $"SELECT COUNT(*) FROM {LegacyTable};";
                    if (Convert.ToInt64(left.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                    {
                        using var drop = _db.CreateCommand();
                        drop.Transaction = tx;
                        drop.CommandText = $"DROP TABLE {LegacyTable};";
                        drop.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            if (deleted > 0)
            {
                Exec(_db, "PRAGMA incremental_vacuum;");
                Exec(_db, "PRAGMA wal_checkpoint(PASSIVE);");
            }
            return deleted;
        }
    }

    // ---- importing the older JSONL file --------------------------------------------------------------------------------------

    public readonly record struct ImportResult(int Imported, int Skipped, int Rejected);

    /// <summary>
    /// Copies new lines of the older events.jsonl (the mirror of browser events, written before SQLite and still kept) into the database.
    /// Only whole lines are read (a line the writer is still writing waits for the next call). How far it got is saved in the same
    /// transaction as the rows, so a crash repeats at most the last batch, and a repeat changes nothing (same ids).
    /// Lines that ended before the retention cutoff are skipped, so cleanup and import never fight.
    /// </summary>
    public ImportResult ImportJsonl(string jsonlPath)
    {
        if (!File.Exists(jsonlPath)) return default;
        var key = "jsonl_offset";
        lock (_lock)
        {
            ThrowIfDisposed();
            var (savedPath, savedOffset) = ReadOffset(key);
            var fullPath = System.IO.Path.GetFullPath(jsonlPath);
            var offset = string.Equals(savedPath, fullPath, StringComparison.OrdinalIgnoreCase) ? savedOffset : 0;

            byte[] bytes;
            using (var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (offset > fs.Length) offset = 0;   // the file was replaced by a shorter one: start again (rows already stored are skipped)
                fs.Position = offset;
                bytes = new byte[fs.Length - offset];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = fs.Read(bytes, read, bytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read < bytes.Length) bytes = bytes[..read];
            }

            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            if (lastNewline < 0) return default;
            var text = new UTF8Encoding(false).GetString(bytes, 0, lastNewline + 1);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

            using var tx = _db.BeginTransaction();
            var floor = _clock() < new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) ? (long?)null : RetentionCutoff().ToUnixTimeMilliseconds();
            var r = InsertAll(lines, tx, floor);
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", $"{fullPath}|{offset + lastNewline + 1}");
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return new ImportResult(r.Inserted, r.Duplicates, r.Rejected);
        }
    }

    private (string? Path, long Offset) ReadOffset(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        if (cmd.ExecuteScalar() is not string value) return (null, 0);
        var bar = value.LastIndexOf('|');
        return bar > 0 && long.TryParse(value[(bar + 1)..], out var off) ? (value[..bar], off) : (null, 0);
    }

    // ---- housekeeping -------------------------------------------------------------------------------------------------------

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { Exec(_db, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { /* the next start recovers from the WAL */ }
            _db.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }
}
