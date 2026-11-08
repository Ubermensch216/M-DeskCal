using System.Globalization;

namespace DeskCal.Core;

/// <summary>
/// Event start/end are wall-clock local times: "yyyy-MM-dd" for an all-day event, "yyyy-MM-ddTHH:mm" (seconds are
/// accepted) for a timed one. The <c>tz</c> column is reserved for future zone support.
/// </summary>
public static class EventTime
{
    private static readonly string[] Formats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"];

    public static bool TryParse(string? text, out DateOnly date, out TimeOnly? time)
    {
        date = default; time = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DateTime.TryParseExact(text.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return false;
        date = DateOnly.FromDateTime(dt);
        time = text.Contains('T') ? TimeOnly.FromDateTime(dt) : null;
        return true;
    }

    public static string Format(DateOnly date, TimeOnly? time) =>
        time is { } t ? $"{date:yyyy-MM-dd}T{t:HH:mm}" : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One concrete showing of an event on the calendar.</summary>
public sealed record EventOccurrence(EventEntry Event, DateOnly Date, DateOnly EndDate, TimeOnly? StartTime, TimeOnly? EndTime)
{
    public bool IsAllDay => StartTime is null;
    public bool IsMultiDay => EndDate > Date;
    public bool CoversDay(DateOnly d) => d >= Date && d <= EndDate;
}

public static class EventOccurrences
{
    /// <summary>
    /// Occurrences of the given events that touch [from, to] (inclusive), minus skipped dates, sorted by
    /// day, all-day first, then time and title. Deleted events and events with unreadable dates are ignored.
    /// </summary>
    public static List<EventOccurrence> Expand(
        IEnumerable<EventEntry> events,
        IReadOnlyDictionary<string, IReadOnlySet<DateOnly>>? skips,
        DateOnly from, DateOnly to,
        Func<DateOnly, bool>? isDayOff = null)
    {
        var result = new List<EventOccurrence>();
        foreach (var e in events)
        {
            if (e.Deleted || !EventTime.TryParse(e.Start, out var d0, out var t0)) continue;

            int spanDays = 0;
            TimeOnly? t1 = null;
            if (EventTime.TryParse(e.End, out var d1, out var endTime))
            {
                spanDays = Math.Max(0, d1.DayNumber - d0.DayNumber);
                t1 = t0 is null ? null : endTime;
                // an end before the start on the same day is meaningless: treat as no end time
                if (spanDays == 0 && t0 is { } a && t1 is { } b && b < a) t1 = null;
            }

            var rule = RecurrenceRule.From(e);
            skips ??= new Dictionary<string, IReadOnlySet<DateOnly>>();
            skips.TryGetValue(e.Id, out var skipped);

            foreach (var date in RecurrenceExpander.Between(rule, d0, from.AddDays(-spanDays), to, isDayOff))
            {
                if (skipped?.Contains(date) == true) continue;
                var end = date.AddDays(spanDays);
                if (end < from) continue;
                result.Add(new EventOccurrence(e, date, end, t0, t1));
            }
        }
        return result
            .OrderBy(o => o.Date)
            .ThenBy(o => o.StartTime is null ? 0 : 1)
            .ThenBy(o => o.StartTime)
            .ThenBy(o => o.Event.Title, StringComparer.CurrentCulture)
            .ToList();
    }
}
