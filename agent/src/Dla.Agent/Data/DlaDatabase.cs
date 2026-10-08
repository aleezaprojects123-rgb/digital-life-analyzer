using Microsoft.Data.Sqlite;

namespace Dla.Agent.Data;

/// <summary>
/// Opens the local SQLite database (default %LOCALAPPDATA%\DLA\dla.db) and runs migrations.
/// Each call to <see cref="Open"/> returns a short-lived connection with the safety pragmas applied.
/// </summary>
public sealed class DlaDatabase
{
    private readonly string _connectionString;

    public string Path { get; }

    private DlaDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // no lingering file handles; our write rate is tiny
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>Creates the file if needed and brings the schema up to date.</summary>
    public static DlaDatabase OpenAndMigrate(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var db = new DlaDatabase(path);
        using var conn = db.Open();
        MigrationRunner.Apply(conn);
        return db;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // WAL: a crash or power loss cannot corrupt the file. secure_delete: purged rows are overwritten,
        // so deleted activity does not linger in the file. busy_timeout: wait instead of failing on a brief lock.
        MigrationRunner.Exec(conn, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA secure_delete = ON; PRAGMA busy_timeout = 5000;");
        return conn;
    }

    public static string DefaultPath => System.IO.Path.Combine(Core.AppPaths.DataDir, "dla.db");
}

internal static class Db
{
    public static long ToMs(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public static object OrNull(object? v) => v ?? DBNull.Value;

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, OrNull(v));
        return cmd;
    }
}
