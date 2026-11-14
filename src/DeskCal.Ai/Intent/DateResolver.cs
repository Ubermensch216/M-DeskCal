using DeskCal.Core.Calendars;

namespace DeskCal.Ai.Intent;

/// <summary>
/// Turns "next Tuesday", "3 December", "the 8th lunar month" into real dates. This is plain calendar arithmetic and the only
/// place dates are worked out: the rule-based parser uses it directly and the language-model path only ever hands it relative
/// words ("week +1, Tuesday"), because a small model's own date arithmetic cannot be trusted.
/// </summary>
public static class DateResolver
{
    public static DateOnly StartOfWeek(DateOnly day, DayOfWeek weekStart) =>
        day.AddDays(-(((int)day.DayOfWeek - (int)weekStart + 7) % 7));

    /// <summary>The given weekday of the week <paramref name="weekOffset"/> weeks from today's week (0 = this week, 1 = next, -1 = last).</summary>
    public static DateOnly InWeek(DateOnly today, int weekOffset, DayOfWeek weekday, DayOfWeek weekStart) =>
        StartOfWeek(today, weekStart).AddDays(7 * weekOffset + ((int)weekday - (int)weekStart + 7) % 7);

    /// <summary>The first date on or after <paramref name="from"/> that falls on the weekday.</summary>
    public static DateOnly OnOrAfter(DateOnly from, DayOfWeek weekday) => from.AddDays(((int)weekday - (int)from.DayOfWeek + 7) % 7);

    /// <summary>
    /// A month and day with no year: this year if it is not over yet, otherwise next year (<paramref name="yearInferred"/> says that
    /// it moved). With a year the date is taken as written. False when the date does not exist (30 February).
    /// </summary>
    public static bool TryMonthDay(DateOnly today, int? year, int month, int day, out DateOnly date, out bool movedToNextYear)
    {
        date = default; movedToNextYear = false;
        if (month is < 1 or > 12 || day < 1) return false;
        if (year is { } y) return Make(y, month, day, out date);
        for (int k = 0; k <= 8; k++)                                                  // 29 February needs up to eight years
        {
            if (!Make(today.Year + k, month, day, out var d)) continue;
            if (d < today) continue;
            date = d; movedToNextYear = k > 0;
            return true;
        }
        return false;
    }

    private static bool Make(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>The next solar date of a lunar month/day (on or after today), or of that lunar year when the year is given. Null when it does not exist.</summary>
    public static DateOnly? LunarToSolar(DateOnly today, int? lunarYear, int month, int day, bool leap)
    {
        if (lunarYear is { } y) return KoreanLunar.ToSolar(y, month, day, leap);
        if (!KoreanLunar.TryFromSolar(today, out var now)) return null;
        for (int ly = now.Year; ly <= now.Year + 3; ly++)
            if (KoreanLunar.ToSolar(ly, month, day, leap) is { } d && d >= today) return d;
        return null;
    }

    public static DateOnly EndOfMonthOnOrAfter(DateOnly from)
    {
        var end = new DateOnly(from.Year, from.Month, DateTime.DaysInMonth(from.Year, from.Month));
        return end >= from ? end : EndOfMonthOnOrAfter(from.AddDays(1 - from.Day).AddMonths(1));
    }

    /// <summary>The last day of March, June, September or December that is on or after <paramref name="from"/>.</summary>
    public static DateOnly QuarterEndOnOrAfter(DateOnly from)
    {
        var d = EndOfMonthOnOrAfter(from);
        while (d.Month % 3 != 0) d = EndOfMonthOnOrAfter(d.AddDays(1));
        return d;
    }

    /// <summary>The first date on or after <paramref name="from"/> that is the n-th (1-4) or last (-1) given weekday of its month.</summary>
    public static DateOnly NthWeekdayOnOrAfter(DateOnly from, DayOfWeek weekday, int nth)
    {
        for (var d = OnOrAfter(from, weekday); ; d = d.AddDays(7))
        {
            bool last = d.Day + 7 > DateTime.DaysInMonth(d.Year, d.Month);
            if (nth == -1 ? last : (d.Day - 1) / 7 + 1 == nth) return d;
        }
    }

    /// <summary>The n-th (1-4) or last (-1) given weekday of a month ("the first Friday of December").</summary>
    public static DateOnly NthWeekdayOfMonth(int year, int month, DayOfWeek weekday, int nth)
    {
        var first = new DateOnly(year, month, 1);
        if (nth == -1)
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(((int)last.DayOfWeek - (int)weekday + 7) % 7));
        }
        return OnOrAfter(first, weekday).AddDays(7 * (Math.Clamp(nth, 1, 4) - 1));
    }

    /// <summary>The first date on or after <paramref name="from"/> whose day of month is <paramref name="day"/>; in a shorter month, its last day.</summary>
    public static DateOnly DayOfMonthOnOrAfter(DateOnly from, int day)
    {
        for (var first = from.AddDays(1 - from.Day); ; first = first.AddMonths(1))
        {
            var d = new DateOnly(first.Year, first.Month, Math.Min(day, DateTime.DaysInMonth(first.Year, first.Month)));
            if (d >= from) return d;
        }
    }
}
