namespace DeskCal.Core.Calendars;

/// <summary>Everything the board can annotate a day with.</summary>
public sealed record DayInfo(DateOnly Date, LunarDate? Lunar, SolarTerm? Term, IReadOnlyList<Holiday> Holidays)
{
    /// <summary>True for statutory, substitute and temporary holidays (the date is drawn in the holiday colour).</summary>
    public bool IsDayOff => Holidays.Any(h => h.IsDayOff);

    /// <summary>The day-off holiday names, e.g. "설날", "삼일절 대체공휴일".</summary>
    public string? HolidayName => Holidays.FirstOrDefault(h => h.IsDayOff)?.Name;

    /// <summary>The name of a memorial/traditional day that is not a day off.</summary>
    public string? FestivalName => Holidays.FirstOrDefault(h => !h.IsDayOff)?.Name;
}

public sealed class KoreanDays(IEnumerable<HolidayOverride>? overrides = null)
{
    private readonly KoreanHolidays _holidays = new(overrides);

    public DayInfo Get(DateOnly date)
    {
        LunarDate? lunar = KoreanLunar.TryFromSolar(date, out var l) ? l : null;
        return new DayInfo(date, lunar, SolarTerms.OnDate(date), _holidays.On(date));
    }

    public KoreanHolidays Holidays => _holidays;
}
