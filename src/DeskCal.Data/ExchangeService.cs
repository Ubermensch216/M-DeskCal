using System.Globalization;
using System.Text;
using DeskCal.Core;
using DeskCal.Core.Exchange;
using Microsoft.Data.Sqlite;

namespace DeskCal.Data;

public sealed record ImportResult(
    int ItemsAdded, int ItemsSkipped, int EventsAdded, int EventsSkipped,
    int CountdownsAdded, int CountdownsSkipped, int NotesAdded, int NotesSkipped)
{
    public int TotalAdded => ItemsAdded + EventsAdded + CountdownsAdded + NotesAdded;
    public int TotalSkipped => ItemsSkipped + EventsSkipped + CountdownsSkipped + NotesSkipped;
}

/// <summary>Reads a slice of the database into an <see cref="ExchangeBundle"/> and merges a bundle back in.</summary>
public sealed class ExchangeService(DeskCalDb db, IClock clock, Func<DateOnly, bool>? isDayOff = null)
{
    // ---- export ---------------------------------------------------------------------------------

    /// <summary>
    /// Items on days inside [from, to] and events that have an occurrence there (both bounds null = everything).
    /// Countdowns and notes are included only for a full export, since they do not belong to a period.
    /// </summary>
    public ExchangeBundle Collect(DateOnly? from = null, DateOnly? to = null)
    {
        var items = new ItemRepository(db, clock);
        var events = new EventRepository(db, clock);
        var reminders = new ReminderRepository(db, clock).ListAll()
            .GroupBy(r => (r.OwnerType, r.OwnerId)).ToDictionary(g => g.Key, g => (IReadOnlyList<ExReminder>)g.Select(r => new ExReminder(r.OffsetMinutes, r.CustomTime)).ToList());
        IReadOnlyList<ExReminder> RemindersOf(ReminderOwner o, string id) => reminders.TryGetValue((o, id), out var l) ? l : [];

        string? f = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var exItems = items.GetForSearch(f, t, includeDeleted: false)
            .OrderBy(i => i.Day, StringComparer.Ordinal).ThenBy(i => i.Sort)
            .Select(i => new ExItem(i.Id, i.Day, i.Content, i.Done, i.BgColor, i.TextColor, i.Sort, RemindersOf(ReminderOwner.Item, i.Id))).ToList();

        var skips = events.GetExceptions();
        var exEvents = events.GetAllActive().Where(e => InRange(e, from, to))
            .Select(e => new ExEvent(e.Id, e.Title, e.Body, e.Start, e.End, e.AllDay, e.RRule, e.ExtRule, e.Color,
                skips.TryGetValue(e.Id, out var s) ? s.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList() : [],
                RemindersOf(ReminderOwner.Event, e.Id))).ToList();

        bool full = from is null && to is null;
        var countdowns = full ? new CountdownRepository(db, clock).List() : [];
        var notes = full ? new NoteRepository(db, clock).List().Select(n => new ExNote(n.Id, n.Title, n.Content, n.Color)).ToList() : [];
        return new ExchangeBundle(exItems, exEvents, countdowns, notes);
    }

    private bool InRange(EventEntry e, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null) return true;
        if (!EventTime.TryParse(e.Start, out var d0, out _)) return false;
        var rule = RecurrenceRule.From(e);
        DateOnly lo = from ?? DateOnly.MinValue, hi = to ?? DateOnly.MaxValue;

