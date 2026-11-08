using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

/// <summary>
/// Owns the SQLite file: opens connections, applies schema migrations (PRAGMA user_version)
/// and makes consistent backups.
/// </summary>
public sealed class DeskCalDb
{
    private readonly string _connectionString;

    public string Path { get; }

    public DeskCalDb(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // deterministic file release (tests, backup, delete)
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Create or upgrade the schema. Safe to call on every start.</summary>
    public void Migrate()
    {
        using var c = Open();
        int current = ScalarInt(c, "PRAGMA user_version");
        if (current > Migrations.Latest)
            throw new InvalidOperationException(
                $"Database schema v{current} is newer than this program supports (v{Migrations.Latest}).");

        for (int v = current + 1; v <= Migrations.Latest; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations.Steps[v - 1];
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA user_version = {v}";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int SchemaVersion()
    {
        using var c = Open();
        return ScalarInt(c, "PRAGMA user_version");
    }

    /// <summary>Write a consistent copy of the database (safe while the app is running).</summary>
    public void Backup(string destinationPath)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var src = Open();
        var dstCs = new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString();
        using var dst = new SqliteConnection(dstCs);
        dst.Open();
        src.BackupDatabase(dst);
    }

    internal static int ScalarInt(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

internal static class Migrations
{
    public static int Latest => Steps.Length;

    /// <summary>Append new steps at the end; never edit a released step.</summary>
    public static readonly string[] Steps =
    [
        // v1: initial schema
        """
        CREATE TABLE items(
            id TEXT PRIMARY KEY, day TEXT NOT NULL, content TEXT NOT NULL DEFAULT '',
            bg_color TEXT, sort INTEGER NOT NULL DEFAULT 0, done INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_items_day ON items(day, sort) WHERE deleted = 0;

        CREATE TABLE events(
            id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', body TEXT NOT NULL DEFAULT '',
            start TEXT NOT NULL, "end" TEXT, all_day INTEGER NOT NULL DEFAULT 0, tz TEXT,
            rrule TEXT, ext_rule TEXT, status INTEGER NOT NULL DEFAULT 0, color TEXT,
            created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_events_start ON events(start) WHERE deleted = 0;

        CREATE TABLE reminders(
            id TEXT PRIMARY KEY, owner_type INTEGER NOT NULL, owner_id TEXT NOT NULL,
            offset_minutes INTEGER NOT NULL DEFAULT 0, custom_time TEXT, sound TEXT, fired_at INTEGER);
        CREATE INDEX ix_reminders_owner ON reminders(owner_type, owner_id);

        CREATE TABLE countdowns(
            id TEXT PRIMARY KEY, title TEXT NOT NULL, date TEXT NOT NULL, type INTEGER NOT NULL DEFAULT 0,
            show_card INTEGER NOT NULL DEFAULT 0, show_bottom INTEGER NOT NULL DEFAULT 0, lunar INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE notes(
            id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', content TEXT NOT NULL DEFAULT '',
            color TEXT, status INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE history(
            id INTEGER PRIMARY KEY AUTOINCREMENT, entity TEXT NOT NULL, entity_id TEXT NOT NULL,
            content TEXT NOT NULL, at INTEGER NOT NULL);
        CREATE INDEX ix_history_entity ON history(entity, entity_id, at);

        CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
        // v2: per-item text colour
        "ALTER TABLE items ADD COLUMN text_color TEXT;",
        // v3: dates removed from a repeating event ("delete this occurrence only")
        """
        CREATE TABLE event_exceptions(
            event_id TEXT NOT NULL, day TEXT NOT NULL,
            PRIMARY KEY(event_id, day));
        """,
        // v4: timetable squares (the period layout lives in settings as "timetable.*")
        """
        CREATE TABLE timetable_cells(
            dow INTEGER NOT NULL, period INTEGER NOT NULL,
            subject TEXT NOT NULL, room TEXT NOT NULL DEFAULT '', color TEXT,
            PRIMARY KEY(dow, period));
        """,
    ];
}
