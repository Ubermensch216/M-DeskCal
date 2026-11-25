using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>One reminder of an event: fires <see cref="OffsetMinutes"/> before the start (all-day events: before CustomTime).</summary>
public sealed record ReminderSpec(int OffsetMinutes, string? CustomTime = null, string? Sound = null);

/// <summary>
/// Everything the event editor edits. <see cref="StartTime"/> == null means all-day (then the end is a date only).
/// </summary>
public sealed record EventDraft(
    string Title, string Body,
    DateOnly StartDate, TimeOnly? StartTime,
    DateOnly? EndDate, TimeOnly? EndTime,
    RecurrenceRule Rule, string? Color,
    IReadOnlyList<ReminderSpec> Reminders,
    bool Done = false,
    string? CountdownId = null)
{
    public bool AllDay => StartTime is null;

    internal string StartText => EventTime.Format(StartDate, StartTime);

    /// <summary>The stored end, or null when the event has none. Timed events end on the start day unless EndDate says otherwise.</summary>
    internal string? EndText()
    {
        if (StartTime is null)
            return EndDate is { } d && d > StartDate ? EventTime.Format(d, null) : null;
        if (EndDate is null && EndTime is null) return null;
        return EventTime.Format(EndDate ?? StartDate, EndTime ?? StartTime);
    }

    /// <summary>Problems that would make the draft unusable (empty list = valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Title)) errors.Add("제목을 입력하세요.");
        if (EndText() is { } end && EventTime.TryParse(end, out var ed, out var et))
        {
            if (ed < StartDate) errors.Add("종료일이 시작일보다 앞섭니다.");
            else if (ed == StartDate && et is { } t && StartTime is { } s && t < s) errors.Add("종료 시간이 시작 시간보다 앞섭니다.");
        }
        if (Rule.Count is < 1) errors.Add("반복 횟수는 1 이상이어야 합니다.");
        if (Rule.Until is { } u && u < StartDate) errors.Add("반복 종료일이 시작일보다 앞섭니다.");
        return errors;
    }

    public static EventDraft From(EventEntry e, IEnumerable<Reminder> reminders)
    {
        EventTime.TryParse(e.Start, out var sd, out var st);
        DateOnly? ed = null; TimeOnly? et = null;
        if (EventTime.TryParse(e.End, out var d1, out var t1)) { ed = d1; et = t1; }
        return new EventDraft(e.Title, e.Body, sd, st, ed, et, RecurrenceRule.From(e), e.Color,
            reminders.Select(r => new ReminderSpec(r.OffsetMinutes, r.CustomTime, r.Sound)).ToList(),
            e.Done, e.CountdownId);
    }
}

/// <summary>
/// Creating, editing and deleting events, including the three scopes for a repeating event:
/// this occurrence only, this and all following ones, or the whole series.
/// </summary>
public sealed class EventSeriesService(DeskCalDb db, IClock clock, Func<DateOnly, bool>? isDayOff = null)
{
    private readonly EventRepository _events = new(db, clock);
    private readonly ReminderRepository _reminders = new(db, clock);
    private readonly CountdownRepository _countdowns = new(db, clock);

    public EventEntry Create(EventDraft d)
    {
        Require(d);
        var (rr, ext) = d.Rule.ToColumns(d.StartDate);
        var e = _events.Add(d.Title.Trim(), d.StartText, d.EndText(), d.AllDay, null, rr, ext, d.Body, d.Color, d.Done ? 1 : 0, d.CountdownId);
        SetReminders(e.Id, d);
        return e;
    }

    public EventDraft? Load(string id)
    {
        var e = _events.Get(id);
        return e is null || e.Deleted ? null : EventDraft.From(e, _reminders.GetByOwner(ReminderOwner.Event, id));
    }

    // ---- edit ------------------------------------------------------------------

    /// <summary>Change the whole series (or a single event).</summary>
    public bool UpdateAll(string id, EventDraft d)
    {
        Require(d);
        var old = _events.Get(id);
        if (old is null || old.Deleted) return false;
        var (rr, ext) = d.Rule.ToColumns(d.StartDate);
        bool ok = _events.Update(old with
        {
            Title = d.Title.Trim(), Body = d.Body, Start = d.StartText, End = d.EndText(), AllDay = d.AllDay,
            RRule = rr, ExtRule = ext, Status = d.Done ? 1 : 0, Color = d.Color, CountdownId = d.CountdownId,
        });
        if (ok)
        {
            SetReminders(id, d);
            SyncLinkedCountdown(d);
        }
        return ok;
    }