        if (!rule.IsRepeating)
        {
            DateOnly end = EventTime.TryParse(e.End, out var d1, out _) ? d1 : d0;
            return d0 <= hi && end >= lo;
        }
        if (d0 > hi) return false;
        if (rule.Count is not null) return RecurrenceExpander.Between(rule, d0, lo, hi, isDayOff).Count > 0;   // bounded: check for real
        return rule.Until is not { } u || u >= lo;                                                            // unbounded: conservative
    }

    // ---- import ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds everything in the bundle whose id is not already in the database (existing records are never changed), in one
    /// transaction: either all of it is added or, on an error, none. New reminders are armed from now on, so importing
    /// never fires a burst of old alarms.
    /// </summary>
    public ImportResult Apply(ExchangeBundle bundle)
    {
        long now = clock.NowMs;
        int ia = 0, isk = 0, ea = 0, esk = 0, ca = 0, csk = 0, na = 0, nsk = 0;

        using var c = db.Open();
        using var tx = c.BeginTransaction();

        foreach (var i in bundle.Items)
        {
            long sort;
            using (var m = Cmd(c, tx, "SELECT COALESCE(MAX(sort), -1) + 1 FROM items WHERE day=$d AND deleted=0", ("$d", i.Day))) sort = Convert.ToInt64(m.ExecuteScalar());
            using var cmd = Cmd(c, tx,
                "INSERT OR IGNORE INTO items(id, day, content, bg_color, sort, done, created_at, updated_at, deleted, text_color) VALUES($id,$d,$c,$bg,$s,$done,$n,$n,0,$tc)",
                ("$id", i.Id), ("$d", i.Day), ("$c", i.Content), ("$bg", i.BgColor), ("$s", sort), ("$done", i.Done ? 1 : 0), ("$n", now), ("$tc", i.TextColor));
            if (cmd.ExecuteNonQuery() == 0) { isk++; continue; }
            ia++;
            AddReminders(c, tx, ReminderOwner.Item, i.Id, i.Reminders, now);
        }

        foreach (var e in bundle.Events)
        {
            using var cmd = Cmd(c, tx,
                "INSERT OR IGNORE INTO events(id, title, body, start, \"end\", all_day, tz, rrule, ext_rule, status, color, created_at, updated_at, deleted) " +
                "VALUES($id,$t,$b,$s,$e,$ad,NULL,$rr,$ex,0,$col,$n,$n,0)",
                ("$id", e.Id), ("$t", e.Title), ("$b", e.Body), ("$s", e.Start), ("$e", e.End), ("$ad", e.AllDay ? 1 : 0),
                ("$rr", e.RRule), ("$ex", e.ExtRule), ("$col", e.Color), ("$n", now));
            if (cmd.ExecuteNonQuery() == 0) { esk++; continue; }
            ea++;
            foreach (var day in e.Exceptions)
            {
                using var x = Cmd(c, tx, "INSERT OR IGNORE INTO event_exceptions(event_id, day) VALUES($e,$d)", ("$e", e.Id), ("$d", day));
                x.ExecuteNonQuery();
            }
            AddReminders(c, tx, ReminderOwner.Event, e.Id, e.Reminders, now);
        }

        foreach (var cd in bundle.Countdowns)
        {
            using var cmd = Cmd(c, tx,
                "INSERT OR IGNORE INTO countdowns(id, title, date, type, show_card, show_bottom, lunar) VALUES($id,$t,$d,$ty,$sc,$sb,$l)",
                ("$id", cd.Id), ("$t", cd.Title), ("$d", cd.Date), ("$ty", (int)cd.Type), ("$sc", cd.ShowCard ? 1 : 0), ("$sb", cd.ShowBottom ? 1 : 0), ("$l", cd.Lunar ? 1 : 0));
            if (cmd.ExecuteNonQuery() == 0) csk++; else ca++;
        }

        foreach (var n in bundle.Notes)
        {
            using var cmd = Cmd(c, tx,
                "INSERT OR IGNORE INTO notes(id, title, content, color, status, created_at, updated_at, deleted) VALUES($id,$t,$c,$col,0,$n,$n,0)",
                ("$id", n.Id), ("$t", n.Title), ("$c", n.Content), ("$col", n.Color), ("$n", now));
            if (cmd.ExecuteNonQuery() == 0) nsk++; else na++;
        }

        tx.Commit();
        return new ImportResult(ia, isk, ea, esk, ca, csk, na, nsk);
    }

    private static void AddReminders(SqliteConnection c, SqliteTransaction tx, ReminderOwner owner, string ownerId, IReadOnlyList<ExReminder> list, long armedAt)
    {
        foreach (var r in list)
        {
            using var cmd = Cmd(c, tx,
                "INSERT INTO reminders(id, owner_type, owner_id, offset_minutes, custom_time, sound, fired_at) VALUES($id,$o,$oid,$m,$ct,NULL,$f)",
                ("$id", Guid.NewGuid().ToString("N")), ("$o", (int)owner), ("$oid", ownerId), ("$m", r.OffsetMinutes), ("$ct", r.CustomTime), ("$f", armedAt));
            cmd.ExecuteNonQuery();
        }
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }
}

