using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;

namespace DeskCal.Tests;

internal static class Local
{
    public static long Ms(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)).ToUnixTimeMilliseconds();
    public static DateTime At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
}

public class EventSeriesTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);
    private static readonly RecurrenceRule Weekly = new() { Kind = RepeatKind.Weekly };

    private static EventDraft Draft(string title = "팀 회의", string start = "2026-11-02", TimeOnly? time = null,
                                    RecurrenceRule? rule = null, params ReminderSpec[] reminders)
    {
        var d = DateOnly.Parse(start);
        return new EventDraft(title, "", d, time ?? new TimeOnly(10, 0), null, null, rule ?? RecurrenceRule.None, null, reminders);
    }

    private static List<(DateOnly Date, string Title)> Shown(DbFixture f, DateOnly from, DateOnly to) =>
        EventOccurrences.Expand(f.Events.GetAllActive(), f.Events.GetExceptions(), from, to)
            .Select(o => (o.Date, o.Event.Title)).ToList();

    private static EventSeriesService Svc(DbFixture f, Func<DateOnly, bool>? off = null) => new(f.Db, f.Clock, off);

    // ---- create / load / validate ---------------------------------------------

    [Fact]
    public void Create_stores_columns_and_load_round_trips()
    {
        using var f = new DbFixture();
        var rule = new RecurrenceRule { Kind = RepeatKind.Weekly, Interval = 2, WeekdaysMask = 0b0000110, Count = 8 };
        var d = new EventDraft(" 스터디 ", "본문", D(2026, 11, 2), new TimeOnly(19, 30), D(2026, 11, 2), new TimeOnly(21, 0), rule, "#FFAA00",
            [new ReminderSpec(10), new ReminderSpec(1440, "08:00", "none")]);
        var e = Svc(f).Create(d);

        Assert.Equal("스터디", e.Title);                                          // trimmed
        Assert.Equal("2026-11-02T19:30", e.Start);
        Assert.Equal("2026-11-02T21:00", e.End);
        Assert.False(e.AllDay);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TU;COUNT=8", e.RRule);
        Assert.Null(e.ExtRule);

        var back = Svc(f).Load(e.Id)!;
        Assert.Equal(d.Title.Trim(), back.Title); Assert.Equal(d.StartDate, back.StartDate); Assert.Equal(d.StartTime, back.StartTime);
        Assert.Equal(d.EndTime, back.EndTime); Assert.Equal(rule, back.Rule); Assert.Equal("#FFAA00", back.Color);
        Assert.Equal([new ReminderSpec(1440, "08:00", "none"), new ReminderSpec(10)], back.Reminders);   // ordered by offset, largest first
    }

    [Fact]
    public void All_day_and_extended_rules_are_stored_in_the_right_columns()
    {
        using var f = new DbFixture();
        var e = Svc(f).Create(new EventDraft("월말 정산", "", D(2026, 11, 30), null, null, null,
            new RecurrenceRule { Kind = RepeatKind.MonthLastDay }, null, []));
        Assert.True(e.AllDay);
        Assert.Equal("2026-11-30", e.Start);
        Assert.Null(e.End);
        Assert.Null(e.RRule);
        Assert.Equal("monthlast", e.ExtRule);

        var span = Svc(f).Create(new EventDraft("출장", "", D(2026, 11, 2), null, D(2026, 11, 5), null, RecurrenceRule.None, null, []));
        Assert.Equal("2026-11-05", span.End);
        var sameDay = Svc(f).Create(new EventDraft("하루", "", D(2026, 11, 2), null, D(2026, 11, 2), null, RecurrenceRule.None, null, []));
        Assert.Null(sameDay.End);                                                      // an all-day end equal to the start is no end
    }

    [Fact]
    public void Timed_event_with_an_end_date_only_ends_at_the_start_time_of_day()
    {
        using var f = new DbFixture();
        var e = Svc(f).Create(new EventDraft("야간", "", D(2026, 11, 2), new TimeOnly(22, 0), D(2026, 11, 3), null, RecurrenceRule.None, null, []));
        Assert.Equal("2026-11-03T22:00", e.End);
    }

    [Fact]
    public void Invalid_drafts_are_reported_and_rejected()
    {
        using var f = new DbFixture();
        Assert.NotEmpty(Draft("  ").Validate());
        Assert.NotEmpty((Draft() with { EndDate = D(2026, 11, 1) }).Validate());
        Assert.NotEmpty((Draft() with { EndTime = new TimeOnly(9, 0) }).Validate());                      // ends before it starts on the same day
        Assert.NotEmpty((Draft(rule: Weekly with { Count = 0 })).Validate());
        Assert.NotEmpty((Draft(rule: Weekly with { Until = D(2026, 10, 1) })).Validate());
        Assert.Empty(Draft().Validate());
        Assert.Throws<ArgumentException>(() => Svc(f).Create(Draft("")));
        Assert.Empty(f.Events.GetAllActive());
    }

    // ---- update ---------------------------------------------------------------

    [Fact]
    public void UpdateAll_changes_fields_and_replaces_reminders_but_keeps_the_id()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var e = svc.Create(Draft(reminders: new ReminderSpec(10)));
        f.Clock.Advance();
        Assert.True(svc.UpdateAll(e.Id, Draft("새 제목", "2026-11-03", new TimeOnly(8, 0), Weekly, new ReminderSpec(60), new ReminderSpec(5)) with { Body = "메모" }));
        var got = f.Events.Get(e.Id)!;
        Assert.Equal(("새 제목", "2026-11-03T08:00", "메모", "FREQ=WEEKLY"), (got.Title, got.Start, got.Body, got.RRule));
        Assert.Equal([60, 5], f.Reminders.GetByOwner(ReminderOwner.Event, e.Id).Select(r => r.OffsetMinutes));
        Assert.False(svc.UpdateAll("missing", Draft()));
    }

    [Fact]
    public void UpdateThis_replaces_one_occurrence_of_a_series()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var series = svc.Create(Draft("정기", "2026-11-02", rule: Weekly, reminders: new ReminderSpec(10)));
        var replacement = svc.UpdateThis(series.Id, D(2026, 11, 9), Draft("특별", "2026-11-10", new TimeOnly(15, 0)))!;

        Assert.NotEqual(series.Id, replacement.Id);
        Assert.Null(replacement.RRule);                                                    // the replacement is stand-alone
        Assert.Equal([(D(2026, 11, 2), "정기"), (D(2026, 11, 10), "특별"), (D(2026, 11, 16), "정기"), (D(2026, 11, 23), "정기")],
            Shown(f, D(2026, 11, 1), D(2026, 11, 25)));
        Assert.Single(f.Reminders.GetByOwner(ReminderOwner.Event, series.Id));                // the series keeps its own reminder
    }

    [Fact]
    public void UpdateThis_on_a_single_event_is_a_plain_update()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var e = svc.Create(Draft("한 번"));
        var r = svc.UpdateThis(e.Id, D(2026, 11, 2), Draft("수정됨"))!;
        Assert.Equal(e.Id, r.Id);
        Assert.Single(f.Events.GetAllActive());
        Assert.Equal("수정됨", f.Events.Get(e.Id)!.Title);
    }

    [Fact]
    public void UpdateFuture_splits_the_series_at_the_occurrence()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var series = svc.Create(Draft("정기", "2026-11-02", rule: Weekly));
        f.Events.AddException(series.Id, D(2026, 11, 30));                                 // a skipped day after the split disappears with it
        var tail = svc.UpdateFuture(series.Id, D(2026, 11, 16), Draft("변경", "2026-11-16", new TimeOnly(11, 0), Weekly))!;

        Assert.NotEqual(series.Id, tail.Id);
        Assert.Equal([(D(2026, 11, 2), "정기"), (D(2026, 11, 9), "정기"), (D(2026, 11, 16), "변경"), (D(2026, 11, 23), "변경"), (D(2026, 12, 7), "변경")],
            Shown(f, D(2026, 11, 1), D(2026, 12, 8)).Where(x => x.Date.DayOfWeek == DayOfWeek.Monday && x.Date != D(2026, 11, 30)).ToList());
        Assert.Equal("FREQ=WEEKLY;UNTIL=20261109", f.Events.Get(series.Id)!.RRule);
        Assert.Empty(f.Events.GetExceptions(series.Id));
    }

    [Fact]
    public void UpdateFuture_on_a_counted_series_keeps_the_total_count()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var counted = Weekly with { Count = 6 };
        var series = svc.Create(Draft("6회 강의", "2026-11-02", rule: counted));
        var tail = svc.UpdateFuture(series.Id, D(2026, 11, 16), Draft("6회 강의(변경)", "2026-11-16", rule: counted))!;

        Assert.Equal("FREQ=WEEKLY;COUNT=4", tail.RRule);                                   // 2 already happened before the split
        Assert.Equal(6, Shown(f, D(2026, 1, 1), D(2027, 12, 31)).Count);
        Assert.Null(f.Events.Get(series.Id)!.RRule is { } r && r.Contains("COUNT") ? "bad" : null);
    }

    [Fact]
    public void UpdateFuture_at_or_before_the_first_occurrence_updates_the_whole_series()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var series = svc.Create(Draft("정기", "2026-11-02", rule: Weekly));
        var r = svc.UpdateFuture(series.Id, D(2026, 11, 2), Draft("전체 변경", "2026-11-02", rule: Weekly))!;
        Assert.Equal(series.Id, r.Id);
        Assert.Single(f.Events.GetAllActive());
        Assert.Equal("전체 변경", f.Events.Get(series.Id)!.Title);
    }

    // ---- delete ---------------------------------------------------------------

    [Fact]
    public void DeleteThis_skips_one_occurrence_of_a_series_and_deletes_a_single_event()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var series = svc.Create(Draft("정기", "2026-11-02", rule: Weekly));
        Assert.True(svc.DeleteThis(series.Id, D(2026, 11, 9)));
        Assert.Equal([D(2026, 11, 2), D(2026, 11, 16)], Shown(f, D(2026, 11, 1), D(2026, 11, 20)).Select(x => x.Date));

        var single = svc.Create(Draft("한 번", "2026-11-05"));
        Assert.True(svc.DeleteThis(single.Id, D(2026, 11, 5)));
        Assert.True(f.Events.Get(single.Id)!.Deleted);
        Assert.False(svc.DeleteThis(single.Id, D(2026, 11, 5)));                            // already gone
    }

    [Fact]
    public void DeleteFuture_ends_the_series_the_day_before_and_clears_the_count()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var series = svc.Create(Draft("정기", "2026-11-02", rule: Weekly with { Count = 10 }));
        Assert.True(svc.DeleteFuture(series.Id, D(2026, 11, 23)));
        Assert.Equal("FREQ=WEEKLY;UNTIL=20261116", f.Events.Get(series.Id)!.RRule);
        Assert.Equal([D(2026, 11, 2), D(2026, 11, 9), D(2026, 11, 16)], Shown(f, D(2026, 1, 1), D(2027, 12, 31)).Select(x => x.Date));
    }

    [Fact]
    public void DeleteFuture_from_the_first_occurrence_deletes_everything_including_reminders()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var series = svc.Create(Draft("정기", "2026-11-02", rule: Weekly, reminders: new ReminderSpec(10)));
        Assert.True(svc.DeleteFuture(series.Id, D(2026, 11, 2)));
        Assert.True(f.Events.Get(series.Id)!.Deleted);
        Assert.Empty(f.Reminders.GetByOwner(ReminderOwner.Event, series.Id));
        Assert.Empty(Shown(f, D(2026, 1, 1), D(2027, 12, 31)));
    }

    [Fact]
    public void DeleteAll_removes_the_event_and_unknown_ids_are_harmless()
    {
        using var f = new DbFixture();
        var svc = Svc(f);
        var e = svc.Create(Draft(reminders: new ReminderSpec(5)));
        Assert.True(svc.DeleteAll(e.Id));
        Assert.Null(svc.Load(e.Id));
        Assert.False(svc.DeleteThis("nope", D(2026, 11, 2)));
        Assert.False(svc.DeleteFuture("nope", D(2026, 11, 2)));
        Assert.Null(svc.UpdateThis("nope", D(2026, 11, 2), Draft()));
        Assert.Null(svc.UpdateFuture("nope", D(2026, 11, 2), Draft()));
    }

    [Fact]
    public void Splitting_a_legal_work_day_series_uses_the_holiday_table()
    {
        using var f = new DbFixture();
        var h = new KoreanHolidays();
        var svc = Svc(f, h.IsDayOff);
        var rule = new RecurrenceRule { Kind = RepeatKind.LegalWorkDay };
        var series = svc.Create(Draft("업무", "2026-09-23", rule: rule));
        // 9/24-9/25 are 추석 days off, so the last working day before 9/28 is 9/23
        Assert.True(svc.DeleteFuture(series.Id, D(2026, 9, 28)));
        Assert.Equal("legalworkday;UNTIL=20260923", f.Events.Get(series.Id)!.ExtRule);
    }
}

