using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskCal.Core;
using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

/// <summary>The calendar as sync records: live ones, and the soft-deleted ones with the time they were deleted.</summary>
public sealed record LocalCalendar(IReadOnlyDictionary<string, SyncDoc> Docs, IReadOnlyDictionary<string, long> DeletedAt);

/// <summary>What <see cref="CalendarSnapshot.Apply"/> did: records written or removed, and the ones left alone because they changed meanwhile.</summary>
public sealed record SnapshotApplyResult(IReadOnlySet<string> Done, IReadOnlySet<string> Skipped, bool HolidaysChanged);

/// <summary>
/// Turns the whole calendar - to-dos (with reminders), events (with skipped days and reminders), D-days, notes, repeat rules of
/// to-dos, the timetable and the holiday corrections file - into one small JSON text per record, and writes such records back.
/// Records are keyed by kind and id ("items/&lt;id&gt;"); <see cref="Timetable"/> and <see cref="Holidays"/> are single records.
/// Window positions, display options, the AI and lock settings stay on each computer and are not part of it.
/// </summary>
public sealed class CalendarSnapshot(DeskCalDb db, IClock clock, string holidaysPath)
{
    public const string Items = "items", Events = "events", Countdowns = "countdowns", Notes = "notes", Series = "series";
    public const string Timetable = "timetable", Holidays = "holidays";
    public static readonly string[] Folders = [Items, Events, Countdowns, Notes, Series];

    private static readonly string[] TimetableKeys =
        ["timetable.periods", "timetable.first_start", "timetable.class_min", "timetable.break_min", "timetable.lunch_after", "timetable.lunch_min", "timetable.weekend"];

    // ---- reading this computer's calendar -----------------------------------------------------

