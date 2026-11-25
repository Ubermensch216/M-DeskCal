using DeskCal.Core;
using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

public abstract class RepoBase(DeskCalDb db, IClock clock)
{
    protected DeskCalDb Db { get; } = db;
    protected IClock Clock { get; } = clock;

    protected static string NewId() => Guid.NewGuid().ToString("N");

    protected static SqliteCommand Cmd(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in p) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    protected static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Runs the query and maps every row.</summary>
    protected static List<T> ReadAll<T>(SqliteCommand cmd, Func<SqliteDataReader, T> map)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}

// ---------------------------------------------------------------------------------------------
public sealed class HistoryRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    /// <summary>Record previous content inside the caller's transaction.</summary>
    internal static void Record(SqliteConnection c, SqliteTransaction tx, string entity, string id, string content, long at)
    {
        using var cmd = Cmd(c, "INSERT INTO history(entity, entity_id, content, at) VALUES($e,$i,$c,$a)", tx,
            ("$e", entity), ("$i", id), ("$c", content), ("$a", at));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> List(string entity, string entityId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT id, entity, entity_id, content, at FROM history WHERE entity=$e AND entity_id=$i ORDER BY at DESC, id DESC",
            null, ("$e", entity), ("$i", entityId));
        return ReadAll(cmd, r => new HistoryEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
    }

    public HistoryEntry? Get(long historyId)
    {
        using var c = Db.Open();
        return Get(c, null, historyId);
    }

    internal static HistoryEntry? Get(SqliteConnection c, SqliteTransaction? tx, long historyId)
    {
        using var cmd = Cmd(c, "SELECT id, entity, entity_id, content, at FROM history WHERE id=$i", tx, ("$i", historyId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new HistoryEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)) : null;
    }

    /// <summary>Permanently remove history of one entity (UI: "delete historical data").</summary>
    public int Delete(string entity, string entityId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM history WHERE entity=$e AND entity_id=$i", null, ("$e", entity), ("$i", entityId));
        return cmd.ExecuteNonQuery();
    }
}

