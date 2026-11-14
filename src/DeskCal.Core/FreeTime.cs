namespace DeskCal.Core;

/// <summary>A stretch of one day (minutes since midnight, end exclusive) that is taken.</summary>
public readonly record struct BusyInterval(DateOnly Date, int StartMinute, int EndMinute);

/// <summary>A gap in a day that is long enough to use (minutes since midnight; the end of the day is minute 1440, shown "24:00").</summary>
public sealed record FreeSlot(DateOnly Date, int StartMinute, int EndMinute)
{
    public int Minutes => EndMinute - StartMinute;
    public string Start => Text(StartMinute);
    public string End => Text(EndMinute);

    private static string Text(int minute) => $"{minute / 60:00}:{minute % 60:00}";
}

/// <summary>
/// "When am I free?" as plain arithmetic, so that an AI client never has to do it in its head. Timed events and filled
/// timetable squares are busy; all-day events and to-do items are not (they have no time of day).
/// </summary>
public static class FreeTimeFinder
{
    /// <summary>Length assumed for a timed event that has no end time.</summary>
    public const int DefaultEventMinutes = 60;

    public const int DayMinutes = 24 * 60;

    /// <summary>Busy stretches of the timed occurrences, cut per day (an event running past midnight is split).</summary>
    public static List<BusyInterval> BusyFromEvents(IEnumerable<EventOccurrence> occurrences)
    {
        var busy = new List<BusyInterval>();
        foreach (var o in occurrences)
        {
            if (o.StartTime is not { } start) continue;                      // all-day: no time of day
            int s = Minute(start);
            if (!o.IsMultiDay)
            {
                int e = o.EndTime is { } et ? Minute(et) : Math.Min(DayMinutes, s + DefaultEventMinutes);
                if (e > s) busy.Add(new BusyInterval(o.Date, s, e));
                continue;
            }
            busy.Add(new BusyInterval(o.Date, s, DayMinutes));
            for (var d = o.Date.AddDays(1); d < o.EndDate; d = d.AddDays(1)) busy.Add(new BusyInterval(d, 0, DayMinutes));
            if (o.EndTime is { } last && Minute(last) > 0) busy.Add(new BusyInterval(o.EndDate, 0, Minute(last)));
        }
        return busy;
    }

    /// <summary>The stretch of <paramref name="day"/> that a timed occurrence takes, or null (all-day, or it does not touch that day).</summary>
    public static BusyInterval? IntervalOn(EventOccurrence o, DateOnly day)
    {
        if (o.StartTime is not { } start || !o.CoversDay(day)) return null;
        int s = Minute(start);
        if (!o.IsMultiDay)
        {
            int e = o.EndTime is { } et ? Minute(et) : Math.Min(DayMinutes, s + DefaultEventMinutes);
            return e > s ? new BusyInterval(day, s, e) : null;
        }
        if (day == o.Date) return new BusyInterval(day, s, DayMinutes);
        if (day < o.EndDate) return new BusyInterval(day, 0, DayMinutes);
        return o.EndTime is { } last && Minute(last) > 0 ? new BusyInterval(day, 0, Minute(last)) : null;
    }

    /// <summary>The periods of the filled timetable squares on every day of the range (a square repeats every week).</summary>
    public static List<BusyInterval> BusyFromTimetable(TimetableConfig config, IEnumerable<TimetableCell> cells, DateOnly from, DateOnly to)
    {
        var slots = TimetableSchedule.Slots(config).ToDictionary(s => s.Number);
        var byDay = cells.Where(c => c.Subject.Length > 0 || c.Room.Length > 0)
                         .Where(c => slots.ContainsKey(c.Period))
                         .GroupBy(c => c.Day).ToDictionary(g => g.Key, g => g.ToList());
        var busy = new List<BusyInterval>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (!byDay.TryGetValue(d.DayOfWeek, out var list)) continue;
            foreach (var c in list)
            {
                var slot = slots[c.Period];
                busy.Add(new BusyInterval(d, Minute(slot.Start), Minute(slot.End)));
            }
        }
        return busy;
    }

    /// <summary>
    /// Gaps of at least <paramref name="minMinutes"/> inside [windowStart, windowEnd) on each day of the range.
    /// Touching or overlapping busy stretches are merged first. <paramref name="skipDay"/> leaves whole days out.
    /// </summary>
    public static List<FreeSlot> Find(DateOnly from, DateOnly to, int windowStartMinute, int windowEndMinute, int minMinutes,
                                      IEnumerable<BusyInterval> busy, Func<DateOnly, bool>? skipDay = null)
    {
        windowStartMinute = Math.Clamp(windowStartMinute, 0, DayMinutes);
        windowEndMinute = Math.Clamp(windowEndMinute, 0, DayMinutes);
        minMinutes = Math.Max(1, minMinutes);
        var result = new List<FreeSlot>();
        if (windowEndMinute <= windowStartMinute || to < from) return result;

        var perDay = busy.GroupBy(b => b.Date).ToDictionary(g => g.Key, g => g.OrderBy(b => b.StartMinute).ToList());
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (skipDay?.Invoke(d) == true) continue;
            int cursor = windowStartMinute;
            if (perDay.TryGetValue(d, out var list))
            {
                foreach (var b in list)
                {
                    if (b.EndMinute <= cursor) continue;                      // before the window or inside what is already covered
                    if (b.StartMinute >= windowEndMinute) break;
                    if (b.StartMinute - cursor >= minMinutes) result.Add(Slot(d, cursor, b.StartMinute));
                    cursor = Math.Max(cursor, b.EndMinute);
                    if (cursor >= windowEndMinute) break;
                }
            }
            if (windowEndMinute - cursor >= minMinutes) result.Add(Slot(d, cursor, windowEndMinute));
        }
        return result;
    }

    /// <summary>"HH:mm" (also "24:00" for the end of the day) to minutes since midnight.</summary>
    public static bool TryParseMinute(string? text, out int minute)
    {
        minute = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text == "24:00") { minute = DayMinutes; return true; }
        if (!TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)) return false;
        minute = Minute(t);
        return true;
    }

    /// <summary>Minutes since midnight as "HH:mm" (1440 = "24:00").</summary>
    public static string Format(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private static int Minute(TimeOnly t) => t.Hour * 60 + t.Minute;

    private static FreeSlot Slot(DateOnly d, int from, int to) => new(d, from, to);
}
