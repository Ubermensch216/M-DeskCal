using System.Globalization;
using System.Text;

namespace DeskCal.Core.Exchange;

/// <summary>
/// Spreadsheet-friendly list: one row per to-do ("todo") or event ("event"). Columns:
/// type, date, start_time, end_date, end_time, all_day, title, body, done, color, bg_color, rrule, ext_rule, exceptions, reminders, id.
/// Reading is forgiving (header names are case-insensitive, a few Korean headings and date styles are accepted, only
/// <c>date</c> and <c>title</c> are required); writing is exact, so a file written here reads back unchanged.
/// Text that a spreadsheet would run as a formula (starts with = + - @) is written with a leading apostrophe, which
/// reading removes again.
/// </summary>
public static class CsvExchange
{
    public static readonly string[] Header =
        ["type", "date", "start_time", "end_date", "end_time", "all_day", "title", "body", "done", "color", "bg_color", "rrule", "ext_rule", "exceptions", "reminders", "id"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["종류"] = "type", ["유형"] = "type", ["날짜"] = "date", ["시작일"] = "date", ["시작시간"] = "start_time", ["시작 시간"] = "start_time",
        ["종료일"] = "end_date", ["종료시간"] = "end_time", ["종료 시간"] = "end_time", ["종일"] = "all_day",
        ["제목"] = "title", ["내용"] = "title", ["메모"] = "body", ["완료"] = "done", ["색"] = "color", ["글자색"] = "color", ["배경색"] = "bg_color",
        ["반복"] = "rrule", ["알림"] = "reminders", ["start"] = "start_time", ["end"] = "end_time", ["summary"] = "title",
        ["description"] = "body", ["notes"] = "body", ["day"] = "date",
    };

    // ---- writing ----------------------------------------------------------------

    public static string Write(ExchangeBundle bundle)
    {
        var sb = new StringBuilder();
        Row(sb, Header);

        foreach (var i in bundle.Items)
            Row(sb, ["todo", i.Day, "", "", "", "", i.Content, "", i.Done ? "1" : "0", i.TextColor ?? "", i.BgColor ?? "", "", "", "",
                Reminders(i.Reminders), i.Id]);

        foreach (var e in bundle.Events)
        {
            EventTime.TryParse(e.Start, out var d0, out var t0);
            string endDate = "", endTime = "";
            if (EventTime.TryParse(e.End, out var d1, out var t1)) { endDate = CountdownCalc.Format(d1); endTime = t1 is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : ""; }
            Row(sb, ["event", CountdownCalc.Format(d0), t0 is { } s ? s.ToString("HH:mm", CultureInfo.InvariantCulture) : "", endDate, endTime,
                t0 is null ? "1" : "0", e.Title, e.Body, "", e.Color ?? "", "", e.RRule ?? "", e.ExtRule ?? "", string.Join(";", e.Exceptions),
                Reminders(e.Reminders), e.Id]);
        }
        return sb.ToString();
    }

    private static string Reminders(IReadOnlyList<ExReminder> list) =>
        string.Join(";", list.Select(r => r.CustomTime is null ? r.OffsetMinutes.ToString(CultureInfo.InvariantCulture) : $"{r.OffsetMinutes}@{r.CustomTime}"));

    private static void Row(StringBuilder sb, string[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            string c = cells[i];
            if (c.Length > 0 && (i == 6 || i == 7) && "=+-@\t\r".Contains(c[0])) c = "'" + c;   // title / body: spreadsheet formula guard
            if (c.AsSpan().IndexOfAny(",\"\r\n") >= 0 || c.StartsWith(' ') || c.EndsWith(' '))
                sb.Append('"').Append(c.Replace("\"", "\"\"")).Append('"');
            else sb.Append(c);
        }
        sb.Append("\r\n");
    }

    // ---- reading ------------------------------------------------------------------

    /// <exception cref="ExchangeFormatException">No usable header row, or an unterminated quote.</exception>
    public static ExchangeReadResult Read(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0) throw new ExchangeFormatException("CSV 파일이 비어 있습니다.");

