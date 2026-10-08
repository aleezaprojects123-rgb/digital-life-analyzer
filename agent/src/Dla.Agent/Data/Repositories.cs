using System.Security.Cryptography;
using Dla.Agent.Consent;
using Microsoft.Data.Sqlite;

namespace Dla.Agent.Data;

// ---- events --------------------------------------------------------------------------------------------------

public enum EventSource { Agent, Extension }

public sealed record NewEvent(
    EventSource Source,
    DateTimeOffset Start,
    DateTimeOffset? End = null,
    string? App = null,
    string? Site = null,
    string? Title = null,
    string? Category = null,
    double? Confidence = null,
    bool IsUnknown = false,
    string? UnknownReason = null);

public sealed record EventRow(
    long Id, EventSource Source, string? App, string? Site, string? Title,
    DateTimeOffset Start, DateTimeOffset? End, string? Category, double? Confidence,
    bool IsUnknown, string? UnknownReason);

public sealed class EventRepository(DlaDatabase db)
{
    /// <summary>FR-7: labels under 60% confidence go to the Needs Review list.</summary>
    public const double NeedsReviewBelow = 0.60;

    private const string Columns = "id, source, app, site, title, start_ts, end_ts, category, confidence, is_unknown, unknown_reason";

    public long Insert(NewEvent e)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, """
            INSERT INTO events (source, app, site, title, start_ts, end_ts, category, confidence, is_unknown, unknown_reason)
            VALUES ($src, $app, $site, $title, $s, $e, $cat, $conf, $unk, $why);
            SELECT last_insert_rowid();
            """,
            ("$src", e.Source.ToString().ToLowerInvariant()), ("$app", e.App), ("$site", e.Site), ("$title", e.Title),
            ("$s", Db.ToMs(e.Start)), ("$e", e.End is { } end ? Db.ToMs(end) : null),
            ("$cat", e.Category), ("$conf", e.Confidence), ("$unk", e.IsUnknown ? 1 : 0), ("$why", e.UnknownReason));
        return (long)cmd.ExecuteScalar()!;
    }

    public EventRow? Get(long id)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, $"SELECT {Columns} FROM events WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Events overlapping [from, to), oldest first.</summary>
    public IReadOnlyList<EventRow> Range(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c,
            $"SELECT {Columns} FROM events WHERE start_ts < $to AND COALESCE(end_ts, start_ts) >= $from ORDER BY start_ts, id",
            ("$from", Db.ToMs(from)), ("$to", Db.ToMs(to)));
        return ReadAll(cmd);
    }

    public IReadOnlyList<EventRow> NeedsReview()
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c,
            $"SELECT {Columns} FROM events WHERE is_unknown = 0 AND confidence IS NOT NULL AND confidence < $t ORDER BY start_ts, id",
            ("$t", NeedsReviewBelow));
        return ReadAll(cmd);
    }

    public void SetEnd(long id, DateTimeOffset end) => Exec("UPDATE events SET end_ts = $e WHERE id = $id", ("$e", Db.ToMs(end)), ("$id", id));

    public void SetCategory(long id, string category, double confidence) =>
        Exec("UPDATE events SET category = $c, confidence = $f WHERE id = $id AND is_unknown = 0", ("$c", category), ("$f", confidence), ("$id", id));

    public long Count()
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM events");
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(string sql, params (string, object?)[] ps)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, sql, ps);
        cmd.ExecuteNonQuery();
    }

    private static List<EventRow> ReadAll(SqliteCommand cmd)
    {
        var list = new List<EventRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static EventRow Read(SqliteDataReader r) => new(
        r.GetInt64(0),
        Enum.Parse<EventSource>(r.GetString(1), ignoreCase: true),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        Db.FromMs(r.GetInt64(5)),
        r.IsDBNull(6) ? null : Db.FromMs(r.GetInt64(6)),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetDouble(8),
        r.GetInt64(9) == 1,
        r.IsDBNull(10) ? null : r.GetString(10));
}

// ---- personal rules ------------------------------------------------------------------------------------------

public sealed record PersonalRule(long Id, string MatchKind, string Pattern, string Category, DateTimeOffset CreatedAt);

public sealed class RuleRepository(DlaDatabase db, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>Adds a rule, or changes the category if the same (kind, pattern) already exists.</summary>
    public void Upsert(string matchKind, string pattern, string category)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, """
            INSERT INTO personal_rules (match_kind, pattern, category, created_at) VALUES ($k, $p, $c, $t)
            ON CONFLICT (match_kind, pattern) DO UPDATE SET category = excluded.category
            """, ("$k", matchKind), ("$p", pattern), ("$c", category), ("$t", Db.ToMs(_now())));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PersonalRule> List()
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "SELECT id, match_kind, pattern, category, created_at FROM personal_rules ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<PersonalRule>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Db.FromMs(r.GetInt64(4))));
        return list;
    }

    public void Delete(long id)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "DELETE FROM personal_rules WHERE id = $id", ("$id", id));
        cmd.ExecuteNonQuery();
    }
}

