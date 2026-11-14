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
        // v5: external calendar sync (accounts, per-event links) and the AI (MCP) approval queue and audit log
        """
        CREATE TABLE sync_accounts(
            id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, url TEXT NOT NULL, username TEXT NOT NULL,
            secret TEXT NOT NULL, two_way INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 1,
            last_sync_at INTEGER, last_result TEXT);

        CREATE TABLE sync_links(
            account_id TEXT NOT NULL, event_id TEXT NOT NULL, href TEXT NOT NULL, uid TEXT NOT NULL, etag TEXT,
            local_stamp INTEGER NOT NULL, protected INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(account_id, event_id));
        CREATE UNIQUE INDEX ix_sync_links_href ON sync_links(account_id, href);

        CREATE TABLE mcp_pending(
            id TEXT PRIMARY KEY, created_at INTEGER NOT NULL, tool TEXT NOT NULL, args TEXT NOT NULL,
            summary TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, decided_at INTEGER, result TEXT);
        CREATE INDEX ix_mcp_pending_status ON mcp_pending(status, created_at);

        CREATE TABLE mcp_audit(
            id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, tool TEXT NOT NULL,
            args TEXT NOT NULL, outcome TEXT NOT NULL);
        """,
        // v6: day-cell item options - repeating items (one real item per occurrence, tied by series_id) and the D-day an item counts down to
        """
        ALTER TABLE items ADD COLUMN series_id TEXT;
        ALTER TABLE items ADD COLUMN countdown_id TEXT;
        CREATE INDEX ix_items_series ON items(series_id, day) WHERE series_id IS NOT NULL;

        CREATE TABLE item_series(
            id TEXT PRIMARY KEY, rrule TEXT, ext_rule TEXT, start_day TEXT NOT NULL,
            remind TEXT NOT NULL DEFAULT '', generated_until TEXT);
        """,
        // v7: what happened to to-do items and when (created, done, undone, moved, deleted, restored) - the basis for reviews such as "what did I put off"
        """
        CREATE TABLE item_activity(
            id INTEGER PRIMARY KEY AUTOINCREMENT, item_id TEXT NOT NULL, kind TEXT NOT NULL, at INTEGER NOT NULL,
            day TEXT, to_day TEXT);
        CREATE INDEX ix_item_activity_item ON item_activity(item_id, at);
        CREATE INDEX ix_item_activity_at ON item_activity(at);
        """,
        // v8: the meaning-search index - one vector per to-do or event (derived from its text; a plain copy of the data's meaning, so it is as readable as the database itself)
        """
        CREATE TABLE embeddings(
            entity TEXT NOT NULL, entity_id TEXT NOT NULL, model TEXT NOT NULL, dim INTEGER NOT NULL,
            vec BLOB NOT NULL, content_hash TEXT NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY(entity, entity_id));
        """,
    ];
}
