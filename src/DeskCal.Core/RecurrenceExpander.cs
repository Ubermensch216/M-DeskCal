using DeskCal.Core.Calendars;

namespace DeskCal.Core;

/// <summary>Turns a <see cref="RecurrenceRule"/> and a start date into occurrence dates (ascending, lazy).</summary>
public static class RecurrenceExpander
{
    /// <summary>Upper bound on candidate dates examined by <see cref="Between"/>, so a pathological rule cannot hang the UI.</summary>
    private const int MaxSteps = 400_000;

    private static readonly int[] EbbinghausOffsets = [0, 1, 2, 4, 7, 15, 30];

    /// <summary>
    /// All occurrences from <paramref name="start"/> on, honouring Count and Until. A non-repeating rule yields the
    /// start date once. <paramref name="isDayOff"/> is used by <see cref="RepeatKind.LegalWorkDay"/>.
    /// </summary>
    public static IEnumerable<DateOnly> Generate(RecurrenceRule rule, DateOnly start, Func<DateOnly, bool>? isDayOff = null)
    {
        int emitted = 0;
        foreach (var d in Raw(rule, start, isDayOff))
        {
            if (rule.Until is { } until && d > until) yield break;
            yield return d;
            if (rule.Count is { } count && ++emitted >= count) yield break;
        }
    }

    /// <summary>Occurrences within [from, to] inclusive.</summary>
    public static List<DateOnly> Between(RecurrenceRule rule, DateOnly start, DateOnly from, DateOnly to, Func<DateOnly, bool>? isDayOff = null)
    {
        var result = new List<DateOnly>();
        int steps = 0;
        foreach (var d in Generate(rule, start, isDayOff))
        {
            if (d > to || ++steps > MaxSteps) break;
            if (d >= from) result.Add(d);
        }
        return result;
    }

    /// <summary>How many occurrences fall strictly before <paramref name="date"/> (used to split a counted series).</summary>
    public static int CountBefore(RecurrenceRule rule, DateOnly start, DateOnly date, Func<DateOnly, bool>? isDayOff = null)
    {
        int n = 0, steps = 0;
        foreach (var d in Generate(rule, start, isDayOff))
        {
            if (d >= date || ++steps > MaxSteps) break;
            n++;
        }
        return n;
    }

    /// <summary>The last occurrence strictly before <paramref name="date"/>, or null if there is none.</summary>
    public static DateOnly? LastBefore(RecurrenceRule rule, DateOnly start, DateOnly date, Func<DateOnly, bool>? isDayOff = null)
    {
        DateOnly? last = null;
        int steps = 0;
        foreach (var d in Generate(rule, start, isDayOff))
        {
            if (d >= date || ++steps > MaxSteps) break;
            last = d;
        }
        return last;
    }

    // ---- per-kind generators (unbounded; Generate applies Count/Until) -----------------

    private static IEnumerable<DateOnly> Raw(RecurrenceRule rule, DateOnly start, Func<DateOnly, bool>? isDayOff)
    {
        int n = Math.Clamp(rule.Interval, 1, 999);
        switch (rule.Kind)
        {
            case RepeatKind.None:
                yield return start;
                yield break;

            case RepeatKind.Daily:
                for (var d = start; d <= DateOnly.MaxValue.AddDays(-n); d = d.AddDays(n)) yield return d;
                yield break;

            case RepeatKind.Weekday:
            case RepeatKind.LegalWorkDay:
                for (var d = start; d < DateOnly.MaxValue; d = d.AddDays(1))
                {
                    if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                    if (rule.Kind == RepeatKind.LegalWorkDay && isDayOff?.Invoke(d) == true) continue;
                    yield return d;
                }
                yield break;

            case RepeatKind.Weekly:
            {
                int mask = rule.WeekdaysMask & 0x7F;
                if (mask == 0) mask = 1 << (int)start.DayOfWeek;
                var week0 = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));        // Monday of the start week
                for (int w = 0; w < 100_000; w++)
                {
                    var ws = week0.AddDays(7 * w * n);
                    for (int k = 0; k < 7; k++)
                    {
                        var d = ws.AddDays(k);
                        if ((mask & (1 << (int)d.DayOfWeek)) != 0 && d >= start) yield return d;
                    }
                }
                yield break;
            }

            case RepeatKind.Monthly:
                for (int i = 0; i < 12_000; i++)
                {
                    var ym = new DateOnly(start.Year, start.Month, 1).AddMonths(i * n);
                    DateOnly d;
                    if (rule.Monthly == MonthlyMode.ByDate)
                        d = new DateOnly(ym.Year, ym.Month, Math.Min(start.Day, DateTime.DaysInMonth(ym.Year, ym.Month)));   // 31st -> last day
                    else
                        d = NthWeekday(ym.Year, ym.Month, start.DayOfWeek, rule.Nth != 0 ? rule.Nth : RecurrenceRule.NthOfStart(start));
                    if (d >= start) yield return d;
                }
                yield break;

            case RepeatKind.Yearly:
                for (int i = 0; start.Year + i * n <= 9998; i++)
                {
                    int y = start.Year + i * n;
                    yield return new DateOnly(y, start.Month, Math.Min(start.Day, DateTime.DaysInMonth(y, start.Month)));      // Feb 29 -> Feb 28
                }
                yield break;

            case RepeatKind.MonthLastDay:
                for (int i = 0; i < 12_000; i++)
                {
                    var ym = new DateOnly(start.Year, start.Month, 1).AddMonths(i * n);
                    var d = new DateOnly(ym.Year, ym.Month, DateTime.DaysInMonth(ym.Year, ym.Month));
                    if (d >= start) yield return d;
                }
                yield break;

            case RepeatKind.SeasonLastDay:
                for (int y = start.Year; y <= 9998; y++)
                    foreach (int m in new[] { 3, 6, 9, 12 })
                    {
                        var d = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
                        if (d >= start) yield return d;
                    }
                yield break;

            case RepeatKind.Ebbinghaus:
                foreach (int off in EbbinghausOffsets) yield return start.AddDays(off);
                yield break;

            case RepeatKind.LunarYearly:
            {
                if (!KoreanLunar.TryFromSolar(start, out var l))
                {
                    // outside the lunar table: fall back to the same solar date every year
                    foreach (var d in Raw(rule with { Kind = RepeatKind.Yearly }, start, isDayOff)) yield return d;
                    yield break;
                }
                DateOnly? previous = null;
                for (int y = l.Year; y <= 2051; y += n)
                {
                    var d = KoreanLunar.ToSolar(y, l.Month, l.Day, l.IsLeapMonth)
                         ?? KoreanLunar.ToSolar(y, l.Month, l.Day)                                  // no such leap month this year
                         ?? (l.Day == 30 ? KoreanLunar.ToSolar(y, l.Month, 29) : null);              // short month
                    if (d is { } day && day >= start && (previous is null || day > previous))
                    {
                        previous = day;
                        yield return day;
                    }
                }
                yield break;
            }
        }
    }

    /// <summary>The nth (1-4) or last (-1) given weekday of a month.</summary>
    internal static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int nth)
    {
        if (nth < 0)
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(((int)last.DayOfWeek - (int)dow + 7) % 7));
        }
        var first = new DateOnly(year, month, 1);
        var d = first.AddDays(((int)dow - (int)first.DayOfWeek + 7) % 7);
        return d.AddDays(7 * (nth - 1));
    }
}
