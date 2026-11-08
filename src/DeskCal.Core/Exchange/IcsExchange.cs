using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskCal.Core.Exchange;

/// <summary>
/// iCalendar (RFC 5545) so events can move to and from other calendar programs. Events become VEVENT (with RRULE, EXDATE,
/// VALARM), to-dos become VTODO. Repeat kinds that have no RFC form (quarter-end, legal work day, lunar yearly,
/// Ebbinghaus) are written as an explicit RDATE list up to a horizon and carry an X-DESKCAL-EXT property so this
/// program restores the original rule. Times are written "floating" (wall-clock, no zone), which is how they are stored.
/// Known simplification: this program pulls a "31st" or "29 February" repeat to the end of a short month, whereas other
/// programs skip that month.
/// </summary>
public static partial class IcsExchange
{
    private const string UidSuffix = "@m-deskcal";
    private const int MaxRdates = 1500;
    private static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    // ===================================================================== writing

    public static string Write(ExchangeBundle bundle, DateTime utcNow, Func<DateOnly, bool>? isDayOff = null, DateOnly? horizon = null)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//M-DeskCal//KO", "CALSCALE:GREGORIAN" };
        string stamp = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        foreach (var e in bundle.Events)
        {
            if (!EventTime.TryParse(e.Start, out var d0, out var t0)) continue;
            bool allDay = t0 is null;
            lines.Add("BEGIN:VEVENT");
            lines.Add("UID:" + e.Id + UidSuffix);
            lines.Add("DTSTAMP:" + stamp);
            lines.Add(allDay ? "DTSTART;VALUE=DATE:" + D(d0) : "DTSTART:" + DT(d0, t0!.Value));
            if (EventTime.TryParse(e.End, out var d1, out var t1))
                lines.Add(allDay ? "DTEND;VALUE=DATE:" + D(d1.AddDays(1)) : "DTEND:" + DT(d1, t1 ?? t0!.Value));
            else if (allDay) lines.Add("DTEND;VALUE=DATE:" + D(d0.AddDays(1)));
            lines.Add("SUMMARY:" + Esc(e.Title));
            if (e.Body.Length > 0) lines.Add("DESCRIPTION:" + Esc(e.Body));
            if (e.Color is not null) lines.Add("X-DESKCAL-COLOR:" + e.Color);

            var rule = RecurrenceRule.TryParse(e.RRule, e.ExtRule, out var r) ? r : RecurrenceRule.None;
            if (rule.IsRepeating)
            {
                string? rfc = e.RRule ?? MapExtToRfc(rule);
                var skipped = e.Exceptions.Select(x => CountdownCalc.TryParseDate(x, out var d) ? d : (DateOnly?)null).Where(d => d is not null).Select(d => d!.Value).ToHashSet();
                if (e.ExtRule is not null) lines.Add("X-DESKCAL-EXT:" + e.ExtRule);
                if (rfc is not null)
                    lines.Add("RRULE:" + (allDay ? rfc : UntilAsDateTime().Replace(rfc, "UNTIL=$1T235959")));
                else
                {
                    var dates = RecurrenceExpander.Generate(rule, d0, isDayOff).Skip(1)
                        .TakeWhile(d => d <= (horizon ?? d0.AddYears(3))).Where(d => !skipped.Contains(d)).Take(MaxRdates).ToList();
                    if (dates.Count > 0)
                        lines.Add(allDay ? "RDATE;VALUE=DATE:" + string.Join(",", dates.Select(D)) : "RDATE:" + string.Join(",", dates.Select(d => DT(d, t0!.Value))));
                }
                // Also written next to an RDATE list (where it matches nothing) so this program gets the skipped days back.
                foreach (var x in skipped.OrderBy(x => x)) lines.Add(allDay ? "EXDATE;VALUE=DATE:" + D(x) : "EXDATE:" + DT(x, t0!.Value));
            }

            foreach (var rem in e.Reminders) AddAlarm(lines, rem, allDay, d0);
            lines.Add("END:VEVENT");
        }