// ---------------------------------------------------------------------------------------------
public sealed class ItemRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    internal const string Cols = "id, day, content, bg_color, sort, done, created_at, updated_at, deleted, text_color, series_id, countdown_id";

    internal static Item Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetInt32(4), r.GetInt32(5) != 0,
        r.GetInt64(6), r.GetInt64(7), r.GetInt32(8) != 0, Str(r, 9), Str(r, 10), Str(r, 11));

    public Item Add(string day, string content, string? bgColor = null)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using var maxCmd = Cmd(c, "SELECT COALESCE(MAX(sort), -1) + 1 FROM items WHERE day=$d AND deleted=0", tx, ("$d", day));
        int sort = Convert.ToInt32(maxCmd.ExecuteScalar());
        var item = Insert(c, tx, day, content, sort, bgColor, null);
        tx.Commit();
        return item;
    }

    private Item Insert(SqliteConnection c, SqliteTransaction tx, string day, string content, int sort, string? bg, string? text)
    {
        long now = Clock.NowMs;
        var item = new Item(NewId(), day, content, bg, sort, false, now, now, false, text);
        using var cmd = Cmd(c, $"INSERT INTO items({Cols}) VALUES($id,$day,$c,$bg,$s,0,$ca,$ua,0,$tc,NULL,NULL)", tx,
            ("$id", item.Id), ("$day", day), ("$c", content), ("$bg", bg), ("$s", sort), ("$ca", now), ("$ua", now), ("$tc", text));
        cmd.ExecuteNonQuery();
        ActivityLog.Record(c, tx, item.Id, ActivityKind.Created, now, day);
        return item;
    }

    /// <summary>
    /// Replace the whole day's list with the given lines (blank lines are dropped, trailing spaces trimmed).
    /// Untouched lines keep their item (id, done flag, colours); edited lines go through history;
    /// removed lines are soft-deleted. See <see cref="LineReconciler"/>.
    /// </summary>
    public IReadOnlyList<Item> ReplaceDay(string day, IEnumerable<string> lines)
    {
        var clean = lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        using var c = Db.Open();
        using var tx = c.BeginTransaction();

        var existing = new List<Item>();
        using (var sel = Cmd(c, $"SELECT {Cols} FROM items WHERE day=$d AND deleted=0 ORDER BY sort, created_at", tx, ("$d", day)))
        using (var r = sel.ExecuteReader())
            while (r.Read()) existing.Add(Map(r));

        foreach (var op in LineReconciler.Reconcile(existing.Select(i => i.Content).ToList(), clean))
        {
            switch (op.Kind)
            {
                case LineOpKind.Keep:
                    SetSort(c, tx, existing[op.OldIndex], op.NewIndex);
                    break;
                case LineOpKind.Update:
                    UpdateContent(c, tx, existing[op.OldIndex].Id, clean[op.NewIndex]);
                    SetSort(c, tx, existing[op.OldIndex], op.NewIndex);
                    if (existing[op.OldIndex].CountdownId is { } linked)      // the D-day carries the item's text
                    {
                        using var title = Cmd(c, "UPDATE countdowns SET title=$t WHERE id=$i", tx, ("$t", clean[op.NewIndex]), ("$i", linked));
                        title.ExecuteNonQuery();
                    }
                    break;
                case LineOpKind.Add:
                    Insert(c, tx, day, clean[op.NewIndex], op.NewIndex, null, null);
                    break;
                case LineOpKind.Delete:
                    using (var del = Cmd(c, "UPDATE items SET deleted=1, updated_at=$u WHERE id=$i", tx,
                               ("$u", Clock.NowMs), ("$i", existing[op.OldIndex].Id)))
                        del.ExecuteNonQuery();
                    ActivityLog.Record(c, tx, existing[op.OldIndex].Id, ActivityKind.Deleted, Clock.NowMs, existing[op.OldIndex].Day);
                    DropCountdown(c, tx, existing[op.OldIndex].CountdownId);
                    break;
            }
        }
        tx.Commit();
        return GetByDay(day);
    }

    private static void SetSort(SqliteConnection c, SqliteTransaction tx, Item item, int sort)
    {
        if (item.Sort == sort) return;
        using var cmd = Cmd(c, "UPDATE items SET sort=$s WHERE id=$i", tx, ("$s", sort), ("$i", item.Id));
        cmd.ExecuteNonQuery();
    }

    public Item? Get(string id)
    {
        using var c = Db.Open();
        return Get(c, null, id);
    }

    private static Item? Get(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var cmd = Cmd(c, $"SELECT {Cols} FROM items WHERE id=$i", tx, ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public IReadOnlyList<Item> GetByDay(string day) => GetRange(day, day);

    /// <summary>Non-deleted items for days in [fromDay, toDay] (inclusive, "yyyy-MM-dd").</summary>
    public IReadOnlyList<Item> GetRange(string fromDay, string toDay)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM items WHERE day BETWEEN $a AND $b AND deleted=0 ORDER BY day, sort, created_at",
            null, ("$a", fromDay), ("$b", toDay));
        return ReadAll(cmd, Map);
    }

    /// <summary>Items for the search: optionally limited to days in [from, to], optionally including soft-deleted ones.</summary>
    public IReadOnlyList<Item> GetForSearch(string? fromDay, string? toDay, bool includeDeleted)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c,
            $"SELECT {Cols} FROM items WHERE ($a IS NULL OR day >= $a) AND ($b IS NULL OR day <= $b) AND ($d = 1 OR deleted = 0)",
            null, ("$a", fromDay), ("$b", toDay), ("$d", includeDeleted ? 1 : 0));
        return ReadAll(cmd, Map);
    }

    /// <summary>Change content; the previous content is saved to history when it differs.</summary>
    public Item? UpdateContent(string id, string content)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var item = UpdateContent(c, tx, id, content);
        tx.Commit();
        return item;
    }

    private Item? UpdateContent(SqliteConnection c, SqliteTransaction tx, string id, string content)
    {
        var old = Get(c, tx, id);
        if (old is null) return null;
        if (old.Content == content) return old;
        long now = Clock.NowMs;
        HistoryRepository.Record(c, tx, EntityNames.Item, id, old.Content, now);
        using var cmd = Cmd(c, "UPDATE items SET content=$c, updated_at=$u WHERE id=$i", tx, ("$c", content), ("$u", now), ("$i", id));
        cmd.ExecuteNonQuery();
        return old with { Content = content, UpdatedAt = now };
    }

    /// <summary>Restore an item's content from a history record (current content becomes a new history record).</summary>
    public Item? RestoreFromHistory(long historyId)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var h = HistoryRepository.Get(c, tx, historyId);
        if (h is null || h.Entity != EntityNames.Item) return null;
        var item = UpdateContent(c, tx, h.EntityId, h.Content);
        tx.Commit();
        return item;
    }

    public bool SetDone(string id, bool done)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var before = Get(c, tx, id);
        using var cmd = Cmd(c, "UPDATE items SET done=$v, updated_at=$u WHERE id=$i AND deleted=0", tx, ("$v", done ? 1 : 0), ("$u", Clock.NowMs), ("$i", id));
        bool changed = cmd.ExecuteNonQuery() > 0;
        if (changed && before is { Deleted: false } && before.Done != done)
            ActivityLog.Record(c, tx, id, done ? ActivityKind.Done : ActivityKind.Undone, Clock.NowMs, before.Day);      // ticking an already ticked item records nothing
        tx.Commit();
        return changed;
    }
    public bool SetColor(string id, string? color) => Exec("UPDATE items SET bg_color=$v, updated_at=$u WHERE id=$i AND deleted=0", id, color);
    public bool SetTextColor(string id, string? color) => Exec("UPDATE items SET text_color=$v, updated_at=$u WHERE id=$i AND deleted=0", id, color);
    public bool Move(string id, string newDay)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var before = Get(c, tx, id);
        using (var move = Cmd(c, "UPDATE items SET day=$v, updated_at=$u WHERE id=$i AND deleted=0", tx, ("$v", newDay), ("$u", Clock.NowMs), ("$i", id)))
            if (move.ExecuteNonQuery() == 0) return false;
        using (var cmd = Cmd(c, "UPDATE countdowns SET date=$d WHERE id=(SELECT countdown_id FROM items WHERE id=$i)", tx, ("$d", newDay), ("$i", id)))
            cmd.ExecuteNonQuery();      // the linked D-day follows the item to its new day
        if (before is not null && before.Day != newDay) ActivityLog.Record(c, tx, id, ActivityKind.Moved, Clock.NowMs, before.Day, newDay);
        tx.Commit();
        return true;
    }
    public bool SetCountdown(string id, string? countdownId) => Exec("UPDATE items SET countdown_id=$v, updated_at=$u WHERE id=$i AND deleted=0", id, countdownId);

    public bool SoftDelete(string id)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var item = Get(c, tx, id);
        using var cmd = Cmd(c, "UPDATE items SET deleted=1, updated_at=$u WHERE id=$i", tx, ("$u", Clock.NowMs), ("$i", id));
        bool changed = cmd.ExecuteNonQuery() > 0;
        if (changed) DropCountdown(c, tx, item?.CountdownId);
        if (changed && item is { Deleted: false }) ActivityLog.Record(c, tx, id, ActivityKind.Deleted, Clock.NowMs, item.Day);
        tx.Commit();
        return changed;
    }

    public bool Undelete(string id)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var before = Get(c, tx, id);
        using var cmd = Cmd(c, "UPDATE items SET deleted=0, updated_at=$u WHERE id=$i", tx, ("$u", Clock.NowMs), ("$i", id));
        bool changed = cmd.ExecuteNonQuery() > 0;
        if (changed && before is { Deleted: true }) ActivityLog.Record(c, tx, id, ActivityKind.Restored, Clock.NowMs, before.Day);
        tx.Commit();
        return changed;
    }

    /// <summary>A deleted item takes the D-day it was counting down to with it (the link is left on the item, harmlessly).</summary>
    private static void DropCountdown(SqliteConnection c, SqliteTransaction tx, string? countdownId)
    {
        if (countdownId is null) return;
        using var cmd = Cmd(c, "DELETE FROM countdowns WHERE id=$i", tx, ("$i", countdownId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The items with these ids (deleted ones included), in one query.</summary>
    public IReadOnlyDictionary<string, Item> GetMany(IEnumerable<string> ids)
    {
        var wanted = ids.Distinct().ToList();
        var map = new Dictionary<string, Item>();
        if (wanted.Count == 0) return map;
        using var c = Db.Open();
        foreach (var chunk in wanted.Chunk(500))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM items WHERE id IN ({string.Join(",", chunk.Select((_, i) => "$p" + i))})";
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var item = Map(r); map[item.Id] = item; }
        }
        return map;
    }

    private bool Exec(string sql, string id, object? value)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, sql, null, ("$v", value), ("$u", Clock.NowMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }
}