public class ReminderProcessorTests
{
    private static readonly DateTime Nov8 = new(2026, 11, 8, 10, 0, 0);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static (ReminderProcessor Proc, EventSeriesService Svc) Setup(DbFixture f, DateTime now)
    {
        f.Clock.NowMs = Local.Ms(now);
        return (new ReminderProcessor(f.Db, f.Clock), new EventSeriesService(f.Db, f.Clock));
    }

    private static EventDraft Timed(string date, string time, string title = "팀 회의", RecurrenceRule? rule = null, params ReminderSpec[] r) =>
        new(title, "", DateOnly.Parse(date), TimeOnly.Parse(time), null, null, rule ?? RecurrenceRule.None, null, r);

    [Fact]
    public void A_reminder_fires_once_at_its_trigger_time()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        svc.Create(Timed("2026-11-09", "09:30", r: new ReminderSpec(10)));

        Assert.Equal(new DateTime(2026, 11, 9, 9, 20, 0), proc.NextWake(Nov8));
        Assert.Empty(proc.CollectDue(new DateTime(2026, 11, 9, 9, 19, 59), TimeSpan.FromHours(24)));

        var due = proc.CollectDue(new DateTime(2026, 11, 9, 9, 20, 1), TimeSpan.FromHours(24));
        var m = Assert.Single(due);
        Assert.Equal("팀 회의", m.Title);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 20, 0), m.Trigger);
        Assert.Equal((D(2026, 11, 9), new TimeOnly(9, 30), 10, false), (m.Occurrence, m.StartTime!.Value, m.OffsetMinutes, m.Late));

        Assert.Empty(proc.CollectDue(new DateTime(2026, 11, 9, 9, 21, 0), TimeSpan.FromHours(24)));        // never repeats
        Assert.Null(proc.NextWake(new DateTime(2026, 11, 9, 9, 21, 0)));
        Assert.Equal(Local.Ms(new DateTime(2026, 11, 9, 9, 20, 0)), f.Reminders.ListAll().Single().FiredAt);
    }

    [Fact]
    public void A_daily_series_wakes_again_for_the_next_day()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        svc.Create(Timed("2026-11-09", "09:30", rule: new RecurrenceRule { Kind = RepeatKind.Daily }, r: new ReminderSpec(10)));
        proc.CollectDue(new DateTime(2026, 11, 9, 9, 21, 0), TimeSpan.FromHours(24));
        Assert.Equal(new DateTime(2026, 11, 10, 9, 20, 0), proc.NextWake(new DateTime(2026, 11, 9, 9, 22, 0)));
        Assert.Single(proc.CollectDue(new DateTime(2026, 11, 10, 9, 20, 30), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Missed_reminders_inside_the_window_are_shown_as_late_and_older_ones_are_swallowed()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        svc.Create(Timed("2026-11-09", "09:30", rule: new RecurrenceRule { Kind = RepeatKind.Daily }, r: new ReminderSpec(10)));

        // the PC was off until 11/12 10:00: triggers 11/9..11/12 at 09:20; only the last is within 24 h
        var now = new DateTime(2026, 11, 12, 10, 0, 0);
        var due = proc.CollectDue(now, TimeSpan.FromHours(24));
        var m = Assert.Single(due);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 20, 0), m.Trigger);
        Assert.True(m.Late);
        Assert.Empty(proc.CollectDue(now, TimeSpan.FromHours(24)));                                        // and the swallowed ones are not replayed
    }

    [Fact]
    public void A_wide_window_returns_every_missed_trigger_in_order()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        svc.Create(Timed("2026-11-09", "09:30", rule: new RecurrenceRule { Kind = RepeatKind.Daily }, r: new ReminderSpec(10)));
        var due = proc.CollectDue(new DateTime(2026, 11, 12, 10, 0, 0), TimeSpan.FromDays(30));
        Assert.Equal([9, 10, 11, 12], due.Select(m => m.Trigger.Day));
        Assert.All(due, m => Assert.True(m.Late));
    }

    [Fact]
    public void Skipped_occurrences_do_not_fire()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        var e = svc.Create(Timed("2026-11-09", "09:30", rule: new RecurrenceRule { Kind = RepeatKind.Daily }, r: new ReminderSpec(10)));
        svc.DeleteThis(e.Id, D(2026, 11, 10));
        var due = proc.CollectDue(new DateTime(2026, 11, 11, 12, 0, 0), TimeSpan.FromDays(30));
        Assert.Equal([9, 11], due.Select(m => m.Trigger.Day));
    }

    [Fact]
    public void A_reminder_created_after_its_trigger_time_does_not_fire_retroactively()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, new DateTime(2026, 11, 9, 10, 0, 0));
        svc.Create(Timed("2026-11-09", "09:30", r: new ReminderSpec(10)));                               // trigger 09:20 already passed
        Assert.Empty(proc.CollectDue(new DateTime(2026, 11, 9, 10, 5, 0), TimeSpan.FromDays(1)));
        Assert.Null(proc.NextWake(new DateTime(2026, 11, 9, 10, 5, 0)));
    }

    [Fact]
    public void All_day_events_and_items_fire_at_their_custom_time()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        svc.Create(new EventDraft("기념일", "", D(2026, 11, 10), null, null, null, RecurrenceRule.None, null, [new ReminderSpec(1440, "08:00")]));
        var item = f.Items.Add("2026-11-11", "서류 제출");
        f.Reminders.Add(ReminderOwner.Item, item.Id, 0);                                                    // default 09:00 on the day

        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), proc.NextWake(Nov8));
        var first = Assert.Single(proc.CollectDue(new DateTime(2026, 11, 9, 8, 1, 0), TimeSpan.FromHours(1)));
        Assert.Equal("기념일", first.Title);
        Assert.Null(first.StartTime);

        var second = Assert.Single(proc.CollectDue(new DateTime(2026, 11, 11, 9, 0, 30), TimeSpan.FromHours(1)));
        Assert.Equal(("서류 제출", ReminderOwner.Item), (second.Title, second.OwnerType));
    }

    [Fact]
    public void Deleted_owners_and_orphans_are_ignored()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        var item = f.Items.Add("2026-11-09", "지워질 항목");
        f.Reminders.Add(ReminderOwner.Item, item.Id, 0);
        f.Reminders.Add(ReminderOwner.Event, "no-such-event", 0);
        f.Reminders.Add(ReminderOwner.Item, "no-such-item", 0);
        f.Items.SoftDelete(item.Id);
        Assert.Null(proc.NextWake(Nov8));
        Assert.Empty(proc.CollectDue(new DateTime(2026, 11, 10), TimeSpan.FromDays(5)));
    }

    [Fact]
    public void A_legacy_reminder_without_a_handled_marker_does_not_flood()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        var e = svc.Create(Timed("2026-11-09", "09:30", rule: new RecurrenceRule { Kind = RepeatKind.Daily }));
        using (var c = f.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO reminders(id, owner_type, owner_id, offset_minutes, fired_at) VALUES('legacy', 1, '{e.Id}', 10, NULL)";
            cmd.ExecuteNonQuery();
        }
        var now = new DateTime(2026, 12, 25, 12, 0, 0);
        Assert.Empty(proc.CollectDue(now, TimeSpan.FromDays(365)));                                          // treated as armed "now", nothing replays
        Assert.Equal(new DateTime(2026, 12, 26, 9, 20, 0), proc.NextWake(now));
    }

    [Fact]
    public void Editing_an_event_rearms_its_reminders_without_replaying_old_triggers()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, Nov8);
        var e = svc.Create(Timed("2026-11-09", "09:30", rule: new RecurrenceRule { Kind = RepeatKind.Daily }, r: new ReminderSpec(10)));
        proc.CollectDue(new DateTime(2026, 11, 9, 9, 21, 0), TimeSpan.FromDays(1));                      // 11/9 handled

        f.Clock.NowMs = Local.Ms(new DateTime(2026, 11, 9, 9, 25, 0));
        svc.UpdateAll(e.Id, Timed("2026-11-09", "09:30", "회의(수정)", new RecurrenceRule { Kind = RepeatKind.Daily }, new ReminderSpec(10)));
        Assert.Empty(proc.CollectDue(new DateTime(2026, 11, 9, 9, 26, 0), TimeSpan.FromDays(1)));
        Assert.Equal("회의(수정)", Assert.Single(proc.CollectDue(new DateTime(2026, 11, 10, 9, 20, 5), TimeSpan.FromDays(1))).Title);
    }

    [Fact]
    public void Catching_up_a_very_old_daily_series_is_bounded()
    {
        using var f = new DbFixture();
        var (proc, svc) = Setup(f, new DateTime(2020, 1, 1, 0, 0, 0));
        svc.Create(Timed("2020-01-02", "09:00", rule: new RecurrenceRule { Kind = RepeatKind.Daily }, r: new ReminderSpec(0)));
        var now = new DateTime(2026, 11, 8, 0, 0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = proc.CollectDue(now, TimeSpan.FromDays(10_000));
        Assert.Equal(200, first.Count);                                                                      // capped per call
        Assert.True(sw.ElapsedMilliseconds < 3000, sw.ElapsedMilliseconds + " ms");
        Assert.NotEmpty(proc.CollectDue(now, TimeSpan.FromDays(10_000)));                                    // the next call continues where it stopped
    }

    [Fact]
    public void Nothing_scheduled_means_no_wake()
    {
        using var f = new DbFixture();
        var (proc, _) = Setup(f, Nov8);
        Assert.Null(proc.NextWake(Nov8));
        Assert.Empty(proc.CollectDue(Nov8, TimeSpan.FromDays(1)));
    }
}
