using System.Globalization;

namespace DeskCal.Core.Calendars;

/// <summary>A date in the Korean lunisolar calendar. <see cref="Month"/> is the month number (1-12), not an index.</summary>
public readonly record struct LunarDate(int Year, int Month, int Day, bool IsLeapMonth)
{
    /// <summary>"9.29", leap months as "윤4.1".</summary>
    public string ToShortString() => $"{(IsLeapMonth ? "윤" : "")}{Month}.{Day}";
}

/// <summary>Conversion between solar and lunar dates, on top of <see cref="KoreanLunisolarCalendar"/> (918-2051).</summary>
public static class KoreanLunar
{
    private static readonly KoreanLunisolarCalendar Cal = new();

    // KoreanLunisolarCalendar reports years on the Gregorian scale (2026 -> 2026), not as Dangi years.
    private const int DangiOffset = 0;

    public static DateOnly MinDate => DateOnly.FromDateTime(Cal.MinSupportedDateTime);
    public static DateOnly MaxDate => DateOnly.FromDateTime(Cal.MaxSupportedDateTime);

    public static bool TryFromSolar(DateOnly date, out LunarDate lunar)
    {
        lunar = default;
        if (date < MinDate || date > MaxDate) return false;
        var dt = date.ToDateTime(TimeOnly.MinValue);
        int dangi = Cal.GetYear(dt);
        int index = Cal.GetMonth(dt);                         // 1..13, counts the leap month
        int leap = Cal.GetLeapMonth(dangi, Cal.GetEra(dt));  // index of the leap month, 0 if none
        bool isLeap = leap > 0 && index == leap;
        int number = leap > 0 && index >= leap ? index - 1 : index;
        lunar = new LunarDate(dangi - DangiOffset, number, Cal.GetDayOfMonth(dt), isLeap);
        return true;
    }

    /// <summary>Solar date of a lunar date, or null if it does not exist (e.g. day 30 of a 29-day month, or out of range).</summary>
    public static DateOnly? ToSolar(int lunarYear, int month, int day, bool leapMonth = false)
    {
        try
        {
            int dangi = lunarYear + DangiOffset;
            int leap = Cal.GetLeapMonth(dangi, 0);           // index of the leap month in this year, or 0
            if (leapMonth && (leap == 0 || leap - 1 != month)) return null;
            int index = leapMonth ? leap : (leap > 0 && month >= leap ? month + 1 : month);
            var dt = Cal.ToDateTime(dangi, index, day, 0, 0, 0, 0, 0);
            return DateOnly.FromDateTime(dt);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
