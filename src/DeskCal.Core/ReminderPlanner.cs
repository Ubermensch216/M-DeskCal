using System.Globalization;

namespace DeskCal.Core;

/// <summary>What a reminder is attached to: an optionally repeating start date with an optional time of day.</summary>
public sealed record ReminderSubject(DateOnly Start, TimeOnly? StartTime, RecurrenceRule Rule, IReadOnlySet<DateOnly> Skips);

/// <summary>
/// Pure reminder arithmetic. A reminder fires <see cref="Reminder.OffsetMinutes"/> before an occurrence's start.
/// All-day occurrences and day-cell items have no start time, so they fire at the reminder's custom time
/// ("HH:mm", default 09:00) minus the offset - e.g. offset 1440 at 09:00 means "the day before at 09:00".
/// </summary>
public static class ReminderPlanner
{
    public static readonly TimeOnly DefaultAllDayTime = new(9, 0);
    private static readonly IReadOnlySet<DateOnly> NoSkips = new HashSet<DateOnly>();

    public static ReminderSubject Subject(EventEntry e, IReadOnlySet<DateOnly>? skips) =>
        EventTime.TryParse(e.Start, out var d, out var t)
            ? new ReminderSubject(d, t, RecurrenceRule.From(e), skips ?? NoSkips)
            : throw new ArgumentException("Event start is not a valid date/time.", nameof(e));

    public static bool TryParseClock(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static DateTime TriggerFor(DateOnly occurrenceDate, TimeOnly? startTime, Reminder r)
    {
        var clock = startTime ?? (TryParseClock(r.CustomTime, out var c) ? c : DefaultAllDayTime);
        return occurrenceDate.ToDateTime(clock).AddMinutes(-Math.Max(0, r.OffsetMinutes));
    }

    /// <summary>The first trigger strictly after <paramref name="after"/> (local time), or null if none within the horizon.</summary>
    public static DateTime? NextTrigger(ReminderSubject s, Reminder r, DateTime after,
                                        Func<DateOnly, bool>? isDayOff = null, int horizonDays = 800) =>
        Next(s, r, after, isDayOff, horizonDays)?.Trigger;

    /// <summary>Like <see cref="NextTrigger"/>, but also says which occurrence (its date) the trigger is for.</summary>
    public static (DateOnly Occurrence, DateTime Trigger)? Next(ReminderSubject s, Reminder r, DateTime after,
                                                                Func<DateOnly, bool>? isDayOff = null, int horizonDays = 800)
    {
        // An occurrence before after.Date starts before `after`, and a trigger is never later than its start.
        var from = DateOnly.FromDateTime(after);
        var to = from.AddDays(horizonDays);
        foreach (var date in RecurrenceExpander.Between(s.Rule, s.Start, from, to, isDayOff))
        {
            if (s.Skips.Contains(date)) continue;
            var t = TriggerFor(date, s.StartTime, r);
            if (t > after) return (date, t);
        }
        return null;
    }
}