    public LocalCalendar Collect()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var result = Collect(c, tx);
        tx.Commit();
        return result;
    }

    private LocalCalendar Collect(SqliteConnection c, SqliteTransaction tx)
    {
        var docs = new Dictionary<string, SyncDoc>(StringComparer.Ordinal);
        var gone = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(string key, string text, long stamp) => docs[key] = new SyncDoc(key, text, stamp);

        var reminders = new Dictionary<(int, string), List<RemindSpec>>();
        using (var cmd = Cmd(c, tx, "SELECT owner_type, owner_id, offset_minutes, custom_time, sound FROM reminders"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var k = (r.GetInt32(0), r.GetString(1));
                (reminders.TryGetValue(k, out var l) ? l : reminders[k] = []).Add(new RemindSpec(r.GetInt32(2), Str(r, 3), Str(r, 4)));
            }
        List<RemindSpec> RemindersOf(ReminderOwner o, string id) => reminders.TryGetValue(((int)o, id), out var l) ? l : [];

        using (var cmd = Cmd(c, tx, $"SELECT {ItemRepository.Cols} FROM items"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var i = ItemRepository.Map(r);
                string key = Items + "/" + i.Id;
                if (i.Deleted) { gone[key] = i.UpdatedAt; continue; }
                Add(key, Write(new SItem(i.Id, i.Day, i.Content, i.Done, i.Sort, i.BgColor, i.TextColor, i.SeriesId, i.CountdownId,
                    i.CreatedAt, i.UpdatedAt, RemindersOf(ReminderOwner.Item, i.Id))), i.UpdatedAt);
            }

        var skips = new Dictionary<string, List<string>>();
        using (var cmd = Cmd(c, tx, "SELECT event_id, day FROM event_exceptions"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) (skips.TryGetValue(r.GetString(0), out var l) ? l : skips[r.GetString(0)] = []).Add(r.GetString(1));

        using (var cmd = Cmd(c, tx, "SELECT id, title, body, start, \"end\", all_day, tz, rrule, ext_rule, status, color, created_at, updated_at, deleted FROM events"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string id = r.GetString(0), key = Events + "/" + id;
                if (r.GetInt32(13) != 0) { gone[key] = r.GetInt64(12); continue; }
                Add(key, Write(new SEvent(id, r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetInt32(5) != 0, Str(r, 6), Str(r, 7), Str(r, 8),
                    r.GetInt32(9), Str(r, 10), r.GetInt64(11), r.GetInt64(12), skips.TryGetValue(id, out var s) ? s : [], RemindersOf(ReminderOwner.Event, id))), r.GetInt64(12));
            }

        using (var cmd = Cmd(c, tx, "SELECT id, title, date, type, show_card, show_bottom, lunar FROM countdowns"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                Add(Countdowns + "/" + r.GetString(0), Write(new Countdown(r.GetString(0), r.GetString(1), r.GetString(2), (CountdownType)r.GetInt32(3),
                    r.GetInt32(4) != 0, r.GetInt32(5) != 0, r.GetInt32(6) != 0)), 0);

        using (var cmd = Cmd(c, tx, "SELECT id, title, content, color, status, created_at, updated_at, deleted FROM notes"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string key = Notes + "/" + r.GetString(0);
                if (r.GetInt32(7) != 0) { gone[key] = r.GetInt64(6); continue; }
                Add(key, Write(new SNote(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6))), r.GetInt64(6));
            }

        using (var cmd = Cmd(c, tx, "SELECT id, rrule, ext_rule, start_day, remind FROM item_series"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                Add(Series + "/" + r.GetString(0), Write(new SSeries(r.GetString(0), Str(r, 1), Str(r, 2), r.GetString(3), r.GetString(4))), 0);

        var config = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in TimetableKeys)
        {
            using var cmd = Cmd(c, tx, "SELECT value FROM settings WHERE key=$k", ("$k", k));
            if (cmd.ExecuteScalar() is string v) config[k] = v;
        }
        var cells = new List<TimetableCell>();
        using (var cmd = Cmd(c, tx, "SELECT dow, period, subject, room, color FROM timetable_cells ORDER BY period, dow"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) cells.Add(new TimetableCell((DayOfWeek)r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Str(r, 4)));
        if (config.Count > 0 || cells.Count > 0) Add(Timetable, Write(new STimetable(config, cells)), 0);

        if (ReadHolidays() is { } holidays) Add(Holidays, holidays, 0);
        return new LocalCalendar(docs, gone);
    }

    private string? ReadHolidays()
    {
        try { return File.Exists(holidaysPath) ? NormalizeText(File.ReadAllText(holidaysPath)) : null; }
        catch (IOException) { return null; }
    }

    // ---- reading a record from the shared folder ----------------------------------------------

    /// <summary>
    /// Reads a record file of the given kind and returns it in canonical form (so a re-indented or hand-edited file compares by
    /// content, not by layout). Throws <see cref="FormatException"/> when the file is not a valid record.
    /// </summary>
    public static SyncDoc ParseRemote(string kind, string text)
    {
        try
        {
            switch (kind)
            {
                case Holidays:
                    return new SyncDoc(Holidays, NormalizeText(text), 0);
                case Timetable:
                    return new SyncDoc(Timetable, Write(ReadTimetable(text)), 0);
            }
            using var doc = JsonDocument.Parse(text);
            var e = doc.RootElement;
            string id = Req(e, "id");
            if (id.Length == 0) throw new FormatException("id 가 비어 있습니다.");
            return kind switch
            {
                Items => Doc(ReadItem(e), x => x.UpdatedAt),
                Events => Doc(ReadEvent(e), x => x.UpdatedAt),
                Countdowns => Doc(ReadCountdown(e), _ => 0),
                Notes => Doc(ReadNote(e), x => x.UpdatedAt),
                Series => Doc(ReadSeries(e), _ => 0),
                _ => throw new FormatException("알 수 없는 종류: " + kind),
            };

            SyncDoc Doc<T>(T record, Func<T, long> stamp) => new(kind + "/" + id, Write(record!), stamp(record));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    // ---- writing records into this computer's calendar ----------------------------------------

    /// <summary>
    /// Writes <paramref name="upserts"/> and removes <paramref name="deletes"/> in one transaction. A record is only touched while it
    /// still looks the way the merge saw it (<paramref name="expected"/>: key → hash, absent = it did not exist); one the user changed
    /// in the meantime is skipped and left for the next sync. Earlier texts that get replaced are saved to the history.
    /// </summary>
    public SnapshotApplyResult Apply(IReadOnlyList<SyncDoc> upserts, IReadOnlyList<string> deletes, IReadOnlyDictionary<string, string> expected)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        bool holidaysChanged = false;
        long now = clock.NowMs;

        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            var current = Collect(c, tx).Docs;
            bool Unchanged(string key) =>
                current.TryGetValue(key, out var d) ? expected.TryGetValue(key, out var h) && h == d.Hash : !expected.ContainsKey(key);

            var newSeries = new List<string>();
            foreach (var u in upserts.Where(u => u.Key != Holidays))
            {
                if (!Unchanged(u.Key)) { skipped.Add(u.Key); continue; }
                Upsert(c, tx, u, now, newSeries);
                done.Add(u.Key);
            }
            foreach (var key in deletes.Where(k => k != Holidays))
            {
                if (!Unchanged(key)) { skipped.Add(key); continue; }
                Delete(c, tx, key, now);
                done.Add(key);
            }
            // a repeat rule that arrives from elsewhere continues after the last occurrence that came with it (so days deleted there stay deleted)
            foreach (var id in newSeries)
                Exec(c, tx, "UPDATE item_series SET generated_until=(SELECT MAX(day) FROM items WHERE series_id=$i) WHERE id=$i", ("$i", id));
            tx.Commit();
        }

        bool wantHolidays = upserts.Any(u => u.Key == Holidays) || deletes.Contains(Holidays);
        if (wantHolidays)
        {
            string? now0 = ReadHolidays();
            bool same = now0 is null ? !expected.ContainsKey(Holidays) : expected.TryGetValue(Holidays, out var h) && h == GitMerge.HashOf(now0);
            if (!same) skipped.Add(Holidays);
            else
            {
                if (upserts.FirstOrDefault(u => u.Key == Holidays) is { } up) WriteFileAtomic(holidaysPath, up.Text);
                else if (File.Exists(holidaysPath)) File.Delete(holidaysPath);
                done.Add(Holidays);
                holidaysChanged = true;
            }
        }
        return new SnapshotApplyResult(done, skipped, holidaysChanged);
    }

    private static void Upsert(SqliteConnection c, SqliteTransaction tx, SyncDoc doc, long now, List<string> newSeries)
    {
        string kind = KindOf(doc.Key);
        if (doc.Key == Timetable)
        {
            var t = ReadTimetable(doc.Text);
            Exec(c, tx, "DELETE FROM timetable_cells");
            foreach (var cell in t.Cells)
                Exec(c, tx, "INSERT OR REPLACE INTO timetable_cells(dow, period, subject, room, color) VALUES($d,$p,$s,$r,$c)",
                    ("$d", (int)cell.Day), ("$p", cell.Period), ("$s", cell.Subject), ("$r", cell.Room), ("$c", cell.Color));
            foreach (var k in TimetableKeys) Exec(c, tx, "DELETE FROM settings WHERE key=$k", ("$k", k));
            foreach (var (k, v) in t.Config.Where(kv => TimetableKeys.Contains(kv.Key)))
                Exec(c, tx, "INSERT INTO settings(key, value) VALUES($k,$v)", ("$k", k), ("$v", v));
            return;
        }

        using var parsed = JsonDocument.Parse(doc.Text);
        var e = parsed.RootElement;
        switch (kind)
        {
            case Items:
            {
                var i = ReadItem(e);
                if (Scalar(c, tx, "SELECT content FROM items WHERE id=$i", ("$i", i.Id)) is string old && old != i.Content)
                    HistoryRepository.Record(c, tx, EntityNames.Item, i.Id, old, now);
                Exec(c, tx,
                    $"INSERT INTO items({ItemRepository.Cols}) VALUES($id,$day,$c,$bg,$s,$done,$ca,$ua,0,$tc,$sid,$cid) " +
                    "ON CONFLICT(id) DO UPDATE SET day=$day, content=$c, bg_color=$bg, sort=$s, done=$done, created_at=$ca, updated_at=$ua, deleted=0, " +
                    "text_color=$tc, series_id=$sid, countdown_id=$cid",
                    ("$id", i.Id), ("$day", i.Day), ("$c", i.Content), ("$bg", i.BgColor), ("$s", i.Sort), ("$done", i.Done ? 1 : 0),
                    ("$ca", i.CreatedAt), ("$ua", i.UpdatedAt), ("$tc", i.TextColor), ("$sid", i.SeriesId), ("$cid", i.CountdownId));
                ReplaceReminders(c, tx, ReminderOwner.Item, i.Id, i.Reminders, now);
                break;
            }
            case Events:
            {
                var v = ReadEvent(e);
                if (Scalar(c, tx, "SELECT body FROM events WHERE id=$i", ("$i", v.Id)) is string old && old != v.Body)
                    HistoryRepository.Record(c, tx, EntityNames.Event, v.Id, old, now);
                Exec(c, tx,
                    "INSERT INTO events(id, title, body, start, \"end\", all_day, tz, rrule, ext_rule, status, color, created_at, updated_at, deleted) " +
                    "VALUES($id,$t,$b,$s,$e,$ad,$tz,$rr,$ex,$st,$col,$ca,$ua,0) " +
                    "ON CONFLICT(id) DO UPDATE SET title=$t, body=$b, start=$s, \"end\"=$e, all_day=$ad, tz=$tz, rrule=$rr, ext_rule=$ex, status=$st, " +
                    "color=$col, created_at=$ca, updated_at=$ua, deleted=0",
                    ("$id", v.Id), ("$t", v.Title), ("$b", v.Body), ("$s", v.Start), ("$e", v.End), ("$ad", v.AllDay ? 1 : 0), ("$tz", v.Tz),
                    ("$rr", v.RRule), ("$ex", v.ExtRule), ("$st", v.Status), ("$col", v.Color), ("$ca", v.CreatedAt), ("$ua", v.UpdatedAt));
                Exec(c, tx, "DELETE FROM event_exceptions WHERE event_id=$e", ("$e", v.Id));
                foreach (var day in v.Exceptions.Distinct())
                    Exec(c, tx, "INSERT INTO event_exceptions(event_id, day) VALUES($e,$d)", ("$e", v.Id), ("$d", day));
                ReplaceReminders(c, tx, ReminderOwner.Event, v.Id, v.Reminders, now);
                break;
            }
            case Countdowns:
            {
                var cd = ReadCountdown(e);
                Exec(c, tx,
                    "INSERT INTO countdowns(id, title, date, type, show_card, show_bottom, lunar) VALUES($id,$t,$d,$ty,$sc,$sb,$l) " +
                    "ON CONFLICT(id) DO UPDATE SET title=$t, date=$d, type=$ty, show_card=$sc, show_bottom=$sb, lunar=$l",
                    ("$id", cd.Id), ("$t", cd.Title), ("$d", cd.Date), ("$ty", (int)cd.Type),
                    ("$sc", cd.ShowCard ? 1 : 0), ("$sb", cd.ShowBottom ? 1 : 0), ("$l", cd.Lunar ? 1 : 0));
                break;
            }
            case Notes:
            {
                var n = ReadNote(e);
                if (Scalar(c, tx, "SELECT content FROM notes WHERE id=$i", ("$i", n.Id)) is string old && old != n.Content)
                    HistoryRepository.Record(c, tx, EntityNames.Note, n.Id, old, now);
                Exec(c, tx,
                    "INSERT INTO notes(id, title, content, color, status, created_at, updated_at, deleted) VALUES($id,$t,$c,$col,$st,$ca,$ua,0) " +
                    "ON CONFLICT(id) DO UPDATE SET title=$t, content=$c, color=$col, status=$st, created_at=$ca, updated_at=$ua, deleted=0",
                    ("$id", n.Id), ("$t", n.Title), ("$c", n.Content), ("$col", n.Color), ("$st", n.Status), ("$ca", n.CreatedAt), ("$ua", n.UpdatedAt));
                break;
            }
            case Series:
            {
                var s = ReadSeries(e);
                bool exists = Scalar(c, tx, "SELECT id FROM item_series WHERE id=$i", ("$i", s.Id)) is not null;
                // how far occurrences were created is this computer's own business, so it is not part of the record
                Exec(c, tx,
                    "INSERT INTO item_series(id, rrule, ext_rule, start_day, remind, generated_until) VALUES($i,$r,$e,$s,$m,NULL) " +
                    "ON CONFLICT(id) DO UPDATE SET rrule=$r, ext_rule=$e, start_day=$s, remind=$m",
                    ("$i", s.Id), ("$r", s.RRule), ("$e", s.ExtRule), ("$s", s.StartDay), ("$m", s.Remind));
                if (!exists) newSeries.Add(s.Id);
                break;
            }
        }
    }

    private static void Delete(SqliteConnection c, SqliteTransaction tx, string key, long now)
    {
        if (key == Timetable)
        {
            Exec(c, tx, "DELETE FROM timetable_cells");
            foreach (var k in TimetableKeys) Exec(c, tx, "DELETE FROM settings WHERE key=$k", ("$k", k));
            return;
        }
        string id = key[(key.IndexOf('/') + 1)..];
        switch (KindOf(key))
        {
            case Items: Exec(c, tx, "UPDATE items SET deleted=1, updated_at=$u WHERE id=$i AND deleted=0", ("$u", now), ("$i", id)); break;
            case Events: Exec(c, tx, "UPDATE events SET deleted=1, updated_at=$u WHERE id=$i AND deleted=0", ("$u", now), ("$i", id)); break;
            case Notes: Exec(c, tx, "UPDATE notes SET deleted=1, updated_at=$u WHERE id=$i AND deleted=0", ("$u", now), ("$i", id)); break;
            case Countdowns: Exec(c, tx, "DELETE FROM countdowns WHERE id=$i", ("$i", id)); break;
            case Series: Exec(c, tx, "DELETE FROM item_series WHERE id=$i", ("$i", id)); break;
        }
    }

    /// <summary>Replace an owner's reminders; they are armed from now on, so a sync never fires a burst of old alarms.</summary>
    private static void ReplaceReminders(SqliteConnection c, SqliteTransaction tx, ReminderOwner owner, string id, IReadOnlyList<RemindSpec> list, long now)
    {
        Exec(c, tx, "DELETE FROM reminders WHERE owner_type=$o AND owner_id=$i", ("$o", (int)owner), ("$i", id));
        foreach (var r in list)
            Exec(c, tx, "INSERT INTO reminders(id, owner_type, owner_id, offset_minutes, custom_time, sound, fired_at) VALUES($id,$o,$i,$m,$t,$s,$f)",
                ("$id", Guid.NewGuid().ToString("N")), ("$o", (int)owner), ("$i", id), ("$m", Math.Max(0, r.Offset)), ("$t", r.Time), ("$s", r.Sound), ("$f", now));
    }

    public static string KindOf(string key) => key.IndexOf('/') is var i and > 0 ? key[..i] : key;

    // ---- the record formats ----------------------------------------------------------------------

    private sealed record RemindSpec(int Offset, string? Time, string? Sound);
    private sealed record SItem(string Id, string Day, string Content, bool Done, int Sort, string? BgColor, string? TextColor, string? SeriesId,
                                string? CountdownId, long CreatedAt, long UpdatedAt, IReadOnlyList<RemindSpec> Reminders);
    private sealed record SEvent(string Id, string Title, string Body, string Start, string? End, bool AllDay, string? Tz, string? RRule, string? ExtRule,
                                 int Status, string? Color, long CreatedAt, long UpdatedAt, IReadOnlyList<string> Exceptions, IReadOnlyList<RemindSpec> Reminders);
    private sealed record SNote(string Id, string Title, string Content, string? Color, int Status, long CreatedAt, long UpdatedAt);
    private sealed record SSeries(string Id, string? RRule, string? ExtRule, string StartDay, string Remind);
    private sealed record STimetable(IReadOnlyDictionary<string, string> Config, IReadOnlyList<TimetableCell> Cells);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n",
    };

    private static string Write(object record)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            switch (record)
            {
                case SItem i:
                    w.WriteString("id", i.Id); w.WriteString("day", i.Day); w.WriteString("content", i.Content); w.WriteBoolean("done", i.Done);
                    w.WriteNumber("sort", i.Sort); Opt(w, "bgColor", i.BgColor); Opt(w, "textColor", i.TextColor);
                    Opt(w, "seriesId", i.SeriesId); Opt(w, "countdownId", i.CountdownId);
                    w.WriteNumber("createdAt", i.CreatedAt); w.WriteNumber("updatedAt", i.UpdatedAt);
                    WriteReminders(w, i.Reminders);
                    break;
                case SEvent v:
                    w.WriteString("id", v.Id); w.WriteString("title", v.Title); w.WriteString("body", v.Body);
                    w.WriteString("start", v.Start); Opt(w, "end", v.End); w.WriteBoolean("allDay", v.AllDay); Opt(w, "tz", v.Tz);
                    Opt(w, "rrule", v.RRule); Opt(w, "extRule", v.ExtRule); w.WriteNumber("status", v.Status); Opt(w, "color", v.Color);
                    w.WriteNumber("createdAt", v.CreatedAt); w.WriteNumber("updatedAt", v.UpdatedAt);
                    w.WriteStartArray("exceptions");
                    foreach (var d in v.Exceptions.Distinct().Order(StringComparer.Ordinal)) w.WriteStringValue(d);
                    w.WriteEndArray();
                    WriteReminders(w, v.Reminders);
                    break;
                case Countdown cd:
                    w.WriteString("id", cd.Id); w.WriteString("title", cd.Title); w.WriteString("date", cd.Date); w.WriteNumber("type", (int)cd.Type);
                    w.WriteBoolean("showCard", cd.ShowCard); w.WriteBoolean("showBottom", cd.ShowBottom); w.WriteBoolean("lunar", cd.Lunar);
                    break;
                case SNote n:
                    w.WriteString("id", n.Id); w.WriteString("title", n.Title); w.WriteString("content", n.Content); Opt(w, "color", n.Color);
                    w.WriteNumber("status", n.Status); w.WriteNumber("createdAt", n.CreatedAt); w.WriteNumber("updatedAt", n.UpdatedAt);
                    break;
                case SSeries s:
                    w.WriteString("id", s.Id); Opt(w, "rrule", s.RRule); Opt(w, "extRule", s.ExtRule); w.WriteString("startDay", s.StartDay); w.WriteString("remind", s.Remind);
                    break;
                case STimetable t:
                    w.WriteStartObject("config");
                    foreach (var (k, val) in t.Config.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(k, val);
                    w.WriteEndObject();
                    w.WriteStartArray("cells");
                    foreach (var cell in t.Cells.OrderBy(x => x.Period).ThenBy(x => (int)x.Day))
                    {
                        w.WriteStartObject();
                        w.WriteNumber("dow", (int)cell.Day); w.WriteNumber("period", cell.Period); w.WriteString("subject", cell.Subject);
                        w.WriteString("room", cell.Room); Opt(w, "color", cell.Color);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    break;
                default: throw new ArgumentException("unknown record", nameof(record));
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteReminders(Utf8JsonWriter w, IReadOnlyList<RemindSpec> list)
    {
        w.WriteStartArray("reminders");
        foreach (var r in list.OrderByDescending(r => r.Offset).ThenBy(r => r.Time, StringComparer.Ordinal).ThenBy(r => r.Sound, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteNumber("offsetMinutes", r.Offset); Opt(w, "time", r.Time); Opt(w, "sound", r.Sound);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Opt(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    private static SItem ReadItem(JsonElement e) => new(Req(e, "id"), Day(e, "day"), Req(e, "content"), Bool(e, "done"), Int(e, "sort"),
        Opt(e, "bgColor"), Opt(e, "textColor"), Opt(e, "seriesId"), Opt(e, "countdownId"), Long(e, "createdAt"), Long(e, "updatedAt"), ReadReminders(e));

    private static SEvent ReadEvent(JsonElement e) => new(Req(e, "id"), Req(e, "title"), Opt(e, "body") ?? "", Req(e, "start"), Opt(e, "end"), Bool(e, "allDay"),
        Opt(e, "tz"), Opt(e, "rrule"), Opt(e, "extRule"), Int(e, "status"), Opt(e, "color"), Long(e, "createdAt"), Long(e, "updatedAt"),
        e.TryGetProperty("exceptions", out var x) && x.ValueKind == JsonValueKind.Array
            ? x.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String && IsDay(d.GetString())).Select(d => d.GetString()!).ToList() : [],
        ReadReminders(e));

    private static Countdown ReadCountdown(JsonElement e) => new(Req(e, "id"), Req(e, "title"), Day(e, "date"), (CountdownType)Int(e, "type"),
        Bool(e, "showCard"), Bool(e, "showBottom"), Bool(e, "lunar"));

    private static SNote ReadNote(JsonElement e) => new(Req(e, "id"), Opt(e, "title") ?? "", Opt(e, "content") ?? "", Opt(e, "color"), Int(e, "status"),
        Long(e, "createdAt"), Long(e, "updatedAt"));

    private static SSeries ReadSeries(JsonElement e) => new(Req(e, "id"), Opt(e, "rrule"), Opt(e, "extRule"), Day(e, "startDay"), Opt(e, "remind") ?? "");

    private static STimetable ReadTimetable(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var e = doc.RootElement;
        var config = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
            foreach (var p in cfg.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && TimetableKeys.Contains(p.Name)) config[p.Name] = p.Value.GetString()!;
        var cells = new List<TimetableCell>();
        if (e.TryGetProperty("cells", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
            {
                int dow = Int(x, "dow");
                if (dow is < 0 or > 6) throw new FormatException("dow 는 0~6 이어야 합니다.");
                cells.Add(new TimetableCell((DayOfWeek)dow, Int(x, "period"), Req(x, "subject"), Opt(x, "room") ?? "", Opt(x, "color")));
            }
        return new STimetable(config, cells);
    }

    private static List<RemindSpec> ReadReminders(JsonElement e) =>
        e.TryGetProperty("reminders", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(r => new RemindSpec(Int(r, "offsetMinutes"), Opt(r, "time"), Opt(r, "sound"))).ToList()
            : [];

    private static string Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : throw new FormatException($"'{name}' 가 없습니다.");

    private static string? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Day(JsonElement e, string name) =>
        Req(e, name) is var d && IsDay(d) ? d : throw new FormatException($"'{name}' 는 yyyy-MM-dd 날짜여야 합니다.");

    private static bool IsDay(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : 0;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long v) ? v : 0;

    // ---- small helpers -----------------------------------------------------------------------------

    /// <summary>Line endings as "\n", so the same text written on different computers compares equal.</summary>
    public static string NormalizeText(string text) => text.Replace("\r\n", "\n").TrimStart('﻿');

    public static void WriteFileAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, tx, sql, p);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, tx, sql, p);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
