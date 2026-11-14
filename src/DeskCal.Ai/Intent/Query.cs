using System.Globalization;
using System.Text.RegularExpressions;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai.Intent;

/// <summary>A question about the calendar ("이번 주 일정 보여줘"): which days, and whether events, to-dos or both.</summary>
/// <param name="FreeTime">The question is "when am I free?": answered with free stretches instead of the things on the calendar.</param>
public sealed record QueryRequest(DateOnly From, DateOnly To, string Label, bool Events, bool Items, bool FreeTime = false);

/// <summary>One line of an answer.</summary>
public sealed record QueryRow(DateOnly Date, string Time, string Title, bool IsItem, bool Done);

/// <summary>
/// Tells a question from something to add, with rules only. The answer itself is never written by a model: it is read from the
/// calendar by <see cref="QueryService"/>, so it cannot contain anything that is not there. A sentence with a clock time and no
/// "show me" word ("내일 3시 회의 있어") is an event to add, not a question.
/// </summary>
public static class KoreanQueryParser
{
    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreateVerb = R(@"추가|등록|잡아|넣어|만들|예약해|저장");
    private static readonly Regex StrongAsk = R(@"보여|알려|뭐|무엇|뭘|조회|목록|리스트|\?|？|언제");
    private static readonly Regex FreeAsk = R(@"빈\s*시간|비는\s*시간|한가한|여유\s*시간|시간\s*(?:이\s*)?(?:나|있)");
    private static readonly Regex WeakAsk = R(@"있어|있나|있는|있니|있을까|확인");
    private static readonly Regex Thing = R(@"일정|할\s*일|할일|스케줄|약속|투두|todo");
    private static readonly Regex ClockTime = R(@"\d{1,2}\s*시|\d{1,2}:\d{2}");
    private static readonly Regex ItemsOnly = R(@"할\s*일|할일|투두|todo");
    private static readonly Regex EventsOnly = R(@"일정|스케줄|약속|회의");

    private static readonly Regex WeekRx = R(@"(?<w>다다음|다음|이번|지난|저번|담)\s*주(?!말)(?!\s*[월화수목금토일]요일)");
    private static readonly Regex WeekendRx = R(@"(?<w>이번|다음|담)\s*주말");
    private static readonly Regex MonthWordRx = R(@"(?<w>이번|다음|지난|저번|담)\s*(?:달|월)(?!\s*\d)");
    private static readonly Regex MonthNumRx = R(@"(?<![\d.])(?:(?<ry>내년|올해)\s*)?(?<m>\d{1,2})\s*월(?!\s*\d)(?!\s*(?:첫째|둘째|셋째|넷째|마지막))");

    public static QueryRequest? Parse(string? text, DateOnly today, DayOfWeek weekStart)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > KoreanEventParser.MaxChars) return null;
        if (CreateVerb.IsMatch(text)) return null;
        bool free = FreeAsk.IsMatch(text);
        bool strong = StrongAsk.IsMatch(text) || free;
        bool weak = WeakAsk.IsMatch(text) && Thing.IsMatch(text) && !ClockTime.IsMatch(text);
        if (!strong && !weak) return null;

        // "일정" in everyday speech covers the to-dos in the day cells as well, so both are listed unless only "할 일" was asked for
        bool itemsOnly = ItemsOnly.IsMatch(text) && !EventsOnly.IsMatch(text);
        bool items = true, events = !itemsOnly;
        var range = Range(text, today, weekStart);
        return range is null ? null : new QueryRequest(range.Value.From, range.Value.To, range.Value.Label, events, items, free);
    }

    private static (DateOnly From, DateOnly To, string Label)? Range(string text, DateOnly today, DayOfWeek weekStart)
    {
        if (WeekendRx.Match(text) is { Success: true } we)
        {
            var sat = DateResolver.InWeek(today, we.Groups["w"].Value == "이번" ? 0 : 1, DayOfWeek.Saturday, weekStart);
            return (sat, sat.AddDays(1), $"{Md(sat)} ~ {Md(sat.AddDays(1))} (주말)");
        }
        if (WeekRx.Match(text) is { Success: true } w)
        {
            int offset = w.Groups["w"].Value switch { "다다음" => 2, "다음" or "담" => 1, "이번" => 0, _ => -1 };
            var start = DateResolver.StartOfWeek(today, weekStart).AddDays(7 * offset);
            return (start, start.AddDays(6), $"{Md(start)} ~ {Md(start.AddDays(6))} ({w.Value.Trim()})");
        }
        if (MonthWordRx.Match(text) is { Success: true } mw)
        {
            int offset = mw.Groups["w"].Value switch { "다음" or "담" => 1, "이번" => 0, _ => -1 };
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(offset);
            return Month(first.Year, first.Month);
        }
        if (MonthNumRx.Match(text) is { Success: true } mn && int.Parse(mn.Groups["m"].Value) is >= 1 and <= 12 and var month)
        {
            int year = mn.Groups["ry"].Value == "내년" ? today.Year + 1 : today.Year;
            if (!mn.Groups["ry"].Success && month < today.Month) year++;                       // "3월" asked in November means the coming March
            return Month(year, month);
        }

        var parsed = KoreanEventParser.Parse(text, today, weekStart);
        if (parsed.Event.Date is { } day) return (day, day, $"{Md(day)} ({Dow[(int)day.DayOfWeek]})");
        return null;
    }

    private static (DateOnly, DateOnly, string) Month(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return (first, last, $"{year}년 {month}월");
    }

    private static readonly string[] Dow = ["일", "월", "화", "수", "목", "금", "토"];
    private static string Md(DateOnly d) => $"{d.Month}월 {d.Day}일";
}

