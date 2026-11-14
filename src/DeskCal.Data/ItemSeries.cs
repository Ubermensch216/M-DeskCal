using System.Globalization;
using DeskCal.Core;
using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

/// <summary>One reminder of a day-cell item: fires <see cref="OffsetMinutes"/> before <see cref="Time"/> ("HH:mm") on the item's day.</summary>
public readonly record struct ItemReminderSpec(int OffsetMinutes, string Time);

/// <summary>A repeat rule attached to day-cell items. <see cref="StartDay"/> is the day of the item the rule was last applied to.</summary>
public sealed record ItemSeries(string Id, RecurrenceRule Rule, DateOnly StartDay, string? GeneratedUntil);

/// <summary>
/// Repeating day-cell items. Every occurrence is an ordinary item of its own (so it can be completed, coloured, edited or deleted
/// on its own), tied to the others by <see cref="Item.SeriesId"/>. Occurrences are created up to a year ahead
/// (<see cref="HorizonDays"/>); <see cref="Extend"/> tops them up as time passes. The text, colours and reminders that the item
/// has when the rule is applied are copied to the later occurrences.
/// </summary>
public sealed class ItemSeriesRepository(DeskCalDb db, IClock clock, Func<DateOnly, bool>? isDayOff = null) : RepoBase(db, clock)
{
    public const int HorizonDays = 366;
    private const int MaxPerCall = 800;
    private const string Complete = "9999-12-31";       // generated_until of a series whose rule has no more occurrences

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool TryDay(string? s, out DateOnly d) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    public ItemSeries? Get(string seriesId)
    {
        using var c = Db.Open();
        return Get(c, null, seriesId);
    }

    private static ItemSeries? Get(SqliteConnection c, SqliteTransaction? tx, string seriesId)
    {
        using var cmd = Cmd(c, "SELECT id, rrule, ext_rule, start_day, generated_until FROM item_series WHERE id=$i", tx, ("$i", seriesId));
        using var r = cmd.ExecuteReader();
        if (!r.Read() || !TryDay(r.GetString(3), out var start)) return null;
        RecurrenceRule.TryParse(Str(r, 1), Str(r, 2), out var rule);
        return new ItemSeries(r.GetString(0), rule, start, Str(r, 4));
    }

    /// <summary>
    /// Make the item repeat by <paramref name="rule"/>: occurrences after the item's day are (re)created, the item itself stays.
    /// A rule of <see cref="RepeatKind.None"/> ends the repetition (same as <see cref="ClearRepeat"/>). Returns how many items were created.
    /// </summary>
    public int SetRepeat(string itemId, RecurrenceRule rule, DateOnly today)
    {
        if (!rule.IsRepeating) { ClearRepeat(itemId); return 0; }

        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var item = GetItem(c, tx, itemId);
        if (item is null || item.Deleted || !TryDay(item.Day, out var start)) return 0;

        string seriesId = item.SeriesId ?? NewId();
        if (item.SeriesId is not null) DeleteAfter(c, tx, seriesId, item.Day);
        else Exec(c, tx, "UPDATE items SET series_id=$s WHERE id=$i", ("$s", seriesId), ("$i", itemId));

        var remind = Format(Reminders(c, tx, itemId));
        var (rrule, ext) = rule.ToColumns(start);
        Exec(c, tx,
            "INSERT INTO item_series(id, rrule, ext_rule, start_day, remind, generated_until) VALUES($i,$r,$e,$s,$m,$g) " +
            "ON CONFLICT(id) DO UPDATE SET rrule=$r, ext_rule=$e, start_day=$s, remind=$m, generated_until=$g",
            ("$i", seriesId), ("$r", rrule), ("$e", ext), ("$s", item.Day), ("$m", remind), ("$g", item.Day));

        int made = Fill(c, tx, seriesId, rule, start, item.Day, item.Content, item.BgColor, item.TextColor, Parse(remind), today);
        tx.Commit();
        return made;
    }

