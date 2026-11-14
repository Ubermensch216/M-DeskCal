namespace DeskCal.Core;

/// <param name="DaysLate">How many days ago the item's day was.</param>
/// <param name="TimesMoved">How often it has already been moved to a later day (from the activity log).</param>
public sealed record OverdueItem(string Id, string Day, string Content, int DaysLate, int TimesMoved);

/// <summary>An overdue item and the day it is proposed to go to.</summary>
public sealed record RolloverMove(OverdueItem Item, DateOnly To);

/// <summary>
/// Unfinished to-dos on past days, and where to put them. Everything here is plain rules (no model): which items count as overdue,
/// which day is "next", and a spread over the coming working days that leaves room on the busy ones. The user sees the result and
/// changes it before anything moves.
/// </summary>
public static class RolloverPlanner
{
    /// <summary>Only the last this many days are looked at; anything older is history, not a backlog.</summary>
    public const int LookbackDays = 30;

    /// <summary>An item moved this many times or more is flagged: it may be better split up or dropped than pushed again.</summary>
    public const int ManyMoves = 3;

    /// <summary>
    /// Not done, not deleted, on a day before <paramref name="today"/> (within the look-back). Items of a repeating series and items
    /// tied to a D-day are left out: a series makes its own, and moving a D-day item would move the D-day. Oldest first.
    /// </summary>
    public static IReadOnlyList<OverdueItem> FindOverdue(IEnumerable<Item> items, DateOnly today, IReadOnlyDictionary<string, int>? timesMoved = null)
    {
        var first = today.AddDays(-LookbackDays);
        var list = new List<OverdueItem>();
        foreach (var i in items)
        {
            if (i.Done || i.Deleted || i.SeriesId is not null || i.CountdownId is not null) continue;
            if (!CountdownCalc.TryParseDate(i.Day, out var day) || day >= today || day < first) continue;
            list.Add(new OverdueItem(i.Id, i.Day, i.Content, today.DayNumber - day.DayNumber, timesMoved is not null && timesMoved.TryGetValue(i.Id, out int n) ? n : 0));
        }
        return list.OrderBy(o => o.Day, StringComparer.Ordinal).ThenBy(o => o.Content, StringComparer.Ordinal).ToList();
    }

    /// <summary>Today, or the next working day when today is a day off.</summary>
    public static DateOnly FirstDay(DateOnly today, Func<DateOnly, bool> isDayOff) => NextWorkingDay(today, isDayOff);

    /// <summary>The first working day on or after <paramref name="from"/> (the day itself when no working day is found within two weeks).</summary>
    public static DateOnly NextWorkingDay(DateOnly from, Func<DateOnly, bool> isDayOff)
    {
        var d = from;
        for (int i = 0; i < 14; i++, d = d.AddDays(1)) if (!isDayOff(d)) return d;
        return from;
    }

    /// <summary>The next working day after <paramref name="day"/>.</summary>
    public static DateOnly DayAfter(DateOnly day, Func<DateOnly, bool> isDayOff) => NextWorkingDay(day.AddDays(1), isDayOff);

    /// <summary>All of them on one day.</summary>
    public static IReadOnlyList<RolloverMove> AllTo(IReadOnlyList<OverdueItem> items, DateOnly day) => items.Select(i => new RolloverMove(i, day)).ToList();

    /// <summary>
    /// Spreads the items over the next <paramref name="days"/> working days from <paramref name="start"/>: each goes to the day with the
    /// fewest open to-dos (counting the ones already given to it), with two busy hours of timed events weighing as much as one to-do;
    /// a tie goes to the earlier day. The most-postponed and oldest items are placed first so they land on the earliest free days.
    /// </summary>
    /// <param name="openCount">Open to-dos already on a day.</param>
    /// <param name="busyMinutes">Minutes taken by timed events on a day.</param>
    public static IReadOnlyList<RolloverMove> Distribute(
        IReadOnlyList<OverdueItem> items, DateOnly start, Func<DateOnly, bool> isDayOff,
        Func<DateOnly, int> openCount, Func<DateOnly, int> busyMinutes, int days = 5)
    {
        if (items.Count == 0) return [];
        var candidates = new List<DateOnly>();
        for (var d = NextWorkingDay(start, isDayOff); candidates.Count < Math.Max(1, days); d = NextWorkingDay(d.AddDays(1), isDayOff))
        {
            if (candidates.Count > 0 && d <= candidates[^1]) break;                  // no further working day: stop with what there is
            candidates.Add(d);
        }
        var load = candidates.ToDictionary(d => d, d => (double)openCount(d) + busyMinutes(d) / 120.0);
        var result = new List<RolloverMove>();
        foreach (var item in items.OrderByDescending(i => i.TimesMoved).ThenByDescending(i => i.DaysLate).ThenBy(i => i.Content, StringComparer.Ordinal))
        {
            var day = candidates.OrderBy(d => load[d]).ThenBy(d => d).First();
            load[day] += 1;
            result.Add(new RolloverMove(item, day));
        }
        return result.OrderBy(m => m.Item.Day, StringComparer.Ordinal).ThenBy(m => m.Item.Content, StringComparer.Ordinal).ToList();
    }
}