        foreach (var i in bundle.Items)
        {
            if (!CountdownCalc.TryParseDate(i.Day, out var day)) continue;
            lines.Add("BEGIN:VTODO");
            lines.Add("UID:" + i.Id + UidSuffix);
            lines.Add("DTSTAMP:" + stamp);
            lines.Add("DTSTART;VALUE=DATE:" + D(day));
            lines.Add("DUE;VALUE=DATE:" + D(day));
            lines.Add("SUMMARY:" + Esc(i.Content));
            lines.Add(i.Done ? "STATUS:COMPLETED" : "STATUS:NEEDS-ACTION");
            if (i.Done) lines.Add("PERCENT-COMPLETE:100");
            if (i.TextColor is not null) lines.Add("X-DESKCAL-COLOR:" + i.TextColor);
            if (i.BgColor is not null) lines.Add("X-DESKCAL-BGCOLOR:" + i.BgColor);
            foreach (var rem in i.Reminders) AddAlarm(lines, rem, allDay: true, day);
            lines.Add("END:VTODO");
        }

        lines.Add("END:VCALENDAR");
        var sb = new StringBuilder();
        foreach (var l in lines) foreach (var part in Fold(l)) sb.Append(part).Append("\r\n");
        return sb.ToString();
    }

    private static void AddAlarm(List<string> lines, ExReminder rem, bool allDay, DateOnly _)
    {
        int minutes;   // signed: positive = after the start
        if (allDay)
        {
            int clock = ReminderPlanner.TryParseClock(rem.CustomTime, out var c) ? c.Hour * 60 + c.Minute : 540;
            minutes = clock - rem.OffsetMinutes;
        }
        else minutes = -rem.OffsetMinutes;
        lines.Add("BEGIN:VALARM");
        lines.Add("ACTION:DISPLAY");
        lines.Add("DESCRIPTION:Reminder");
        lines.Add($"TRIGGER:{(minutes < 0 ? "-" : "")}PT{Math.Abs(minutes)}M");
        lines.Add("END:VALARM");
    }

    private static string? MapExtToRfc(RecurrenceRule rule)
    {
        string tail = (rule.Interval > 1 ? $";INTERVAL={rule.Interval}" : "")
                    + (rule.Count is { } c ? $";COUNT={c}" : "")
                    + (rule.Until is { } u ? ";UNTIL=" + u.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "");
        return rule.Kind switch
        {
            RepeatKind.Weekday => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR" + (rule.Count is { } c2 ? $";COUNT={c2}" : "") + (rule.Until is { } u2 ? ";UNTIL=" + u2.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : ""),
            RepeatKind.MonthLastDay => "FREQ=MONTHLY;BYMONTHDAY=-1" + tail,
            _ => null,
        };
    }

    [GeneratedRegex(@"UNTIL=(\d{8})")]
    private static partial Regex UntilAsDateTime();

    private static string D(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string DT(DateOnly d, TimeOnly t) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "T" + t.ToString("HHmmss", CultureInfo.InvariantCulture);

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>Content lines are limited to 75 octets; longer ones continue on the next line after one space. Never splits a UTF-8 character.</summary>
    internal static IEnumerable<string> Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) { yield return line; yield break; }
        var sb = new StringBuilder();
        int bytes = 0, limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > limit)
            {
                yield return sb.ToString();
                sb.Clear(); sb.Append(' ');
                bytes = 1; limit = 75;
            }
            sb.Append(rune.ToString());
            bytes += n;
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // ===================================================================== reading

    private sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    private sealed class Comp(string name)
    {
        public string Name { get; } = name;
        public List<Prop> Props { get; } = [];
        public List<Comp> Children { get; } = [];
        public Prop? Get(string n) => Props.FirstOrDefault(p => p.Name == n);
    }

    /// <exception cref="ExchangeFormatException">Not an iCalendar file.</exception>
    public static ExchangeReadResult Read(string text)
    {
        var root = ParseComponents(text);
        var cal = root.Children.FirstOrDefault(c => c.Name == "VCALENDAR")
            ?? throw new ExchangeFormatException("iCalendar(.ics) 파일이 아닙니다. BEGIN:VCALENDAR 를 찾을 수 없습니다.");

        var warnings = new List<string>();
        var ctx = new Context(warnings);
        var events = new List<ExEvent>();
        var items = new List<ExItem>();

        foreach (var c in cal.Children)
        {
            if (c.Name == "VEVENT") { if (ReadEvent(c, ctx) is { } e) events.Add(e); }
            else if (c.Name == "VTODO") { if (ReadTodo(c, ctx) is { } i) items.Add(i); }
        }
        if (ctx.UnknownZones.Count > 0)
            warnings.Add("알 수 없는 시간대(" + string.Join(", ", ctx.UnknownZones) + ")의 시각은 변환하지 않고 그대로 가져옴");
        return ExchangeCleaner.Clean(new ExchangeBundle(items, events, [], []), warnings);
    }

    private sealed class Context(List<string> warnings)
    {
        public List<string> Warnings { get; } = warnings;
        public HashSet<string> UnknownZones { get; } = [];
    }

    private static Comp ParseComponents(string text)
    {
        // unfold: a line starting with space/tab continues the previous one
        var logical = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && logical.Count > 0) logical[^1] += raw[1..];
            else if (raw.Length > 0) logical.Add(raw);
        }

        var root = new Comp("ROOT");
        var stack = new Stack<Comp>();
        stack.Push(root);
        foreach (var line in logical)
        {
            if (!TryProp(line, out var prop)) continue;
            if (prop.Name == "BEGIN") { var c = new Comp(prop.Value.Trim().ToUpperInvariant()); stack.Peek().Children.Add(c); stack.Push(c); }
            else if (prop.Name == "END") { if (stack.Count > 1) stack.Pop(); }
            else stack.Peek().Props.Add(prop);
        }
        return root;
    }

    private static bool TryProp(string line, out Prop prop)
    {
        prop = null!;
        // the name/parameters end at the first ':' that is not inside quotes
        bool quoted = false; int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return false;

        var head = SplitOutsideQuotes(line[..colon], ';');
        var pars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) pars[p[..eq].Trim()] = p[(eq + 1)..].Trim().Trim('"');
        }
        prop = new Prop(head[0].Trim().ToUpperInvariant(), pars, line[(colon + 1)..]);
        return true;
    }

    private static List<string> SplitOutsideQuotes(string s, char sep)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        foreach (char ch in s)
        {
            if (ch == '"') q = !q;
            if (ch == sep && !q) { parts.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        parts.Add(cur.ToString());
        return parts;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static (DateOnly Date, TimeOnly? Time)? ParseDt(Prop p, Context ctx)
    {
        string v = p.Value.Trim();
        if (v.Length >= 8 && DateOnly.TryParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            if (v.Length == 8 || p.Params.GetValueOrDefault("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true) return (date, null);
            if (v.Length >= 15 && v[8] == 'T' && DateTime.TryParseExact(v[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                if (v.EndsWith('Z')) dt = TimeZoneInfo.ConvertTimeFromUtc(dt, TimeZoneInfo.Local);
                else if (p.Params.TryGetValue("TZID", out var tzid))
                {
                    try { dt = TimeZoneInfo.ConvertTime(dt, TimeZoneInfo.FindSystemTimeZoneById(tzid), TimeZoneInfo.Local); }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { ctx.UnknownZones.Add(tzid); }
                }
                return (DateOnly.FromDateTime(dt), TimeOnly.FromDateTime(dt));
            }
        }
        return null;
    }

    [GeneratedRegex(@"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    private static TimeSpan? ParseDuration(string v)
    {
        var m = DurationPattern().Match(v.Trim());
        if (!m.Success || v.Trim().TrimStart('+', '-').Length <= 1) return null;
        long G(int i) => m.Groups[i].Success ? long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        if (G(2) > 5000 || G(3) > 36500 || G(4) > 1_000_000 || G(5) > 100_000_000 || G(6) > 1_000_000_000) return null;
        var ts = TimeSpan.FromDays(G(2) * 7 + G(3)) + TimeSpan.FromHours(G(4)) + TimeSpan.FromMinutes(G(5)) + TimeSpan.FromSeconds(G(6));
        return m.Groups[1].Value == "-" ? -ts : ts;
    }

    private static string Id(string? uid, string fallbackKey)
    {
        if (uid is not null)
        {
            var m = OwnUid().Match(uid.Trim());
            if (m.Success) return m.Groups[1].Value;
        }
        return ExchangeIds.Stable("ics-", uid ?? fallbackKey);
    }

    [GeneratedRegex(@"^([0-9A-Za-z_-]{1,64})@m-deskcal$")]
    private static partial Regex OwnUid();

    private static ExEvent? ReadEvent(Comp c, Context ctx)
    {
        string title = Unescape(c.Get("SUMMARY")?.Value ?? "").Trim();
        string label = title.Length > 0 ? $"'{(title.Length > 30 ? title[..30] + "…" : title)}'" : "(제목 없음)";

        if (string.Equals(c.Get("STATUS")?.Value.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase)) { ctx.Warnings.Add($"일정 {label}: 취소된 일정이라 건너뜀"); return null; }
        if (c.Get("DTSTART") is not { } ds || ParseDt(ds, ctx) is not { } start) { ctx.Warnings.Add($"일정 {label}: DTSTART 를 읽을 수 없어 건너뜀"); return null; }
        var (d0, t0) = start;
        bool allDay = t0 is null;

        // end
        string? end = null;
        if (c.Get("DTEND") is { } de && ParseDt(de, ctx) is { } e1)
        {
            if (allDay && e1.Time is null) { var last = e1.Date.AddDays(-1); if (last > d0) end = EventTime.Format(last, null); }
            else if (!allDay && e1.Time is { } et) end = EventTime.Format(e1.Date, et);
        }
        else if (c.Get("DURATION") is { } du && ParseDuration(du.Value) is { } dur && dur > TimeSpan.Zero)
        {
            if (allDay) { var last = d0.AddDays((int)Math.Ceiling(dur.TotalDays) - 1); if (last > d0) end = EventTime.Format(last, null); }
            else { var dt = d0.ToDateTime(t0!.Value).Add(dur); end = EventTime.Format(DateOnly.FromDateTime(dt), TimeOnly.FromDateTime(dt)); }
        }

        // repeat rule
        string? rrule = null, ext = null;
        if (c.Get("X-DESKCAL-EXT") is { } x && RecurrenceRule.TryParse(null, x.Value.Trim(), out _)) ext = x.Value.Trim();
        else if (c.Get("RRULE") is { } rr)
        {
            if (!Normalize(rr.Value, d0, out rrule, out ext))
            {
                ctx.Warnings.Add($"일정 {label}: 이 프로그램이 지원하지 않는 반복 규칙이라 한 번만 발생하는 일정으로 가져옴 (RRULE:{rr.Value})");
                rrule = ext = null;
            }
        }
        else if (c.Get("RDATE") is not null)
            ctx.Warnings.Add($"일정 {label}: RDATE(날짜 목록 반복)는 지원하지 않아 첫 날짜만 가져옴");

        var exceptions = new List<string>();
        if (rrule is not null || ext is not null)
            foreach (var p in c.Props.Where(p => p.Name == "EXDATE"))
                foreach (var v in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (ParseDt(p with { Value = v }, ctx) is { } ex) exceptions.Add(CountdownCalc.Format(ex.Date));

        string body = Unescape(c.Get("DESCRIPTION")?.Value ?? "");
        if (c.Get("LOCATION") is { } loc && Unescape(loc.Value).Trim() is { Length: > 0 } place)
            body = body.Length > 0 ? $"장소: {place}\n{body}" : $"장소: {place}";

        string uid = c.Get("UID")?.Value ?? "";
        string key = c.Get("RECURRENCE-ID") is { } rid ? uid + "|" + rid.Value : uid;
        if (c.Get("RECURRENCE-ID") is not null) ctx.Warnings.Add($"일정 {label}: 반복 일정의 개별 수정본은 별도의 단일 일정으로 가져옴");

        return new ExEvent(Id(key.Length > 0 ? key : null, EventTime.Format(d0, t0) + "|" + title), title, body, EventTime.Format(d0, t0), end, allDay,
            rrule, ext, ExchangeIds.CleanColor(c.Get("X-DESKCAL-COLOR")?.Value), exceptions, Alarms(c, allDay, label, ctx));
    }

    private static ExItem? ReadTodo(Comp c, Context ctx)
    {
        string title = Unescape(c.Get("SUMMARY")?.Value ?? "").Trim();
        string label = title.Length > 0 ? $"'{(title.Length > 30 ? title[..30] + "…" : title)}'" : "(제목 없음)";
        if (string.Equals(c.Get("STATUS")?.Value.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase)) { ctx.Warnings.Add($"할 일 {label}: 취소됨 — 건너뜀"); return null; }

        var when = (c.Get("DUE") is { } due ? ParseDt(due, ctx) : null) ?? (c.Get("DTSTART") is { } st ? ParseDt(st, ctx) : null);
        if (when is not { } w) { ctx.Warnings.Add($"할 일 {label}: 날짜(DUE/DTSTART)가 없어 건너뜀"); return null; }

        bool done = string.Equals(c.Get("STATUS")?.Value.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase)
                    || (int.TryParse(c.Get("PERCENT-COMPLETE")?.Value, out int pc) && pc >= 100);
        string uid = c.Get("UID")?.Value ?? "";
        return new ExItem(Id(uid.Length > 0 ? uid : null, CountdownCalc.Format(w.Date) + "|" + title), CountdownCalc.Format(w.Date), title, done,
            ExchangeIds.CleanColor(c.Get("X-DESKCAL-BGCOLOR")?.Value), ExchangeIds.CleanColor(c.Get("X-DESKCAL-COLOR")?.Value), 0,
            Alarms(c, allDay: true, label, ctx));
    }

    private static List<ExReminder> Alarms(Comp c, bool allDay, string label, Context ctx)
    {
        var list = new List<ExReminder>();
        foreach (var a in c.Children.Where(x => x.Name == "VALARM"))
        {
            if (a.Get("TRIGGER") is not { } t) continue;
            if (t.Params.GetValueOrDefault("VALUE")?.Equals("DATE-TIME", StringComparison.OrdinalIgnoreCase) == true || ParseDuration(t.Value) is not { } span)
            {
                ctx.Warnings.Add($"{label}: 지원하지 않는 알림 형식은 무시함");
                continue;
            }
            int signed = (int)Math.Round(span.TotalMinutes);                  // negative = before the start
            if (!allDay)
            {
                if (signed <= 0) list.Add(new ExReminder(-signed, null));
                else ctx.Warnings.Add($"{label}: 시작 이후에 울리는 알림은 지원하지 않아 무시함");
                continue;
            }
            int before = -signed;                                              // minutes before 00:00 of the day
            if (before >= -540) list.Add(new ExReminder(before + 540, null));  // relative to the default 09:00
            else if (before > -1440) list.Add(new ExReminder(0, TimeSpan.FromMinutes(-before).ToString(@"hh\:mm", CultureInfo.InvariantCulture)));
            else ctx.Warnings.Add($"{label}: 당일 이후에 울리는 알림은 지원하지 않아 무시함");
        }
        return list;
    }

    /// <summary>
    /// Brings a foreign RRULE into what <see cref="RecurrenceRule"/> understands: drops WKST and the BYMONTHDAY / BYMONTH
    /// values that merely repeat the start date, and maps "last day of month". Anything else returns false.
    /// </summary>
    private static bool Normalize(string raw, DateOnly start, out string? rrule, out string? ext)
    {
        rrule = ext = null;
        var parts = new List<(string Key, string Value)>();
        foreach (var p in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = p.IndexOf('=');
            if (eq <= 0) return false;
            parts.Add((p[..eq].ToUpperInvariant(), p[(eq + 1)..]));
        }
        string freq = parts.FirstOrDefault(p => p.Key == "FREQ").Value?.ToUpperInvariant() ?? "";

        bool lastDay = false;
        var kept = new List<string>();
        foreach (var (k, v) in parts)
        {
            if (k == "WKST") continue;
            if (k == "BYDAY")
            {
                // Weekly: any set of weekdays. Monthly: exactly "the nth <weekday of the start date>". Nothing else is representable.
                var days = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (freq == "MONTHLY")
                {
                    if (days.Length != 1 || days[0].Length < 3 || Array.IndexOf(DayNames, days[0][^2..].ToUpperInvariant()) != (int)start.DayOfWeek) return false;
                }
                else if (freq != "WEEKLY" || days.Any(d => d.Length != 2)) return false;
            }
            if (k == "BYMONTHDAY" && freq == "MONTHLY")
            {
                if (v == "-1") { lastDay = true; continue; }
                if (int.TryParse(v, out int n) && n == start.Day) continue;
            }
            if (k == "BYMONTH" && freq == "YEARLY" && int.TryParse(v, out int m) && m == start.Month) continue;
            if (k == "BYMONTHDAY" && freq == "YEARLY" && int.TryParse(v, out int n2) && n2 == start.Day) continue;
            kept.Add($"{k}={v}");
        }

        string text = string.Join(";", kept);
        if (!RecurrenceRule.TryParse(text, null, out var rule)) return false;
        if (lastDay)
        {
            // "FREQ=MONTHLY;BYMONTHDAY=-1" is the app's month-end rule; anything more specific is not supported
            if (rule.Kind != RepeatKind.Monthly || rule.Monthly != MonthlyMode.ByDate) return false;
            (_, ext) = (rule with { Kind = RepeatKind.MonthLastDay }).ToColumns(start);
            return ext is not null;
        }
        rrule = text;
        return true;
    }
}
