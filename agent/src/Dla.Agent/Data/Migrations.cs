using Microsoft.Data.Sqlite;

namespace Dla.Agent.Data;

public sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Ordered, append-only list of schema migrations. Never edit a shipped migration: add a new one.
///
/// Privacy by schema: no column anywhere holds keystrokes or screenshots. The only BLOB column is
/// upload_queue.payload, which holds an already-encrypted summary. OCR keeps only counts and a label.
/// All timestamps are integer milliseconds since the Unix epoch (UTC).
/// </summary>
public static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, "initial_schema", """
            CREATE TABLE events (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                source         TEXT    NOT NULL CHECK (source IN ('agent','extension')),
                app            TEXT,
                site           TEXT,
                title          TEXT,
                start_ts       INTEGER NOT NULL,
                end_ts         INTEGER,
                category       TEXT    CHECK (category IS NULL OR category IN ('Study','Work','Entertainment','Social Media','Other')),
                confidence     REAL    CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
                is_unknown     INTEGER NOT NULL DEFAULT 0 CHECK (is_unknown IN (0,1)),
                unknown_reason TEXT,
                CHECK (end_ts IS NULL OR end_ts >= start_ts),
                CHECK (is_unknown = 0 OR (category IS NULL AND confidence IS NULL))
            );
            CREATE INDEX ix_events_start ON events(start_ts);
            CREATE INDEX ix_events_end ON events(end_ts);

            CREATE TABLE personal_rules (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                match_kind TEXT    NOT NULL CHECK (match_kind IN ('app','site','title_contains')),
                pattern    TEXT    NOT NULL COLLATE NOCASE CHECK (length(pattern) > 0),
                category   TEXT    NOT NULL CHECK (category IN ('Study','Work','Entertainment','Social Media','Other')),
                created_at INTEGER NOT NULL,
                UNIQUE (match_kind, pattern)
            );

            CREATE TABLE settings (
                name       TEXT    PRIMARY KEY,
                value      TEXT    NOT NULL,
                updated_at INTEGER NOT NULL
            );
            INSERT INTO settings (name, value, updated_at) VALUES
                ('retention_days',         '30',  0),
                ('idle_threshold_seconds', '180', 0),
                ('paused',                 '0',   0);

            CREATE TABLE exclusions (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                kind       TEXT    NOT NULL CHECK (kind IN ('app','site')),
                pattern    TEXT    NOT NULL COLLATE NOCASE CHECK (length(pattern) > 0),
                created_at INTEGER NOT NULL,
                UNIQUE (kind, pattern)
            );

            -- Append-only history; the newest row is the current consent.
            CREATE TABLE consent_records (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                state        TEXT    NOT NULL CHECK (state IN ('Accepted','Declined','Withdrawn')),
                text_version INTEGER NOT NULL,
                at           INTEGER NOT NULL
            );

            -- One row per OCR capture. Counts and a label only: no text, no image.
            CREATE TABLE ocr_audit_log (
                id                         INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at                INTEGER NOT NULL,
                app                        TEXT    NOT NULL,
                window_title               TEXT,
                trigger_reason             TEXT    NOT NULL,
                chars_read                 INTEGER NOT NULL CHECK (chars_read >= 0),
                label                      TEXT,
                confidence                 REAL    CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
                capture_destroyed_after_ms INTEGER NOT NULL CHECK (capture_destroyed_after_ms >= 0)
            );
            CREATE INDEX ix_ocr_audit_time ON ocr_audit_log(captured_at);

            -- Encrypted summaries waiting to be synced. payload is ciphertext produced elsewhere.
            -- payload_hash makes enqueueing the same summary twice a no-op (no duplicates).
            CREATE TABLE upload_queue (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                payload         BLOB    NOT NULL,
                payload_hash    TEXT    NOT NULL UNIQUE,
                period_start    INTEGER NOT NULL,
                period_end      INTEGER NOT NULL,
                created_at      INTEGER NOT NULL,
                attempts        INTEGER NOT NULL DEFAULT 0,
                last_attempt_at INTEGER,
                sent_at         INTEGER,
                CHECK (period_end >= period_start)
            );
            CREATE INDEX ix_upload_pending ON upload_queue(sent_at, created_at);

            CREATE TABLE lifecycle_markers (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                at         INTEGER NOT NULL,
                kind       TEXT    NOT NULL CHECK (kind IN
                           ('start','clean_shutdown','suspend','resume','crash_detected','extension_silent_start','extension_silent_end')),
                session_id TEXT    NOT NULL,
                detail     TEXT
            );
            CREATE INDEX ix_lifecycle_at ON lifecycle_markers(at);
            """),
    ];
}

public sealed class SchemaTooNewException(int found, int supported)
    : Exception($"Database schema version {found} is newer than this agent supports ({supported}). Update the agent.");

public static class MigrationRunner
{
    /// <summary>Applies every pending migration, each in its own transaction. Safe to call on every start.</summary>
    public static void Apply(SqliteConnection conn, IReadOnlyList<Migration>? migrations = null)
    {
        migrations ??= Migrations.All;

        Exec(conn, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)");

        var current = Convert.ToInt32(Scalar(conn, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations"));
        var latest = migrations.Max(m => m.Version);
        if (current > latest) throw new SchemaTooNewException(current, latest);

        foreach (var m in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = m.Sql;
                cmd.ExecuteNonQuery();
            }
            using (var rec = conn.CreateCommand())
            {
                rec.Transaction = tx;
                rec.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($v, $n, $t)";
                rec.Parameters.AddWithValue("$v", m.Version);
                rec.Parameters.AddWithValue("$n", m.Name);
                rec.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                rec.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    internal static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
