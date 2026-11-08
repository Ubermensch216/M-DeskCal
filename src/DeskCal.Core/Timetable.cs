namespace DeskCal.Core;

/// <summary>
/// How the day is cut into class periods. Start times are generated, not typed in: the first period starts at
/// <see cref="FirstStart"/>, each lasts <see cref="ClassMinutes"/>, with <see cref="BreakMinutes"/> between them and a
/// longer lunch break after period <see cref="LunchAfter"/> (0 = no lunch break).
/// </summary>
public sealed record TimetableConfig
{
    public int Periods { get; init; } = 7;
    public TimeOnly FirstStart { get; init; } = new(9, 0);
    public int ClassMinutes { get; init; } = 50;
    public int BreakMinutes { get; init; } = 10;
    public int LunchAfter { get; init; } = 4;
    public int LunchMinutes { get; init; } = 60;
    public bool ShowWeekend { get; init; }

    public TimetableConfig Sanitized() => this with
    {
        Periods = Math.Clamp(Periods, 1, 12),
        ClassMinutes = Math.Clamp(ClassMinutes, 10, 180),
        BreakMinutes = Math.Clamp(BreakMinutes, 0, 60),
        LunchAfter = Math.Clamp(LunchAfter, 0, Math.Clamp(Periods, 1, 12) - 1),
        LunchMinutes = Math.Clamp(LunchMinutes, 0, 180),
    };
}

public sealed record PeriodSlot(int Number, TimeOnly Start, TimeOnly End);

/// <summary>One filled square of the timetable.</summary>
public sealed record TimetableCell(DayOfWeek Day, int Period, string Subject, string Room, string? Color);

public static class TimetableSchedule
{
    /// <summary>The periods of a day. A day that would run past midnight is cut short.</summary>
    public static List<PeriodSlot> Slots(TimetableConfig config)
    {
        var c = config.Sanitized();
        var slots = new List<PeriodSlot>();
        int minute = c.FirstStart.Hour * 60 + c.FirstStart.Minute;
        for (int n = 1; n <= c.Periods; n++)
        {
            int end = minute + c.ClassMinutes;
            if (end >= 24 * 60) break;
            slots.Add(new PeriodSlot(n, At(minute), At(end)));
            minute = end + (n == c.LunchAfter ? c.LunchMinutes : c.BreakMinutes);
        }
        return slots;
    }

    /// <summary>The period being taught at <paramref name="now"/>, or null (before, between or after classes).</summary>
    public static int? Current(TimetableConfig config, TimeOnly now) =>
        Slots(config).FirstOrDefault(s => now >= s.Start && now < s.End)?.Number;

    /// <summary>The period that starts next (or is in progress), for highlighting "what is next".</summary>
    public static int? NextOrCurrent(TimetableConfig config, TimeOnly now) =>
        Slots(config).FirstOrDefault(s => now < s.End)?.Number;

    /// <summary>Columns to show: Monday-Friday, plus the weekend when asked for.</summary>
    public static DayOfWeek[] Days(TimetableConfig config) => config.ShowWeekend
        ? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday]
        : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static TimeOnly At(int minute) => new(minute / 60, minute % 60);
}