    /// <summary>Stop repeating: the item and the earlier occurrences stay; the later ones are removed.</summary>
    public void ClearRepeat(string itemId)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var item = GetItem(c, tx, itemId);
        if (item?.SeriesId is { } seriesId)
        {
            DeleteAfter(c, tx, seriesId, item.Day);
            Exec(c, tx, "UPDATE items SET series_id=NULL WHERE series_id=$s", ("$s", seriesId));
            Exec(c, tx, "DELETE FROM item_series WHERE id=$s", ("$s", seriesId));
        }
        tx.Commit();
    }

    /// <summary>Create the occurrences that now fall inside the horizon (call at start-up and when the day changes). Returns how many were created.</summary>
    public int Extend(DateOnly today)
    {
        using var c = Db.Open();
        var horizon = Fmt(today.AddDays(HorizonDays));
        var due = new List<string>();
        using (var cmd = Cmd(c, "SELECT id FROM item_series WHERE generated_until IS NULL OR (generated_until < $h AND generated_until <> $c)", null, ("$h", horizon), ("$c", Complete)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) due.Add(r.GetString(0));
        if (due.Count == 0) return 0;

        int total = 0;
        using var tx = c.BeginTransaction();
        foreach (var id in due)
        {
            var series = Get(c, tx, id);
            if (series is null) continue;
            // The newest live occurrence is the template for the new ones; with none left the series is over.
            Item? last = null;
            using (var sel = Cmd(c, $"SELECT {ItemRepository.Cols} FROM items WHERE series_id=$s AND deleted=0 ORDER BY day DESC LIMIT 1", tx, ("$s", id)))
            using (var r = sel.ExecuteReader())
                if (r.Read()) last = ItemRepository.Map(r);
            if (last is null) { Exec(c, tx, "DELETE FROM item_series WHERE id=$s", ("$s", id)); continue; }

            string remind = ScalarString(c, tx, "SELECT remind FROM item_series WHERE id=$s", ("$s", id)) ?? "";
            total += Fill(c, tx, id, series.Rule, series.StartDay, series.GeneratedUntil ?? Fmt(series.StartDay),
                last.Content, last.BgColor, last.TextColor, Parse(remind), today);
        }
        tx.Commit();
        return total;
    }

    // ---- internals -------------------------------------------------------------

    /// <summary>Insert the occurrences after <paramref name="after"/> up to the horizon, and record how far the series now reaches.</summary>
    private int Fill(SqliteConnection c, SqliteTransaction tx, string seriesId, RecurrenceRule rule, DateOnly start, string after,
                     string content, string? bg, string? text, IReadOnlyList<ItemReminderSpec> reminders, DateOnly today)
    {
        TryDay(after, out var afterDay);
        // A year ahead of today - or of the series' own start when that lies further out than today.
        var horizon = (start > today ? start : today).AddDays(HorizonDays);

        long now = Clock.NowMs;
        int made = 0;
        DateOnly? lastDay = null;
        bool exhausted = true;
        foreach (var d in RecurrenceExpander.Generate(rule, start, isDayOff))
        {
            if (d <= afterDay) continue;
            if (d > horizon || made >= MaxPerCall) { exhausted = false; break; }

            string day = Fmt(d);
            int sort = Convert.ToInt32(ScalarString(c, tx, "SELECT COALESCE(MAX(sort), -1) + 1 FROM items WHERE day=$d AND deleted=0", ("$d", day)));
            string id = NewId();
            Exec(c, tx,
                $"INSERT INTO items({ItemRepository.Cols}) VALUES($id,$day,$c,$bg,$s,0,$n,$n,0,$tc,$sid,NULL)",
                ("$id", id), ("$day", day), ("$c", content), ("$bg", bg), ("$s", sort), ("$n", now), ("$tc", text), ("$sid", seriesId));
            foreach (var spec in reminders)
                Exec(c, tx,
                    "INSERT INTO reminders(id, owner_type, owner_id, offset_minutes, custom_time, sound, fired_at) VALUES($id,0,$o,$m,$t,NULL,$f)",
                    ("$id", NewId()), ("$o", id), ("$m", spec.OffsetMinutes), ("$t", spec.Time), ("$f", now));
            lastDay = d;
            made++;
        }
        // Remember how far the series reaches; a rule that ran out is marked complete so it is not examined again.
        string reached = exhausted ? Complete : Fmt(lastDay ?? afterDay);
        Exec(c, tx, "UPDATE item_series SET generated_until=$g WHERE id=$i", ("$g", reached), ("$i", seriesId));
        return made;
    }

    /// <summary>Remove the occurrences of a series that lie after <paramref name="day"/>, with their reminders and history.</summary>
    private static void DeleteAfter(SqliteConnection c, SqliteTransaction tx, string seriesId, string day)
    {
        const string ids = "(SELECT id FROM items WHERE series_id=$s AND day > $d)";
        Exec(c, tx, $"DELETE FROM reminders WHERE owner_type=0 AND owner_id IN {ids}", ("$s", seriesId), ("$d", day));
        Exec(c, tx, $"DELETE FROM history WHERE entity='item' AND entity_id IN {ids}", ("$s", seriesId), ("$d", day));
        Exec(c, tx, "DELETE FROM items WHERE series_id=$s AND day > $d", ("$s", seriesId), ("$d", day));
    }

    private static Item? GetItem(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var cmd = Cmd(c, $"SELECT {ItemRepository.Cols} FROM items WHERE id=$i", tx, ("$i", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ItemRepository.Map(r) : null;
    }

    private static List<ItemReminderSpec> Reminders(SqliteConnection c, SqliteTransaction tx, string itemId)
    {
        using var cmd = Cmd(c, "SELECT offset_minutes, custom_time FROM reminders WHERE owner_type=0 AND owner_id=$i ORDER BY offset_minutes DESC", tx, ("$i", itemId));
        return ReadAll(cmd, r => new ItemReminderSpec(r.GetInt32(0), Str(r, 1) ?? "09:00"));
    }

    private static string Format(IEnumerable<ItemReminderSpec> specs) =>
        string.Join(";", specs.Select(s => $"{s.OffsetMinutes}@{s.Time}"));

    private static List<ItemReminderSpec> Parse(string text)
    {
        var list = new List<ItemReminderSpec>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('@');
            if (kv.Length == 2 && int.TryParse(kv[0], NumberStyles.None, CultureInfo.InvariantCulture, out int m))
                list.Add(new ItemReminderSpec(m, kv[1]));
        }
        return list;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, sql, tx, p);
        cmd.ExecuteNonQuery();
    }

    private static string? ScalarString(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, sql, tx, p);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
