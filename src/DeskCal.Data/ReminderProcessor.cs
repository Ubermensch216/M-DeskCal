using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>A reminder that is due now.</summary>
public sealed record ReminderMessage(
    string ReminderId, ReminderOwner OwnerType, string OwnerId, string Title,
    DateOnly Occurrence, TimeOnly? StartTime, DateTime Trigger, int OffsetMinutes, string? Sound,
    /// <summary>True when it was due noticeably earlier (the app was closed, or the PC was asleep).</summary>
    bool Late);

/// <summary>
/// Finds due reminders and the next wake-up time. <see cref="Reminder.FiredAt"/> means "all triggers up to this
/// instant have been handled", so a restart never repeats a reminder and one missed while the app was closed is
/// shown (as <see cref="ReminderMessage.Late"/>) if it is recent enough. All times are local.
/// </summary>
public sealed class ReminderProcessor(DeskCalDb db, IClock clock, Func<DateOnly, bool>? isDayOff = null)
{
    private readonly ReminderRepository _reminders = new(db, clock);
    private readonly EventRepository _events = new(db, clock);
    private readonly ItemRepository _items = new(db, clock);

    private const int MaxPerReminder = 200;       // a reminder that is hours behind on a daily series must not spin forever
    private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    private sealed record Resolved(Reminder Reminder, ReminderSubject Subject, string Title);

    /// <summary>
    /// Reminders whose trigger is at or before <paramref name="nowLocal"/>. Triggers older than
    /// <paramref name="missedWindow"/> are marked handled silently. Everything returned is marked handled.
    /// </summary>
    public IReadOnlyList<ReminderMessage> CollectDue(DateTime nowLocal, TimeSpan missedWindow)
    {
        var messages = new List<ReminderMessage>();
        foreach (var rs in Resolve())
        {
            var after = AfterOf(rs.Reminder, nowLocal);
            DateTime? lastHandled = null;
            for (int i = 0; i < MaxPerReminder; i++)
            {
                if (ReminderPlanner.Next(rs.Subject, rs.Reminder, after, isDayOff) is not { } next || next.Trigger > nowLocal) break;
                lastHandled = after = next.Trigger;
                if (nowLocal - next.Trigger > missedWindow) continue;           // too old: swallow
                messages.Add(new ReminderMessage(rs.Reminder.Id, rs.Reminder.OwnerType, rs.Reminder.OwnerId, rs.Title,
                    next.Occurrence, rs.Subject.StartTime, next.Trigger, rs.Reminder.OffsetMinutes, rs.Reminder.Sound,
                    Late: nowLocal - next.Trigger > LateAfter));
            }
            if (lastHandled is { } t) _reminders.MarkFired(rs.Reminder.Id, ToMs(t));
        }
        return messages.OrderBy(m => m.Trigger).ThenBy(m => m.Title, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>The soonest trigger after everything handled so far, or null if nothing is scheduled.</summary>
    public DateTime? NextWake(DateTime nowLocal)
    {
        DateTime? best = null;
        foreach (var rs in Resolve())
            if (ReminderPlanner.NextTrigger(rs.Subject, rs.Reminder, AfterOf(rs.Reminder, nowLocal), isDayOff) is { } t && (best is null || t < best))
                best = t;
        return best;
    }

    // ---- internals -------------------------------------------------------------

    private static DateTime AfterOf(Reminder r, DateTime nowLocal) =>
        r.FiredAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : nowLocal;

    private static long ToMs(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)).ToUnixTimeMilliseconds();

    private List<Resolved> Resolve()
    {
        var all = _reminders.ListAll();
        var result = new List<Resolved>(all.Count);
        if (all.Count == 0) return result;

        var events = _events.GetAllActive().ToDictionary(e => e.Id);
        var skips = _events.GetExceptions();
        var items = _items.GetMany(all.Where(r => r.OwnerType == ReminderOwner.Item).Select(r => r.OwnerId));    // one query, however many repeating items there are

        foreach (var r in all)
        {
            if (r.OwnerType == ReminderOwner.Event)
            {
                if (!events.TryGetValue(r.OwnerId, out var e) || !EventTime.TryParse(e.Start, out _, out _)) continue;
                skips.TryGetValue(e.Id, out var s);
                result.Add(new Resolved(r, ReminderPlanner.Subject(e, s), e.Title));
            }
            else
            {
                if (!items.TryGetValue(r.OwnerId, out var item) || item.Deleted ||!DateOnly.TryParseExact(item.Day, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)) continue;
                result.Add(new Resolved(r, new ReminderSubject(day, null, RecurrenceRule.None, new HashSet<DateOnly>()), item.Content));
            }
        }
        return result;
    }
}
