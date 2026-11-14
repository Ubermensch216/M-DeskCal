using DeskCal.Ai.Intent;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai;

/// <summary>
/// Looks at the calendar for events that clash with a proposed one (the same event, or one that overlaps in time). For a repeating
/// event only its first day is checked. Needs no AI, so it also protects what the model suggests.
/// </summary>
public sealed class ConflictService(EventRepository events, Func<DateOnly, bool>? isDayOff = null)
{
    public IReadOnlyList<EventConflict> Check(ParsedEvent e) =>
        e.Date is { } date ? Check(date, e.Title, e.Start, e.End) : [];

    /// <param name="excludeEventId">An event being edited does not clash with itself.</param>
    public IReadOnlyList<EventConflict> Check(DateOnly date, string title, TimeOnly? start, TimeOnly? end, string? excludeEventId = null)
    {
        var sameDay = EventOccurrences.Expand(events.GetAllActive(), events.GetExceptions(), date, date, isDayOff)
            .Where(o => excludeEventId is null || o.Event.Id != excludeEventId);
        return EventConflicts.Find(sameDay, date, title, start, end);
    }
}