// ---- settings, exclusions, consent record --------------------------------------------------------------------

public sealed record ConsentEntry(ConsentState State, int TextVersion, DateTimeOffset At);

public sealed class SettingsRepository(DlaDatabase db, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public string? Get(string name)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "SELECT value FROM settings WHERE name = $n", ("$n", name));
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string name, string value)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, """
            INSERT INTO settings (name, value, updated_at) VALUES ($n, $v, $t)
            ON CONFLICT (name) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """, ("$n", name), ("$v", value), ("$t", Db.ToMs(_now())));
        cmd.ExecuteNonQuery();
    }

    /// <summary>How many days raw events are kept locally. Default 30; the user may change it (1 to 3650).</summary>
    public int RetentionDays
    {
        get => int.Parse(Get("retention_days") ?? "30");
        set
        {
            if (value is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(value), "Retention must be 1 to 3650 days.");
            Set("retention_days", value.ToString());
        }
    }

    /// <summary>No input for this long means the user is away. Default 3 minutes.</summary>
    public TimeSpan IdleThreshold
    {
        get => TimeSpan.FromSeconds(int.Parse(Get("idle_threshold_seconds") ?? "180"));
        set
        {
            if (value < TimeSpan.FromSeconds(30) || value > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(value), "Idle threshold must be 30 seconds to 1 hour.");
            Set("idle_threshold_seconds", ((int)value.TotalSeconds).ToString());
        }
    }

    public bool Paused
    {
        get => Get("paused") == "1";
        set => Set("paused", value ? "1" : "0");
    }

    // exclusions: apps or sites the user does not want recorded
    public void AddExclusion(string kind, string pattern)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "INSERT OR IGNORE INTO exclusions (kind, pattern, created_at) VALUES ($k, $p, $t)",
            ("$k", kind), ("$p", pattern), ("$t", Db.ToMs(_now())));
        cmd.ExecuteNonQuery();
    }

    public void RemoveExclusion(string kind, string pattern)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "DELETE FROM exclusions WHERE kind = $k AND pattern = $p", ("$k", kind), ("$p", pattern));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Kind, string Pattern)> Exclusions()
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "SELECT kind, pattern FROM exclusions ORDER BY kind, pattern");
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    // consent record: append-only history, newest row is current
    public void AppendConsent(ConsentState state, int textVersion)
    {
        if (state == ConsentState.None) throw new ArgumentException("'None' is not stored; it means no row exists.", nameof(state));
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "INSERT INTO consent_records (state, text_version, at) VALUES ($s, $v, $t)",
            ("$s", state.ToString()), ("$v", textVersion), ("$t", Db.ToMs(_now())));
        cmd.ExecuteNonQuery();
    }

    public ConsentEntry? LatestConsent()
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "SELECT state, text_version, at FROM consent_records ORDER BY id DESC LIMIT 1");
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ConsentEntry(Enum.Parse<ConsentState>(r.GetString(0)), r.GetInt32(1), Db.FromMs(r.GetInt64(2))) : null;
    }
}

// ---- OCR audit log -------------------------------------------------------------------------------------------

public sealed record OcrAuditEntry(
    DateTimeOffset CapturedAt, string App, string? WindowTitle, string TriggerReason,
    int CharsRead, string? Label, double? Confidence, int CaptureDestroyedAfterMs);

public sealed class OcrAuditRepository(DlaDatabase db)
{
    public void Add(OcrAuditEntry e)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, """
            INSERT INTO ocr_audit_log (captured_at, app, window_title, trigger_reason, chars_read, label, confidence, capture_destroyed_after_ms)
            VALUES ($t, $app, $title, $why, $n, $label, $conf, $ms)
            """, ("$t", Db.ToMs(e.CapturedAt)), ("$app", e.App), ("$title", e.WindowTitle), ("$why", e.TriggerReason),
            ("$n", e.CharsRead), ("$label", e.Label), ("$conf", e.Confidence), ("$ms", e.CaptureDestroyedAfterMs));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<OcrAuditEntry> List(int limit = 200)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, """
            SELECT captured_at, app, window_title, trigger_reason, chars_read, label, confidence, capture_destroyed_after_ms
            FROM ocr_audit_log ORDER BY captured_at DESC, id DESC LIMIT $n
            """, ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<OcrAuditEntry>();
        while (r.Read())
            list.Add(new(Db.FromMs(r.GetInt64(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.GetInt32(7)));
        return list;
    }
}

