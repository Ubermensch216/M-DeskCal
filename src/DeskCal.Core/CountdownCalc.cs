using System.Globalization;
using DeskCal.Core.Calendars;

namespace DeskCal.Core;

/// <param name="Label">Short text for a card: "D-12", "D-Day", "D+3" or "100일째".</param>
/// <param name="Target">The day being counted to (or from).</param>
/// <param name="Days">Target minus today (negative once it has passed).</param>
/// <param name="Detail">Optional second line: "만 30세", "5주년 D-40", or empty.</param>
/// <param name="Elapsed">True for "days since" style counters (memorials, passed one-off dates).</param>
public sealed record CountdownStatus(string Label, DateOnly Target, int Days, string Detail, bool Elapsed);

/// <summary>
/// D-day arithmetic. Types: Custom/Exam = one date (D-n, D-Day, D+n); Memorial = days since the date, day 1 = the date
/// itself (the Korean "100일" convention), plus the next anniversary; Birthday = next yearly occurrence with the age;
/// Festival = next yearly occurrence. With <see cref="Countdown.Lunar"/> the stored date is read as a lunar y-m-d.
/// </summary>
public static class CountdownCalc
{
    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static string Format(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The first problem with a title/date pair, or null when it is acceptable.</summary>
    public static string? Validate(string? title, string? date, bool lunar)
    {
        if (string.IsNullOrWhiteSpace(title)) return "제목을 입력하세요.";
        if (!TryParseDate(date, out var d)) return "날짜를 yyyy-MM-dd 형식으로 입력하세요. (예: 2027-03-15)";
        if (lunar && ToSolar(d) is null) return "음력 날짜가 올바르지 않거나 지원 범위(1900~2050년)를 벗어났습니다.";
        return null;
    }

    /// <summary>Null when the stored date cannot be interpreted (bad text, lunar date outside the table).</summary>
    public static CountdownStatus? Compute(Countdown cd, DateOnly today)
    {
        if (!TryParseDate(cd.Date, out var stored)) return null;
        var solar = cd.Lunar ? ToSolar(stored) : stored;
        if (solar is not { } date) return null;

        switch (cd.Type)
        {
            case CountdownType.Birthday or CountdownType.Festival when date <= today || cd.Type == CountdownType.Festival:
            {
                var next = NextYearly(stored, cd.Lunar, today);
                if (next is not { } n) return null;
                int days = n.DayNumber - today.DayNumber;
                string detail = "";
                if (cd.Type == CountdownType.Birthday)
                {
                    int year = cd.Lunar && KoreanLunar.TryFromSolar(n, out var l) ? l.Year : n.Year;
                    detail = $"만 {year - stored.Year}세";
                }
                return new CountdownStatus(Label(days), n, days, detail, false);
            }

            case CountdownType.Memorial when date <= today:
            {
                int since = today.DayNumber - date.DayNumber + 1;
                string detail = "";
                if (!cd.Lunar)
                {
                    var ann = NextYearly(stored, false, today);
                    if (ann == date) ann = NextYearly(stored, false, today.AddDays(1));   // the day itself is not an anniversary
                    if (ann is { } a) detail = $"{a.Year - date.Year}주년 {Label(a.DayNumber - today.DayNumber)}";
                }
                return new CountdownStatus($"{since}일째", date, since - 1, detail, true);
            }

            default:   // Custom, Exam, and Birthday/Memorial dates that are still in the future
            {
                int days = date.DayNumber - today.DayNumber;
                return new CountdownStatus(Label(days), date, days, "", days < 0);
            }
        }
    }

    /// <summary>
    /// Statuses in display order: upcoming ones first (nearest first), then the "since" counters.
    /// Countdowns that cannot be computed are left out.
    /// </summary>
    public static List<(Countdown Countdown, CountdownStatus Status)> Ordered(IEnumerable<Countdown> list, DateOnly today)
    {
        var result = new List<(Countdown, CountdownStatus)>();
        foreach (var cd in list)
            if (Compute(cd, today) is { } s) result.Add((cd, s));
        return result.OrderBy(x => x.Item2.Elapsed ? 1 : 0).ThenBy(x => Math.Abs(x.Item2.Days)).ThenBy(x => x.Item1.Title, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>One line for the strip under the calendar: "D-12 시험 · D-30 생일 (만 30세)".</summary>
    public static string Summary(Countdown cd, CountdownStatus s) =>
        s.Detail.Length == 0 ? $"{s.Label} {cd.Title}" : $"{s.Label} {cd.Title} ({s.Detail})";

    private static string Label(int days) => days switch { 0 => "D-Day", > 0 => $"D-{days}", _ => $"D+{-days}" };

    /// <summary>The first occurrence of the stored month/day on or after <paramref name="from"/>.</summary>
    private static DateOnly? NextYearly(DateOnly stored, bool lunar, DateOnly from)
    {
        if (lunar)
        {
            if (!KoreanLunar.TryFromSolar(from, out var l)) return null;
            for (int y = l.Year; y <= l.Year + 1; y++)
            {
                var d = KoreanLunar.ToSolar(y, stored.Month, stored.Day)
                     ?? (stored.Day == 30 ? KoreanLunar.ToSolar(y, stored.Month, 29) : null);
                if (d is { } day && day >= from) return day;
            }
            return null;
        }
        for (int y = from.Year; y <= from.Year + 1; y++)
        {
            // 29 February falls back to the last day of February in common years
            var d = new DateOnly(y, stored.Month, Math.Min(stored.Day, DateTime.DaysInMonth(y, stored.Month)));
            if (d >= from) return d;
        }
        return null;
    }

    private static DateOnly? ToSolar(DateOnly lunarYmd) => KoreanLunar.ToSolar(lunarYmd.Year, lunarYmd.Month, lunarYmd.Day);
}