public enum ExchangeFormat { Json, Csv, Ics, Xlsx }

/// <summary>What the Excel export needs beyond the data: the period (for the month sheets), week start and holiday names.</summary>
public sealed record XlsxOptions(DateOnly? From, DateOnly? To, DayOfWeek FirstDay = DayOfWeek.Sunday, Func<DateOnly, string?>? HolidayName = null);

/// <summary>Reading and writing exchange files: format by extension, text encoding detection, safe replacement of the target file.</summary>
public static class ExchangeFiles
{
    public const long MaxBytes = 50L * 1024 * 1024;

    public static string Extension(ExchangeFormat f) => f switch { ExchangeFormat.Json => ".json", ExchangeFormat.Csv => ".csv", ExchangeFormat.Xlsx => ".xlsx", _ => ".ics" };

    public static ExchangeFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => ExchangeFormat.Json, ".csv" => ExchangeFormat.Csv, ".xlsx" => ExchangeFormat.Xlsx, ".ics" or ".ical" or ".ifb" => ExchangeFormat.Ics, _ => null,
    };

    /// <exception cref="ExchangeFormatException">Too large, or not readable as the format its extension (or content) says.</exception>
    public static ExchangeReadResult ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ExchangeFormatException("파일을 찾을 수 없습니다.");
        if (info.Length > MaxBytes) throw new ExchangeFormatException($"파일이 너무 큽니다 ({info.Length / 1024 / 1024} MB, 한도 {MaxBytes / 1024 / 1024} MB).");

        if (FormatFromPath(path) == ExchangeFormat.Xlsx)
            throw new ExchangeFormatException("엑셀(.xlsx) 파일은 가져올 수 없습니다. 엑셀에서 'CSV UTF-8' 또는 'CSV'로 다시 저장한 뒤 가져오세요.");

        string text = Decode(File.ReadAllBytes(path));
        var format = FormatFromPath(path) ?? Sniff(text);
        return format switch
        {
            ExchangeFormat.Json => JsonExchange.Read(text),
            ExchangeFormat.Ics => IcsExchange.Read(text),
            _ => CsvExchange.Read(text),
        };
    }

    public static void WriteFile(string path, ExchangeFormat format, ExchangeBundle bundle, DateTimeOffset now,
                                 Func<DateOnly, bool>? isDayOff = null, XlsxOptions? xlsx = null)
    {
        byte[] bytes;
        if (format == ExchangeFormat.Xlsx)
        {
            var o = xlsx ?? new XlsxOptions(null, null);
            using var ms = new MemoryStream();
            XlsxWriter.Write(ms, XlsxExport.Build(bundle, o.From, o.To, o.FirstDay, isDayOff, o.HolidayName));
            bytes = ms.ToArray();
        }
        else
        {
            string text = format switch
            {
                ExchangeFormat.Json => JsonExchange.Write(bundle, now),
                ExchangeFormat.Csv => CsvExchange.Write(bundle),
                _ => IcsExchange.Write(bundle, now.UtcDateTime, isDayOff),
            };
            // CSV gets a BOM so that Excel opens Korean text correctly; JSON and ICS are plain UTF-8.
            bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ExchangeFormat.Csv).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static ExchangeFormat Sniff(string text)
    {
        var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (t.StartsWith('{')) return ExchangeFormat.Json;
        if (t.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return ExchangeFormat.Ics;
        return ExchangeFormat.Csv;
    }

    /// <summary>UTF-8 (with or without BOM) if the bytes are valid UTF-8, otherwise Korean Windows code page 949 (Excel's default CSV encoding).</summary>
    internal static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }
}