// ---------------------------------------------------------------------------------------------
public sealed class EventRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    private const string Cols = "id, title, body, start, \"end\", all_day, tz, rrule, ext_rule, status, color, created_at, updated_at, deleted, countdown_id";

    private static EventEntry Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetInt32(5) != 0, Str(r, 6),
        Str(r, 7), Str(r, 8), r.GetInt32(9), Str(r, 10), r.GetInt64(11), r.GetInt64(12), r.GetInt32(13) != 0, Str(r, 14));

    public EventEntry Add(string title, string start, string? end = null, bool allDay = false, string? tz = null,
                          string? rrule = null, string? extRule = null, string body = "", string? color = null,
                          int status = 0, string? countdownId = null)
    {
        long now = Clock.NowMs;
        var e = new EventEntry(NewId(), title, body, start, end, allDay, tz, rrule, extRule, status, color, now, now, false, countdownId);
        using var c = Db.Open();
        using var cmd = Cmd(c, $"INSERT INTO events({Cols}) VALUES($id,$t,$b,$s,$e,$ad,$tz,$rr,$ex,$st,$col,$ca,$ua,0,$cid)", null,
            ("$id", e.Id), ("$t", title), ("$b", body), ("$s", start), ("$e", end), ("$ad", allDay ? 1 : 0), ("$tz", tz),
            ("$rr", rrule), ("$ex", extRule), ("$st", status), ("$col", color), ("$ca", now), ("$ua", now), ("$cid", countdownId));
        cmd.ExecuteNonQuery();
        return e;
    }

    public EventEntry? Get(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM events WHERE id=$i", null, ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>Replace all editable fields. The previous body is saved to history when it changed.</summary>
    public bool Update(EventEntry e)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using var sel = Cmd(c, "SELECT body FROM events WHERE id=$i", tx, ("$i", e.Id));
        var oldBody = sel.ExecuteScalar() as string;
        if (oldBody is null) return false;
        long now = Clock.NowMs;
        if (oldBody != e.Body) HistoryRepository.Record(c, tx, EntityNames.Event, e.Id, oldBody, now);
        using var cmd = Cmd(c,
            "UPDATE events SET title=$t, body=$b, start=$s, \"end\"=$e, all_day=$ad, tz=$tz, rrule=$rr, ext_rule=$ex, status=$st, color=$col, countdown_id=$cid, updated_at=$u WHERE id=$i",
            tx, ("$t", e.Title), ("$b", e.Body), ("$s", e.Start), ("$e", e.End), ("$ad", e.AllDay ? 1 : 0), ("$tz", e.Tz),
            ("$rr", e.RRule), ("$ex", e.ExtRule), ("$st", e.Status), ("$col", e.Color), ("$cid", e.CountdownId), ("$u", now), ("$i", e.Id));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public bool SetDone(string id, bool done)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE events SET status=$st, updated_at=$u WHERE id=$i AND deleted=0", null,
            ("$st", done ? 1 : 0), ("$u", Clock.NowMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetCountdown(string id, string? countdownId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE events SET countdown_id=$cid, updated_at=$u WHERE id=$i AND deleted=0", null,
            ("$cid", countdownId), ("$u", Clock.NowMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Every non-deleted event (the table stays small; recurrence is expanded in memory).</summary>
    public IReadOnlyList<EventEntry> GetAllActive()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM events WHERE deleted=0 ORDER BY start");
        return ReadAll(cmd, Map);
    }

    /// <summary>Remove one occurrence of a repeating event ("delete this one only").</summary>
    public void AddException(string eventId, DateOnly day)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "INSERT OR IGNORE INTO event_exceptions(event_id, day) VALUES($e,$d)", null,
            ("$e", eventId), ("$d", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        if (cmd.ExecuteNonQuery() > 0) Touch(c, eventId);
    }

    /// <summary>Skipping days changes what the event shows, so it counts as an edit (calendar sync looks at updated_at).</summary>
    private void Touch(SqliteConnection c, string eventId)
    {
        using var cmd = Cmd(c, "UPDATE events SET updated_at=$u WHERE id=$i", null, ("$u", Clock.NowMs), ("$i", eventId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Replace the skipped days of an event with exactly these.</summary>
    public void ReplaceExceptions(string eventId, IEnumerable<DateOnly> days)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using (var del = Cmd(c, "DELETE FROM event_exceptions WHERE event_id=$e", tx, ("$e", eventId))) del.ExecuteNonQuery();
        foreach (var d in days.Distinct())
        {
            using var ins = Cmd(c, "INSERT INTO event_exceptions(event_id, day) VALUES($e,$d)", tx,
                ("$e", eventId), ("$d", d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool Undelete(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE events SET deleted=0, updated_at=$u WHERE id=$i", null, ("$u", Clock.NowMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Skipped days per event id, for all events at once.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<DateOnly>> GetExceptions()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT event_id, day FROM event_exceptions");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, HashSet<DateOnly>>();
        while (r.Read())
            if (DateOnly.TryParseExact(r.GetString(1), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                (map.TryGetValue(r.GetString(0), out var set) ? set : map[r.GetString(0)] = []).Add(d);
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<DateOnly>)kv.Value);
    }

    public IReadOnlySet<DateOnly> GetExceptions(string eventId) =>
        GetExceptions().TryGetValue(eventId, out var s) ? s : new HashSet<DateOnly>();

    /// <summary>Forget skipped days on or after <paramref name="day"/> (they no longer matter once the series ends before it).</summary>
    public void RemoveExceptionsFrom(string eventId, DateOnly day)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM event_exceptions WHERE event_id=$e AND day >= $d", null,
            ("$e", eventId), ("$d", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        if (cmd.ExecuteNonQuery() > 0) Touch(c, eventId);
    }

    public bool SoftDelete(string id)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        string? countdownId;
        using (var sel = Cmd(c, "SELECT countdown_id FROM events WHERE id=$i", tx, ("$i", id)))
            countdownId = sel.ExecuteScalar() as string;
        using var cmd = Cmd(c, "UPDATE events SET deleted=1, updated_at=$u WHERE id=$i", tx, ("$u", Clock.NowMs), ("$i", id));
        bool changed = cmd.ExecuteNonQuery() > 0;
        if (changed && !string.IsNullOrEmpty(countdownId))
        {
            using var del = Cmd(c, "DELETE FROM countdowns WHERE id=$i", tx, ("$i", countdownId));
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return changed;
    }
}

// ---------------------------------------------------------------------------------------------
public sealed class ReminderRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    private static Reminder Map(SqliteDataReader r) => new(
        r.GetString(0), (ReminderOwner)r.GetInt32(1), r.GetString(2), r.GetInt32(3), Str(r, 4), Str(r, 5),
        r.IsDBNull(6) ? null : r.GetInt64(6));

    /// <summary>
    /// New reminders are "armed" at creation: FiredAt starts at <paramref name="armedAtMs"/> (default: now) and means
    /// "every trigger up to here has been handled", so past triggers of a newly created reminder never fire.
    /// </summary>
    public Reminder Add(ReminderOwner owner, string ownerId, int offsetMinutes, string? customTime = null, string? sound = null, long? armedAtMs = null)
    {
        var rem = new Reminder(NewId(), owner, ownerId, Math.Max(0, offsetMinutes), customTime, sound, armedAtMs ?? Clock.NowMs);
        using var c = Db.Open();
        using var cmd = Cmd(c, "INSERT INTO reminders(id, owner_type, owner_id, offset_minutes, custom_time, sound, fired_at) VALUES($id,$o,$oi,$m,$ct,$s,$f)",
            null, ("$id", rem.Id), ("$o", (int)owner), ("$oi", ownerId), ("$m", rem.OffsetMinutes), ("$ct", customTime), ("$s", sound), ("$f", rem.FiredAt));
        cmd.ExecuteNonQuery();
        return rem;
    }

    public IReadOnlyList<Reminder> ListAll()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT id, owner_type, owner_id, offset_minutes, custom_time, sound, fired_at FROM reminders ORDER BY owner_type, owner_id, offset_minutes DESC");
        return ReadAll(cmd, Map);
    }

    /// <summary>Replace an owner's reminders with the given ones (all armed now).</summary>
    public void ReplaceForOwner(ReminderOwner owner, string ownerId, IEnumerable<(int Offset, string? CustomTime, string? Sound)> specs)
    {
        DeleteByOwner(owner, ownerId);
        foreach (var (offset, custom, sound) in specs) Add(owner, ownerId, offset, custom, sound);
    }

    public IReadOnlyList<Reminder> GetByOwner(ReminderOwner owner, string ownerId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT id, owner_type, owner_id, offset_minutes, custom_time, sound, fired_at FROM reminders WHERE owner_type=$o AND owner_id=$i ORDER BY offset_minutes DESC",
            null, ("$o", (int)owner), ("$i", ownerId));
        return ReadAll(cmd, Map);
    }

    public bool MarkFired(string id, long firedAtMs)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE reminders SET fired_at=$f WHERE id=$i", null, ("$f", firedAtMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    public int DeleteByOwner(ReminderOwner owner, string ownerId)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM reminders WHERE owner_type=$o AND owner_id=$i", null, ("$o", (int)owner), ("$i", ownerId));
        return cmd.ExecuteNonQuery();
    }
}

// ---------------------------------------------------------------------------------------------
public sealed class CountdownRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    private static Countdown Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), (CountdownType)r.GetInt32(3),
        r.GetInt32(4) != 0, r.GetInt32(5) != 0, r.GetInt32(6) != 0);

    public Countdown Add(string title, string date, CountdownType type = CountdownType.Custom,
                         bool showCard = false, bool showBottom = false, bool lunar = false)
    {
        var cd = new Countdown(NewId(), title, date, type, showCard, showBottom, lunar);
        Upsert(cd);
        return cd;
    }

    public void Upsert(Countdown cd)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c,
            "INSERT INTO countdowns(id, title, date, type, show_card, show_bottom, lunar) VALUES($id,$t,$d,$ty,$sc,$sb,$l) " +
            "ON CONFLICT(id) DO UPDATE SET title=$t, date=$d, type=$ty, show_card=$sc, show_bottom=$sb, lunar=$l",
            null, ("$id", cd.Id), ("$t", cd.Title), ("$d", cd.Date), ("$ty", (int)cd.Type),
            ("$sc", cd.ShowCard ? 1 : 0), ("$sb", cd.ShowBottom ? 1 : 0), ("$l", cd.Lunar ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Countdown> List()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT id, title, date, type, show_card, show_bottom, lunar FROM countdowns ORDER BY date, title");
        return ReadAll(cmd, Map);
    }

    public bool Delete(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM countdowns WHERE id=$i", null, ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }
}