        var (headerLine, header) = rows[0];
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < header.Count; c++)
        {
            string name = header[c].Trim().Trim('﻿');
            if (Aliases.TryGetValue(name, out var canon)) name = canon;
            index.TryAdd(name.ToLowerInvariant(), c);
        }
        if (!index.ContainsKey("date") || !index.ContainsKey("title"))
            throw new ExchangeFormatException("첫 줄(머리글)에 'date'(날짜)와 'title'(제목) 열이 있어야 합니다. 예: type,date,title");

        string Get(List<string> r, string col) => index.TryGetValue(col, out int c) && c < r.Count ? r[c].Trim() : "";
        string Text(List<string> r, string col) => Unguard(index.TryGetValue(col, out int c) && c < r.Count ? r[c] : "");

        var warnings = new List<string>();
        var items = new List<ExItem>();
        var events = new List<ExEvent>();

        foreach (var (line, r) in rows.Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace)) continue;

            if (!TryDate(Get(r, "date"), out var date)) { warnings.Add($"{line}행: 날짜를 읽을 수 없어 건너뜀 ({Get(r, "date")})"); continue; }
            string title = Text(r, "title");
            if (string.IsNullOrWhiteSpace(title)) { warnings.Add($"{line}행: 제목이 없어 건너뜀"); continue; }

            string typeText = Get(r, "type").ToLowerInvariant();
            bool? isEvent = typeText switch
            {
                "event" or "일정" or "이벤트" => true,
                "todo" or "task" or "item" or "할 일" or "할일" or "항목" => false,
                "" => Get(r, "start_time").Length > 0 || Get(r, "end_date").Length > 0 || Get(r, "rrule").Length > 0 || Get(r, "ext_rule").Length > 0 ? true : false,
                _ => null,
            };
            if (isEvent is null) { warnings.Add($"{line}행: 알 수 없는 종류 '{Get(r, "type")}' (todo 또는 event) — 건너뜀"); continue; }

            string id = Get(r, "id");
            var reminders = ParseReminders(Get(r, "reminders"), line, warnings);

            if (isEvent == false)
            {
                items.Add(new ExItem(id, CountdownCalc.Format(date), title, ParseBool(Get(r, "done")), NullIfBlank(Get(r, "bg_color")),
                    NullIfBlank(Get(r, "color")), items.Count, reminders));
                continue;
            }

            string startTime = Get(r, "start_time");
            bool allDay = ParseBool(Get(r, "all_day")) || startTime.Length == 0;
            TimeOnly? t0 = null;
            if (!allDay)
            {
                if (!TryTime(startTime, out var t)) { warnings.Add($"{line}행: 시작 시간을 읽을 수 없어 종일 일정으로 가져옴 ({startTime})"); allDay = true; }
                else t0 = t;
            }

            string? end = null;
            string endDateText = Get(r, "end_date"), endTimeText = Get(r, "end_time");
            if (endDateText.Length > 0 || endTimeText.Length > 0)
            {
                DateOnly endDate = date;
                bool ok = endDateText.Length == 0 || TryDate(endDateText, out endDate);
                TimeOnly? t1 = null;
                if (!allDay && endTimeText.Length > 0) { if (TryTime(endTimeText, out var t)) t1 = t; else ok = false; }
                if (ok && (allDay ? endDate > date : t1 is not null)) end = EventTime.Format(endDate, allDay ? null : t1);
                else if (!ok) warnings.Add($"{line}행: 종료 날짜/시간을 읽을 수 없어 무시함");
            }

            events.Add(new ExEvent(id, title, Text(r, "body"), EventTime.Format(date, t0), end, allDay,
                NullIfBlank(Get(r, "rrule")), NullIfBlank(Get(r, "ext_rule")), NullIfBlank(Get(r, "color")),
                Get(r, "exceptions").Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), reminders));
        }

        return ExchangeCleaner.Clean(new ExchangeBundle(items, events, [], []), warnings);
    }

    private static string? NullIfBlank(string s) => s.Length == 0 ? null : s;

    private static string Unguard(string s) => s.Length > 1 && s[0] == '\'' && "=+-@\t\r".Contains(s[1]) ? s[1..] : s;

    private static bool ParseBool(string s) => s.ToLowerInvariant() is "1" or "true" or "yes" or "y" or "o" or "x" or "완료" or "예" or "✓" or "v";

    private static List<ExReminder> ParseReminders(string text, int line, List<string> warnings)
    {
        var list = new List<ExReminder>();
        foreach (var tok in text.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = tok.IndexOf('@');
            string num = at < 0 ? tok : tok[..at];
            if (!int.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)) { warnings.Add($"{line}행: 알림 '{tok}' 을(를) 읽을 수 없어 무시함"); continue; }
            list.Add(new ExReminder(minutes, at < 0 ? null : tok[(at + 1)..]));
        }
        return list;
    }

    private static readonly string[] DateFormats = ["yyyy-M-d", "yyyyMMdd"];

    /// <summary>Accepts 2026-11-09, 2026/11/9, 2026.11.9, "2026. 11. 9." (Excel, Korean) and 20261109.</summary>
    internal static bool TryDate(string text, out DateOnly date)
    {
        string s = text.Replace(" ", "").TrimEnd('.').Replace('/', '-').Replace('.', '-');
        return DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];
    internal static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>RFC 4180 parsing with the starting line number of every record; the delimiter (, or ;) is guessed from the header.</summary>
    internal static List<(int Line, List<string> Cells)> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        int firstEnd = text.IndexOfAny(['\r', '\n']);
        string first = firstEnd < 0 ? text : text[..firstEnd];
        char delim = first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';

        var rows = new List<(int, List<string>)>();
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        int line = 1, rowLine = 1;

        void EndCell() { cells.Add(cur.ToString()); cur.Clear(); any = true; }
        void EndRow()
        {
            if (any || cur.Length > 0) { EndCell(); rows.Add((rowLine, cells)); cells = []; }
            any = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; } else quoted = false; }
                else { if (ch == '\n') line++; cur.Append(ch); }
                continue;
            }
            switch (ch)
            {
                case '"' when cur.Length == 0: quoted = true; any = true; break;
                case var d when d == delim: EndCell(); break;
                case '\r': if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); line++; rowLine = line; break;
                case '\n': EndRow(); line++; rowLine = line; break;
                default: cur.Append(ch); break;
            }
        }
        if (quoted) throw new ExchangeFormatException("CSV 파일의 따옴표가 닫히지 않았습니다.");
        EndRow();
        return rows;
    }
}
