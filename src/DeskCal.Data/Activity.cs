using Microsoft.Data.Sqlite;
using DeskCal.Core;

namespace DeskCal.Data;

public static class ActivityKind
{
    public const string Created = "created", Done = "done", Undone = "undone", Moved = "moved", Deleted = "deleted", Restored = "restored";
}

/// <summary>One thing that happened to a to-do item. <see cref="Day"/> is the day it was on, <see cref="ToDay"/> the day it was moved to.</summary>
public sealed record ActivityEntry(long Id, string ItemId, string Kind, long At, string? Day, string? ToDay);

internal static class ActivityLog
{
    /// <summary>Keep the table from growing for ever: past this many rows the oldest tenth is dropped.</summary>
    internal const int MaxRows = 100_000;

    internal static void Record(SqliteConnection c, SqliteTransaction? tx, string itemId, string kind, long at, string? day, string? toDay = null)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO item_activity(item_id, kind, at, day, to_day) VALUES($i,$k,$a,$d,$t)";
            cmd.Parameters.AddWithValue("$i", itemId); cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$a", at);
            cmd.Parameters.AddWithValue("$d", (object?)day ?? DBNull.Value); cmd.Parameters.AddWithValue("$t", (object?)toDay ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        using var trim = c.CreateCommand();
        trim.Transaction = tx;
        trim.CommandText = $"DELETE FROM item_activity WHERE (SELECT COUNT(*) FROM item_activity) > {MaxRows} AND id <= (SELECT MAX(id) FROM item_activity) - {MaxRows - MaxRows / 10}";
        trim.ExecuteNonQuery();
    }
}

/// <summary>Reads the activity log. The log only starts when this feature was added: <see cref="Since"/> says when.</summary>
public sealed class ActivityRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    public IReadOnlyList<ActivityEntry> List(long fromMs, long toMs, string? kind = null, int limit = 1000)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c,
            "SELECT id, item_id, kind, at, day, to_day FROM item_activity WHERE at >= $f AND at < $t AND ($k IS NULL OR kind = $k) ORDER BY at, id LIMIT $l",
            null, ("$f", fromMs), ("$t", toMs), ("$k", kind), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ActivityEntry>();
        while (r.Read()) list.Add(new ActivityEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Str(r, 4), Str(r, 5)));
        return list;
    }

    /// <summary>Counts per kind in [fromMs, toMs).</summary>
    public IReadOnlyDictionary<string, int> Counts(long fromMs, long toMs)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT kind, COUNT(*) FROM item_activity WHERE at >= $f AND at < $t GROUP BY kind", null, ("$f", fromMs), ("$t", toMs));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>When the first activity was recorded, or null if nothing has been recorded yet.</summary>
    public long? Since()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT MIN(at) FROM item_activity");
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }
}