// ---------------------------------------------------------------------------------------------
public sealed class NoteRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    private const string Cols = "id, title, content, color, status, created_at, updated_at, deleted";

    private static Note Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7) != 0);

    public Note Add(string title, string content, string? color = null)
    {
        long now = Clock.NowMs;
        var n = new Note(NewId(), title, content, color, 0, now, now);
        using var c = Db.Open();
        using var cmd = Cmd(c, $"INSERT INTO notes({Cols}) VALUES($id,$t,$c,$col,0,$ca,$ua,0)", null,
            ("$id", n.Id), ("$t", title), ("$c", content), ("$col", color), ("$ca", now), ("$ua", now));
        cmd.ExecuteNonQuery();
        return n;
    }

    public Note? Get(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM notes WHERE id=$i", null, ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public IReadOnlyList<Note> List()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, $"SELECT {Cols} FROM notes WHERE deleted=0 ORDER BY updated_at DESC");
        return ReadAll(cmd, Map);
    }

    public Note? Update(string id, string title, string content)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using var sel = Cmd(c, $"SELECT {Cols} FROM notes WHERE id=$i", tx, ("$i", id));
        Note? old;
        using (var r = sel.ExecuteReader()) old = r.Read() ? Map(r) : null;
        if (old is null) return null;
        long now = Clock.NowMs;
        if (old.Content != content) HistoryRepository.Record(c, tx, EntityNames.Note, id, old.Content, now);
        using var cmd = Cmd(c, "UPDATE notes SET title=$t, content=$c, updated_at=$u WHERE id=$i", tx,
            ("$t", title), ("$c", content), ("$u", now), ("$i", id));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return old with { Title = title, Content = content, UpdatedAt = now };
    }

    public bool SoftDelete(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE notes SET deleted=1, updated_at=$u WHERE id=$i", null, ("$u", Clock.NowMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Undelete(string id)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE notes SET deleted=0, updated_at=$u WHERE id=$i", null, ("$u", Clock.NowMs), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetColor(string id, string? color)
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "UPDATE notes SET color=$col WHERE id=$i AND deleted=0", null, ("$col", color), ("$i", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Put an earlier text of the note back (the text being replaced becomes a new history record).</summary>
    public Note? RestoreFromHistory(long historyId)
    {
        var h = new HistoryRepository(Db, Clock).Get(historyId) is { Entity: EntityNames.Note } e ? e : null;
        if (h is null) return null;
        var current = Get(h.EntityId);
        return current is null || current.Deleted ? null : Update(h.EntityId, current.Title, h.Content);
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>The class timetable: the period layout is stored as "timetable.*" settings, the filled squares in their own table.</summary>
public sealed class TimetableRepository(DeskCalDb db, IClock clock) : RepoBase(db, clock)
{
    private const string P = "timetable.";

    public TimetableConfig LoadConfig(SettingsStore s)
    {
        var d = new TimetableConfig();
        return new TimetableConfig
        {
            Periods = s.GetInt(P + "periods", d.Periods),
            FirstStart = TimeOnly.TryParseExact(s.GetString(P + "first_start"), ["HH:mm", "H:mm"], null, System.Globalization.DateTimeStyles.None, out var t) ? t : d.FirstStart,
            ClassMinutes = s.GetInt(P + "class_min", d.ClassMinutes),
            BreakMinutes = s.GetInt(P + "break_min", d.BreakMinutes),
            LunchAfter = s.GetInt(P + "lunch_after", d.LunchAfter),
            LunchMinutes = s.GetInt(P + "lunch_min", d.LunchMinutes),
            ShowWeekend = s.GetBool(P + "weekend", d.ShowWeekend),
        }.Sanitized();
    }

    public void SaveConfig(SettingsStore s, TimetableConfig config)
    {
        var c = config.Sanitized();
        s.SetInt(P + "periods", c.Periods);
        s.SetString(P + "first_start", c.FirstStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        s.SetInt(P + "class_min", c.ClassMinutes);
        s.SetInt(P + "break_min", c.BreakMinutes);
        s.SetInt(P + "lunch_after", c.LunchAfter);
        s.SetInt(P + "lunch_min", c.LunchMinutes);
        s.SetBool(P + "weekend", c.ShowWeekend);
    }

    public IReadOnlyList<TimetableCell> GetCells()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "SELECT dow, period, subject, room, color FROM timetable_cells ORDER BY period, dow");
        return ReadAll(cmd, r => new TimetableCell((DayOfWeek)r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Str(r, 4)));
    }

    /// <summary>Fill or change one square; an empty subject and room empties it.</summary>
    public void SetCell(DayOfWeek day, int period, string subject, string room = "", string? color = null)
    {
        subject = subject.Trim(); room = room.Trim();
        using var c = Db.Open();
        if (subject.Length == 0 && room.Length == 0)
        {
            using var del = Cmd(c, "DELETE FROM timetable_cells WHERE dow=$d AND period=$p", null, ("$d", (int)day), ("$p", period));
            del.ExecuteNonQuery();
            return;
        }
        using var cmd = Cmd(c,
            "INSERT INTO timetable_cells(dow, period, subject, room, color) VALUES($d,$p,$s,$r,$c) " +
            "ON CONFLICT(dow, period) DO UPDATE SET subject=$s, room=$r, color=$c", null,
            ("$d", (int)day), ("$p", period), ("$s", subject), ("$r", room), ("$c", color));
        cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var c = Db.Open();
        using var cmd = Cmd(c, "DELETE FROM timetable_cells");
        cmd.ExecuteNonQuery();
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>Key-value settings. Keys are namespaced like "board.max_rows".</summary>
public sealed class SettingsStore(DeskCalDb db)
{
    public string? GetString(string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetString(string key, string value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(GetString(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public void SetInt(string key, int value) => SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool GetBool(string key, bool fallback) => GetString(key) switch { "1" => true, "0" => false, _ => fallback };

    public void SetBool(string key, bool value) => SetString(key, value ? "1" : "0");

    public IReadOnlyDictionary<string, string> GetAll(string? prefix = null)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings WHERE $p IS NULL OR key LIKE $p || '%' ESCAPE '\\' ORDER BY key";
        cmd.Parameters.AddWithValue("$p", prefix is null ? DBNull.Value : prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, string>();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public bool Remove(string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>"Restore defaults": remove every key under a prefix, so readers fall back to defaults.</summary>
    public int ResetPrefix(string prefix)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key LIKE $p || '%' ESCAPE '\\'";
        cmd.Parameters.AddWithValue("$p", prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
        return cmd.ExecuteNonQuery();
    }
}