// ---- upload queue (encrypted summaries) ----------------------------------------------------------------------

public sealed record QueuedUpload(long Id, byte[] Payload, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, int Attempts);

public sealed class UploadQueueRepository(DlaDatabase db, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>Queues an already-encrypted summary. Returns false (and adds nothing) if the same payload is already queued.</summary>
    public bool Enqueue(byte[] encryptedPayload, DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
        var hash = Convert.ToHexString(SHA256.HashData(encryptedPayload));
        using var c = db.Open();
        using var cmd = Db.Cmd(c, """
            INSERT OR IGNORE INTO upload_queue (payload, payload_hash, period_start, period_end, created_at)
            VALUES ($p, $h, $s, $e, $t)
            """, ("$p", encryptedPayload), ("$h", hash), ("$s", Db.ToMs(periodStart)), ("$e", Db.ToMs(periodEnd)), ("$t", Db.ToMs(_now())));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Unsent items, oldest first.</summary>
    public IReadOnlyList<QueuedUpload> Pending(int limit = 50)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c,
            "SELECT id, payload, period_start, period_end, attempts FROM upload_queue WHERE sent_at IS NULL ORDER BY created_at, id LIMIT $n",
            ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<QueuedUpload>();
        while (r.Read())
            list.Add(new(r.GetInt64(0), (byte[])r["payload"], Db.FromMs(r.GetInt64(2)), Db.FromMs(r.GetInt64(3)), r.GetInt32(4)));
        return list;
    }

    public void MarkSent(long id) => Exec("UPDATE upload_queue SET sent_at = $t, last_attempt_at = $t WHERE id = $id", id);

    public void MarkFailed(long id) => Exec("UPDATE upload_queue SET attempts = attempts + 1, last_attempt_at = $t WHERE id = $id", id);

    private void Exec(string sql, long id)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, sql, ("$t", Db.ToMs(_now())), ("$id", id));
        cmd.ExecuteNonQuery();
    }
}

// ---- lifecycle markers ---------------------------------------------------------------------------------------

public sealed record MarkerRow(DateTimeOffset At, string Kind, string SessionId, string? Detail);

public sealed class LifecycleRepository(DlaDatabase db)
{
    public void Add(DateTimeOffset at, string kind, string sessionId, string? detail = null)
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "INSERT INTO lifecycle_markers (at, kind, session_id, detail) VALUES ($t, $k, $s, $d)",
            ("$t", Db.ToMs(at)), ("$k", kind), ("$s", sessionId), ("$d", detail));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<MarkerRow> List()
    {
        using var c = db.Open();
        using var cmd = Db.Cmd(c, "SELECT at, kind, session_id, detail FROM lifecycle_markers ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<MarkerRow>();
        while (r.Read()) list.Add(new(Db.FromMs(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }
}

// ---- retention purge -----------------------------------------------------------------------------------------

public sealed record PurgeResult(int Events, int OcrAudit, int Markers, int SentUploads)
{
    public int Total => Events + OcrAudit + Markers + SentUploads;
}

/// <summary>
/// Deletes local data older than the retention setting (default 30 days). Raw events, the OCR audit log, lifecycle
/// markers and already-sent queue rows age out together. Unsent upload items are never purged: they are encrypted
/// summaries still waiting for a connection.
/// </summary>
public sealed class RetentionPurger(DlaDatabase db, SettingsRepository settings, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public PurgeResult Purge() => PurgeOlderThan(_now() - TimeSpan.FromDays(settings.RetentionDays));

    /// <summary>An event is purged when it ended (or, if still open, started) before the cutoff.</summary>
    public PurgeResult PurgeOlderThan(DateTimeOffset cutoff)
    {
        var ms = Db.ToMs(cutoff);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        int Delete(string sql)
        {
            using var cmd = Db.Cmd(c, sql, ("$c", ms));
            cmd.Transaction = tx;
            return cmd.ExecuteNonQuery();
        }

        var result = new PurgeResult(
            Delete("DELETE FROM events WHERE COALESCE(end_ts, start_ts) < $c"),
            Delete("DELETE FROM ocr_audit_log WHERE captured_at < $c"),
            Delete("DELETE FROM lifecycle_markers WHERE at < $c"),
            Delete("DELETE FROM upload_queue WHERE sent_at IS NOT NULL AND sent_at < $c"));
        tx.Commit();

        MigrationRunner.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)"); // fold deleted pages out of the -wal file
        return result;
    }
}