/// <summary>Reads the answer to a <see cref="QueryRequest"/> from the calendar.</summary>
public sealed class QueryService(
    EventRepository events, ItemRepository items, Func<DateOnly, bool>? isDayOff = null, TimetableRepository? timetable = null, SettingsStore? settings = null)
{
    public const int MaxRows = 200, MaxFreeDays = 31, FreeFrom = 9 * 60, FreeTo = 18 * 60, FreeMinutes = 60;

    public IReadOnlyList<QueryRow> Run(QueryRequest q)
    {
        if (q.FreeTime) return FreeTime(q);
        var rows = new List<QueryRow>();
        if (q.Events)
            foreach (var o in EventOccurrences.Expand(events.GetAllActive(), events.GetExceptions(), q.From, q.To, isDayOff))
                rows.Add(new QueryRow(o.Date < q.From ? q.From : o.Date,
                    o.StartTime is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) + (o.EndTime is { } e ? "~" + e.ToString("HH:mm", CultureInfo.InvariantCulture) : "") : "종일",
                    o.Event.Title, false, false));
        if (q.Items)
            foreach (var i in items.GetRange(q.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), q.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                if (CountdownCalc.TryParseDate(i.Day, out var d)) rows.Add(new QueryRow(d, "할 일", i.Content, true, i.Done));
        return Order(rows);
    }

    /// <summary>
    /// The free stretches of 60 minutes or more between 09:00 and 18:00: timed events and timetable classes are busy, all-day events and
    /// to-dos are not. Over several days the weekends and holidays are left out (a single day is answered even if it is one).
    /// </summary>
    private IReadOnlyList<QueryRow> FreeTime(QueryRequest q)
    {
        var to = q.To.DayNumber - q.From.DayNumber >= MaxFreeDays ? q.From.AddDays(MaxFreeDays - 1) : q.To;
        var busy = FreeTimeFinder.BusyFromEvents(EventOccurrences.Expand(events.GetAllActive(), events.GetExceptions(), q.From, to, isDayOff));
        if (timetable is not null && settings is not null) busy.AddRange(FreeTimeFinder.BusyFromTimetable(timetable.LoadConfig(settings), timetable.GetCells(), q.From, to));
        bool several = q.From != to;
        var slots = FreeTimeFinder.Find(q.From, to, FreeFrom, FreeTo, FreeMinutes, busy,
            several ? d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || (isDayOff?.Invoke(d) ?? false) : null);
        return slots.Take(MaxRows).Select(s => new QueryRow(s.Date, $"{s.Start}~{s.End}", $"빈 시간 {Briefing.Duration(s.Minutes)}", false, false)).ToList();
    }

    private static IReadOnlyList<QueryRow> Order(List<QueryRow> rows)
    {
        return rows.OrderBy(r => r.Date).ThenBy(r => r.IsItem).ThenBy(r => r.Time == "종일" ? 0 : 1).ThenBy(r => r.Time, StringComparer.Ordinal).Take(MaxRows).ToList();
    }
}