    /// <summary>Change one occurrence: the original skips that day and a stand-alone event takes its place.</summary>
    public EventEntry? UpdateThis(string id, DateOnly occurrence, EventDraft d)
    {
        var old = _events.Get(id);
        if (old is null || old.Deleted) return null;
        if (!RecurrenceRule.From(old).IsRepeating) return UpdateAll(id, d) ? _events.Get(id) : null;

        _events.AddException(id, occurrence);
        // If the stand-alone copy keeps the series' countdown id, don't share one id across two events.
        string? ownCountdown = d.CountdownId is not null && d.CountdownId == old.CountdownId ? null : d.CountdownId;
        return Create(d with { Rule = RecurrenceRule.None, CountdownId = ownCountdown });
    }

    /// <summary>Change this occurrence and all following ones: the original series ends the day before, a new one starts here.</summary>
    public EventEntry? UpdateFuture(string id, DateOnly occurrence, EventDraft d)
    {
        var old = _events.Get(id);
        if (old is null || old.Deleted) return null;
        EventTime.TryParse(old.Start, out var start, out _);
        var oldRule = RecurrenceRule.From(old);

        if (!oldRule.IsRepeating || occurrence <= start) return UpdateAll(id, d) ? _events.Get(id) : null;

        // a counted series keeps its total: the new part gets what is left
        var newRule = d.Rule;
        if (oldRule.Count is { } total && newRule.Kind == oldRule.Kind && newRule.Count == oldRule.Count)
        {
            int used = RecurrenceExpander.CountBefore(oldRule, start, occurrence, isDayOff);
            newRule = newRule with { Count = Math.Max(1, total - used) };
        }

        if (!EndSeriesBefore(old, oldRule, start, occurrence)) DeleteAll(id);
        string? ownCountdown = d.CountdownId is not null && d.CountdownId == old.CountdownId ? null : d.CountdownId;
        return Create(d with { Rule = newRule, CountdownId = ownCountdown });
    }

    // ---- delete ----------------------------------------------------------------

    public bool DeleteAll(string id)
    {
        bool ok = _events.SoftDelete(id);
        _reminders.DeleteByOwner(ReminderOwner.Event, id);
        return ok;
    }

    public bool DeleteThis(string id, DateOnly occurrence)
    {
        var e = _events.Get(id);
        if (e is null || e.Deleted) return false;
        if (!RecurrenceRule.From(e).IsRepeating) return DeleteAll(id);
        _events.AddException(id, occurrence);
        return true;
    }

    public bool DeleteFuture(string id, DateOnly occurrence)
    {
        var e = _events.Get(id);
        if (e is null || e.Deleted) return false;
        EventTime.TryParse(e.Start, out var start, out _);
        var rule = RecurrenceRule.From(e);
        if (!rule.IsRepeating || occurrence <= start) return DeleteAll(id);
        if (!EndSeriesBefore(e, rule, start, occurrence)) DeleteAll(id);
        return true;
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>End the series on its last occurrence before <paramref name="date"/>. False when nothing would remain.</summary>
    private bool EndSeriesBefore(EventEntry e, RecurrenceRule rule, DateOnly start, DateOnly date)
    {
        var last = RecurrenceExpander.LastBefore(rule, start, date, isDayOff);
        if (last is null) return false;
        var (rr, ext) = (rule with { Until = last, Count = null }).ToColumns(start);
        _events.Update(e with { RRule = rr, ExtRule = ext });
        _events.RemoveExceptionsFrom(e.Id, date);
        return true;
    }

    private void SetReminders(string eventId, EventDraft d) =>
        _reminders.ReplaceForOwner(ReminderOwner.Event, eventId, d.Reminders.Select(r => (r.OffsetMinutes, r.CustomTime, r.Sound)));

    private void SyncLinkedCountdown(EventDraft d)
    {
        if (string.IsNullOrEmpty(d.CountdownId)) return;
        var existing = _countdowns.List().FirstOrDefault(c => c.Id == d.CountdownId);
        if (existing is null) return;
        var title = d.Title.Trim();
        var date = existing.Lunar ? existing.Date : CountdownCalc.Format(d.StartDate);
        if (existing.Title != title || existing.Date != date)
            _countdowns.Upsert(existing with { Title = title, Date = date });
    }

    private static void Require(EventDraft d)
    {
        var errors = d.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(d));
    }
}
