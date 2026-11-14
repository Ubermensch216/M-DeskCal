using DeskCal.Core;
using DeskCal.Core.Calendars;

namespace DeskCal.Tests;

public class RecurrenceRuleTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static List<DateOnly> First(RecurrenceRule r, DateOnly start, int n, Func<DateOnly, bool>? off = null) =>
        RecurrenceExpander.Generate(r, start, off).Take(n).ToList();

    [Fact]
    public void Non_repeating_yields_the_start_once()
    {
        Assert.Equal([D(2026, 11, 8)], RecurrenceExpander.Generate(RecurrenceRule.None, D(2026, 11, 8)).ToList());
    }

    [Fact]
    public void Daily_with_interval_count_and_until()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Daily, Interval = 3 };
        Assert.Equal([D(2026, 11, 8), D(2026, 11, 11), D(2026, 11, 14)], First(r, D(2026, 11, 8), 3));
        Assert.Equal(3, RecurrenceExpander.Generate(r with { Count = 3 }, D(2026, 11, 8)).Count());
        Assert.Equal([D(2026, 11, 8), D(2026, 11, 11)], RecurrenceExpander.Generate(r with { Until = D(2026, 11, 13) }, D(2026, 11, 8)).ToList());
        // Until is inclusive; the earlier of Count and Until wins
        Assert.Equal([D(2026, 11, 8), D(2026, 11, 11), D(2026, 11, 14)], RecurrenceExpander.Generate(r with { Until = D(2026, 11, 14) }, D(2026, 11, 8)).ToList());
        Assert.Equal(2, RecurrenceExpander.Generate(r with { Count = 2, Until = D(2030, 1, 1) }, D(2026, 11, 8)).Count());
        Assert.Equal(2, RecurrenceExpander.Generate(r with { Count = 9, Until = D(2026, 11, 12) }, D(2026, 11, 8)).Count());
    }

    [Fact]
    public void Weekly_defaults_to_the_start_weekday()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Weekly };
        Assert.Equal([D(2026, 11, 8), D(2026, 11, 15), D(2026, 11, 22)], First(r, D(2026, 11, 8), 3));   // Sundays
    }

    [Fact]
    public void Weekly_with_weekdays_and_interval_counts_weeks_from_the_start_week()
    {
        // Monday-based weeks. Start Sunday 11/8 belongs to the week of 11/2 and does not match Mon/Wed.
        int mask = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday);
        Assert.Equal([D(2026, 11, 9), D(2026, 11, 11), D(2026, 11, 16)],
            First(new RecurrenceRule { Kind = RepeatKind.Weekly, WeekdaysMask = mask }, D(2026, 11, 8), 3));
        Assert.Equal([D(2026, 11, 16), D(2026, 11, 18), D(2026, 11, 30), D(2026, 12, 2)],
            First(new RecurrenceRule { Kind = RepeatKind.Weekly, WeekdaysMask = mask, Interval = 2 }, D(2026, 11, 8), 4));
    }

    [Fact]
    public void Monthly_by_date_clamps_to_the_last_day_of_short_months()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Monthly };
        Assert.Equal([D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30), D(2026, 5, 31)], First(r, D(2026, 1, 31), 5));
        Assert.Equal([D(2026, 1, 31), D(2026, 3, 31), D(2026, 5, 31)], First(r with { Interval = 2 }, D(2026, 1, 31), 3));
        Assert.Equal(D(2028, 2, 29), First(r, D(2028, 1, 31), 2)[1]);                                   // leap year
    }

    [Fact]
    public void Monthly_by_weekday_nth_and_last()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Monthly, Monthly = MonthlyMode.ByWeekday };
        Assert.Equal([D(2026, 11, 10), D(2026, 12, 8), D(2027, 1, 12)], First(r, D(2026, 11, 10), 3));    // second Tuesday
        Assert.Equal([D(2026, 11, 24), D(2026, 12, 22)], First(r, D(2026, 11, 24), 2));                   // fourth Tuesday
        // a 5th weekday means "the last one", so short months are never skipped
        Assert.Equal([D(2026, 11, 30), D(2026, 12, 28), D(2027, 1, 25), D(2027, 2, 22)], First(r, D(2026, 11, 30), 4));
        Assert.Equal(D(2026, 11, 24), First(r with { Nth = -1 }, D(2026, 11, 10), 1)[0]);               // explicit "last": first hit is the last Tuesday
    }

    [Fact]
    public void Yearly_moves_feb_29_to_feb_28_in_common_years()
    {
        Assert.Equal([D(2028, 2, 29), D(2029, 2, 28), D(2030, 2, 28), D(2031, 2, 28), D(2032, 2, 29)],
            First(new RecurrenceRule { Kind = RepeatKind.Yearly }, D(2028, 2, 29), 5));
        Assert.Equal([D(2026, 5, 5), D(2028, 5, 5)], First(new RecurrenceRule { Kind = RepeatKind.Yearly, Interval = 2 }, D(2026, 5, 5), 2));
    }

    [Fact]
    public void Month_last_day_and_quarter_end()
    {
        Assert.Equal([D(2026, 11, 30), D(2026, 12, 31), D(2027, 1, 31), D(2027, 2, 28)],
            First(new RecurrenceRule { Kind = RepeatKind.MonthLastDay }, D(2026, 11, 8), 4));
        Assert.Equal(D(2026, 11, 30), First(new RecurrenceRule { Kind = RepeatKind.MonthLastDay }, D(2026, 11, 30), 1)[0]);   // start itself qualifies
        Assert.Equal([D(2026, 12, 31), D(2027, 3, 31), D(2027, 6, 30), D(2027, 9, 30), D(2027, 12, 31)],
            First(new RecurrenceRule { Kind = RepeatKind.SeasonLastDay }, D(2026, 11, 8), 5));
        Assert.Equal(D(2026, 3, 31), First(new RecurrenceRule { Kind = RepeatKind.SeasonLastDay }, D(2026, 3, 31), 1)[0]);
        Assert.Equal([D(2026, 11, 30), D(2027, 1, 31)], First(new RecurrenceRule { Kind = RepeatKind.MonthLastDay, Interval = 2 }, D(2026, 11, 8), 2));
    }

    [Fact]
    public void Weekday_skips_weekends()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Weekday };
        Assert.Equal([D(2026, 11, 6), D(2026, 11, 9), D(2026, 11, 10)], First(r, D(2026, 11, 6), 3));       // Fri, Mon, Tue
        Assert.Equal(D(2026, 11, 9), First(r, D(2026, 11, 7), 1)[0]);                                        // starting on a Saturday
    }

    [Fact]
    public void Legal_work_day_also_skips_public_holidays_including_substitutes()
    {
        var h = new KoreanHolidays();
        var r = new RecurrenceRule { Kind = RepeatKind.LegalWorkDay };
        // 추석 2026: Thu 9/24, Fri 9/25, Sat 9/26
        Assert.Equal([D(2026, 9, 23), D(2026, 9, 28)], First(r, D(2026, 9, 23), 2, h.IsDayOff));
        // 개천절 Sat 10/3 -> substitute Mon 10/5
        Assert.Equal([D(2026, 10, 1), D(2026, 10, 2), D(2026, 10, 6)], First(r, D(2026, 10, 1), 3, h.IsDayOff));
        // without a holiday table it behaves like plain weekdays
        Assert.Equal([D(2026, 9, 23), D(2026, 9, 24)], First(r, D(2026, 9, 23), 2));
    }

    [Fact]
    public void Lunar_yearly_follows_the_lunar_date()
    {
        var h = new KoreanHolidays();
        var seollal = Enumerable.Range(2026, 5).Select(y => h.ForYear(y).Single(x => x.Name == "설날").Date).ToList();
        Assert.Equal(seollal, First(new RecurrenceRule { Kind = RepeatKind.LunarYearly }, seollal[0], 5));
    }

    [Fact]
    public void Lunar_yearly_from_a_leap_month_falls_back_to_the_regular_month()
    {
        // 2023-03-22 is 윤2월 1일. 2024 has no leap 2nd month, so it lands on the regular 2월 1일.
        var occ = First(new RecurrenceRule { Kind = RepeatKind.LunarYearly }, D(2023, 3, 22), 3);
        Assert.Equal(D(2023, 3, 22), occ[0]);
        Assert.Equal(KoreanLunar.ToSolar(2024, 2, 1), occ[1]);
        Assert.Equal(KoreanLunar.ToSolar(2025, 2, 1), occ[2]);
    }

    [Fact]
    public void Lunar_yearly_from_day_30_never_skips_a_year_and_stays_ascending()
    {
        // find a date whose lunar day is 30
        var start = Enumerable.Range(0, 400).Select(i => D(2026, 1, 1).AddDays(i))
            .First(d => KoreanLunar.TryFromSolar(d, out var l) && l.Day == 30 && !l.IsLeapMonth);
        var occ = RecurrenceExpander.Generate(new RecurrenceRule { Kind = RepeatKind.LunarYearly }, start).ToList();
        Assert.True(occ.Count >= 24, occ.Count.ToString());                       // up to the end of the lunar table (2051)
        Assert.Equal(occ.OrderBy(x => x).ToList(), occ);
        // consecutive occurrences are one lunar year apart (354 or 384 days, +-1 for a clamped day 30): no year is skipped
        for (int i = 1; i < occ.Count; i++) Assert.InRange(occ[i].DayNumber - occ[i - 1].DayNumber, 340, 400);
    }

    [Fact]
    public void Lunar_yearly_outside_the_table_falls_back_to_the_solar_date()
    {
        var occ = First(new RecurrenceRule { Kind = RepeatKind.LunarYearly }, D(2100, 3, 4), 2);
        Assert.Equal([D(2100, 3, 4), D(2101, 3, 4)], occ);
    }

    [Fact]
    public void Ebbinghaus_review_schedule()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Ebbinghaus };
        var s = D(2026, 11, 8);
        Assert.Equal(new[] { 0, 1, 2, 4, 7, 15, 30 }.Select(s.AddDays), RecurrenceExpander.Generate(r, s));
        Assert.Equal([s, s.AddDays(1), s.AddDays(2), s.AddDays(4)], RecurrenceExpander.Generate(r with { Until = s.AddDays(5) }, s).ToList());
        Assert.Equal(3, RecurrenceExpander.Generate(r with { Count = 3 }, s).Count());
    }

    [Fact]
    public void Between_filters_and_is_fast_for_old_series()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Daily };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var days = RecurrenceExpander.Between(r, D(2000, 1, 1), D(2026, 11, 1), D(2026, 11, 7));
        Assert.True(sw.ElapsedMilliseconds < 500, sw.ElapsedMilliseconds + " ms");
        Assert.Equal(7, days.Count);
        Assert.Equal(D(2026, 11, 1), days[0]);
        Assert.Empty(RecurrenceExpander.Between(r, D(2026, 11, 8), D(2026, 1, 1), D(2026, 11, 7)));        // range before the start
    }

    [Fact]
    public void CountBefore_and_LastBefore_support_splitting_a_series()
    {
        var r = new RecurrenceRule { Kind = RepeatKind.Weekly, Count = 10 };
        var s = D(2026, 11, 8);
        Assert.Equal(0, RecurrenceExpander.CountBefore(r, s, s));
        Assert.Equal(3, RecurrenceExpander.CountBefore(r, s, s.AddDays(21)));
        Assert.Equal(s.AddDays(14), RecurrenceExpander.LastBefore(r, s, s.AddDays(21)));
        Assert.Null(RecurrenceExpander.LastBefore(r, s, s));
        Assert.Equal(10, RecurrenceExpander.CountBefore(r, s, s.AddDays(1000)));                           // Count caps it
    }

    // ---- serialisation ------------------------------------------------------

    public static IEnumerable<object[]> RoundTripRules()
    {
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.Daily, Interval = 2, Count = 5 }, "FREQ=DAILY;INTERVAL=2;COUNT=5", null!};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.Weekly, WeekdaysMask = 0b0101010, Until = new DateOnly(2027, 1, 31) }, "FREQ=WEEKLY;BYDAY=MO,WE,FR;UNTIL=20270131", null!};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.Monthly, Interval = 3 }, "FREQ=MONTHLY;INTERVAL=3", null!};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.Yearly }, "FREQ=YEARLY", null!};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.Weekday, Count = 5 }, null!, "weekday;COUNT=5"};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.MonthLastDay, Interval = 2 }, null!, "monthlast;INTERVAL=2"};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.SeasonLastDay }, null!, "season"};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.LegalWorkDay }, null!, "legalworkday"};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.LunarYearly, Interval = 2 }, null!, "lunaryear;INTERVAL=2"};
        yield return new object[] {new RecurrenceRule { Kind = RepeatKind.Ebbinghaus, Until = new DateOnly(2027, 1, 1) }, null!, "ebbinghaus;UNTIL=20270101"};
    }

    [Theory]
    [MemberData(nameof(RoundTripRules))]
    public void Columns_round_trip(RecurrenceRule rule, string? expectedRRule, string? expectedExt)
    {
        var (rr, ext) = rule.ToColumns(new DateOnly(2026, 11, 8));
        Assert.Equal(expectedRRule, rr);
        Assert.Equal(expectedExt, ext);
        Assert.True(RecurrenceRule.TryParse(rr, ext, out var back));
        Assert.Equal(rule, back);
    }

    [Fact]
    public void Monthly_by_weekday_is_written_with_the_weekday_and_parses_to_the_same_expansion()
    {
        var start = D(2026, 11, 10);                                                        // second Tuesday
        var rule = new RecurrenceRule { Kind = RepeatKind.Monthly, Monthly = MonthlyMode.ByWeekday };
        var (rr, _) = rule.ToColumns(start);
        Assert.Equal("FREQ=MONTHLY;BYDAY=2TU", rr);
        Assert.True(RecurrenceRule.TryParse(rr, null, out var back));
        Assert.Equal(First(rule, start, 6), First(back, start, 6));

        var last = new DateOnly(2026, 11, 30);                                              // a 5th Monday = last Monday
        Assert.Equal("FREQ=MONTHLY;BYDAY=-1MO", rule.ToColumns(last).RRule);
    }

    [Fact]
    public void None_has_no_columns_and_empty_columns_parse_to_none()
    {
        Assert.Equal((null, null), RecurrenceRule.None.ToColumns(D(2026, 1, 1)));
        Assert.True(RecurrenceRule.TryParse(null, null, out var r)); Assert.Equal(RecurrenceRule.None, r);
        Assert.True(RecurrenceRule.TryParse("", "  ", out r)); Assert.Equal(RecurrenceRule.None, r);
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=10")]
    [InlineData("freq=daily;count=3")]
    [InlineData("FREQ=YEARLY;UNTIL=20301231T000000Z")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR")]
    public void Standard_rrule_text_is_accepted(string text)
    {
        Assert.True(RecurrenceRule.TryParse(text, null, out var r));
        Assert.True(r.IsRepeating);
    }

    [Theory]
    [InlineData("FREQ=HOURLY")] [InlineData("FREQ=DAILY;INTERVAL=0")] [InlineData("FREQ=DAILY;COUNT=-3")]
    [InlineData("FREQ=DAILY;BYDAY=XX")] [InlineData("FOO=1")] [InlineData("garbage")] [InlineData("FREQ=DAILY;UNTIL=soon")]
    [InlineData("INTERVAL=2")] [InlineData("FREQ=MONTHLY;BYDAY=9MO")]
    public void Unrecognised_text_is_rejected_and_yields_none(string text)
    {
        Assert.False(RecurrenceRule.TryParse(text, null, out var r));
        Assert.Equal(RecurrenceRule.None, r);
        Assert.False(RecurrenceRule.TryParse(null, text, out _));
    }

    [Fact]
    public void Extended_keywords_are_case_insensitive()
    {
        Assert.True(RecurrenceRule.TryParse(null, "WEEKDAY;count=5", out var r));
        Assert.Equal(new RecurrenceRule { Kind = RepeatKind.Weekday, Count = 5 }, r);
    }

    [Fact]
    public void Garbage_columns_on_an_event_mean_non_repeating()
    {
        var e = new EventEntry("1", "t", "", "2026-11-08", null, true, null, "FREQ=NOPE", null, 0, null, 1, 1);
        Assert.Equal(RecurrenceRule.None, RecurrenceRule.From(e));
    }

    [Fact]
    public void Every_kind_terminates_when_the_consumer_stops_early()
    {
        foreach (var kind in Enum.GetValues<RepeatKind>())
        {
            var rule = new RecurrenceRule { Kind = kind, Interval = 7 };
            var got = RecurrenceExpander.Between(rule, D(2026, 11, 8), D(2026, 1, 1), D(2030, 12, 31));
            Assert.Equal(got.OrderBy(x => x).ToList(), got);
        }
    }
}

public class EventOccurrenceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static EventEntry Ev(string id, string title, string start, string? end = null, string? rrule = null, string? ext = null, bool deleted = false) =>
        new(id, title, "", start, end, !start.Contains('T'), null, rrule, ext, 0, null, 1, 1, deleted);

    private static List<EventOccurrence> Expand(IEnumerable<EventEntry> events, DateOnly from, DateOnly to,
                                                IReadOnlyDictionary<string, IReadOnlySet<DateOnly>>? skips = null) =>
        EventOccurrences.Expand(events, skips, from, to);

    [Theory]
    [InlineData("2026-11-08", 2026, 11, 8, false)]
    [InlineData("2026-11-08T09:30", 2026, 11, 8, true)]
    [InlineData("2026-11-08T09:30:45", 2026, 11, 8, true)]
    public void Event_time_parses_all_forms(string text, int y, int m, int d, bool timed)
    {
        Assert.True(EventTime.TryParse(text, out var date, out var time));
        Assert.Equal(D(y, m, d), date);
        Assert.Equal(timed, time is not null);
    }

    [Theory]
    [InlineData("")] [InlineData("tomorrow")] [InlineData("2026-13-01")] [InlineData("2026-11-08 09:30")] [InlineData(null)]
    public void Event_time_rejects_garbage(string? text) => Assert.False(EventTime.TryParse(text, out _, out _));

    [Fact]
    public void Format_round_trips()
    {
        Assert.Equal("2026-11-08", EventTime.Format(D(2026, 11, 8), null));
        Assert.Equal("2026-11-08T09:05", EventTime.Format(D(2026, 11, 8), new TimeOnly(9, 5)));
    }

    [Fact]
    public void Single_events_appear_only_in_range_and_sort_all_day_first()
    {
        var occ = Expand([Ev("a", "회의", "2026-11-09T14:00"), Ev("b", "하루 종일", "2026-11-09"), Ev("c", "아침", "2026-11-09T08:00"),
                          Ev("d", "범위 밖", "2026-12-01")], D(2026, 11, 1), D(2026, 11, 30));
        Assert.Equal(["하루 종일", "아침", "회의"], occ.Select(o => o.Event.Title));
    }

    [Fact]
    public void Recurring_events_expand_and_skipped_days_are_removed()
    {
        var e = Ev("w", "주간", "2026-11-02T10:00", rrule: "FREQ=WEEKLY");
        var skips = new Dictionary<string, IReadOnlySet<DateOnly>> { ["w"] = new HashSet<DateOnly> { D(2026, 11, 16) } };
        var days = Expand([e], D(2026, 11, 1), D(2026, 11, 30), skips).Select(o => o.Date);
        Assert.Equal([D(2026, 11, 2), D(2026, 11, 9), D(2026, 11, 23), D(2026, 11, 30)], days);
    }

    [Fact]
    public void Multi_day_events_cover_every_day_and_show_when_started_before_the_range()
    {
        var e = Ev("t", "출장", "2026-10-30", "2026-11-03");                                  // all-day, 5 days
        var occ = Expand([e], D(2026, 11, 1), D(2026, 11, 30)).Single();
        Assert.Equal(D(2026, 10, 30), occ.Date);
        Assert.Equal(D(2026, 11, 3), occ.EndDate);
        Assert.True(occ.IsMultiDay && occ.IsAllDay);
        Assert.True(occ.CoversDay(D(2026, 11, 2))); Assert.False(occ.CoversDay(D(2026, 11, 4)));
        Assert.Empty(Expand([e], D(2026, 11, 4), D(2026, 11, 30)));
    }

    [Fact]
    public void Recurring_multi_day_events_include_instances_that_started_before_the_range()
    {
        var e = Ev("m", "워크숍", "2026-10-30", "2026-11-02", rrule: "FREQ=MONTHLY");          // Oct 30-Nov 2, Nov 30-Dec 3, ...
        var occ = Expand([e], D(2026, 11, 1), D(2026, 11, 1));
        Assert.Equal([D(2026, 10, 30)], occ.Select(o => o.Date));
    }

    [Fact]
    public void End_time_before_start_on_the_same_day_is_ignored()
    {
        var occ = Expand([Ev("x", "t", "2026-11-09T10:00", "2026-11-09T09:00")], D(2026, 11, 9), D(2026, 11, 9)).Single();
        Assert.Null(occ.EndTime);
        var ok = Expand([Ev("y", "t", "2026-11-09T10:00", "2026-11-09T11:30")], D(2026, 11, 9), D(2026, 11, 9)).Single();
        Assert.Equal(new TimeOnly(11, 30), ok.EndTime);
    }

    [Fact]
    public void Deleted_events_and_unreadable_dates_are_ignored_not_thrown()
    {
        var occ = Expand([Ev("a", "ok", "2026-11-09"), Ev("b", "deleted", "2026-11-09", deleted: true), Ev("c", "bad", "soon")],
            D(2026, 11, 1), D(2026, 11, 30));
        Assert.Equal(["ok"], occ.Select(o => o.Event.Title));
    }

    [Fact]
    public void Extended_rules_work_through_the_event_columns()
    {
        var e = Ev("e", "복습", "2026-11-08", ext: "ebbinghaus");
        var days = Expand([e], D(2026, 11, 1), D(2026, 12, 31)).Select(o => o.Date.DayNumber - D(2026, 11, 8).DayNumber);
        Assert.Equal([0, 1, 2, 4, 7, 15, 30], days);
    }
}

public class ReminderPlannerTests
{
    private static Reminder R(int offset, string? custom = null) => new("r", ReminderOwner.Event, "e", offset, custom, null, null);
    private static ReminderSubject Timed(string start, string? rrule = null, params DateOnly[] skips)
    {
        EventTime.TryParse(start, out var d, out var t);
        RecurrenceRule.TryParse(rrule, null, out var rule);
        return new ReminderSubject(d, t, rule, new HashSet<DateOnly>(skips));
    }

    [Fact]
    public void Timed_event_fires_the_offset_before_its_start()
    {
        var s = Timed("2026-11-09T09:30");
        Assert.Equal(new DateTime(2026, 11, 9, 9, 20, 0), ReminderPlanner.NextTrigger(s, R(10), new DateTime(2026, 11, 9, 9, 0, 0)));
        Assert.Null(ReminderPlanner.NextTrigger(s, R(10), new DateTime(2026, 11, 9, 9, 20, 0)));            // strictly after
        Assert.Equal(new DateTime(2026, 11, 9, 9, 30, 0), ReminderPlanner.NextTrigger(s, R(0), new DateTime(2026, 11, 9, 9, 29, 59)));
    }

    [Fact]
    public void All_day_reminders_use_the_custom_time_or_nine_oclock()
    {
        var s = Timed("2026-11-09");
        Assert.Equal(new DateTime(2026, 11, 8, 9, 0, 0), ReminderPlanner.NextTrigger(s, R(1440), new DateTime(2026, 11, 1)));      // day before, default 09:00
        Assert.Equal(new DateTime(2026, 11, 8, 8, 0, 0), ReminderPlanner.NextTrigger(s, R(1440, "08:00"), new DateTime(2026, 11, 1)));
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), ReminderPlanner.NextTrigger(s, R(0, "bogus"), new DateTime(2026, 11, 1)));   // bad custom time -> default
        Assert.Equal(new DateTime(2026, 11, 2, 7, 30, 0), ReminderPlanner.NextTrigger(s, R(7 * 1440 + 90, "09:00"), new DateTime(2026, 11, 1))); // 1 week + 90 min earlier
    }

    [Fact]
    public void Recurring_events_return_the_next_occurrences_trigger_and_honour_skips()
    {
        var s = Timed("2026-11-09T09:30", "FREQ=DAILY");
        Assert.Equal(new DateTime(2026, 11, 10, 9, 20, 0), ReminderPlanner.NextTrigger(s, R(10), new DateTime(2026, 11, 9, 9, 25, 0)));
        var skipped = Timed("2026-11-09T09:30", "FREQ=DAILY", new DateOnly(2026, 11, 10));
        Assert.Equal(new DateTime(2026, 11, 11, 9, 20, 0), ReminderPlanner.NextTrigger(skipped, R(10), new DateTime(2026, 11, 9, 9, 25, 0)));
    }

    [Fact]
    public void Large_offsets_skip_occurrences_whose_trigger_has_already_passed()
    {
        var s = Timed("2026-11-02T10:00", "FREQ=WEEKLY");                                        // Mondays
        // trigger = 3 days before. After Thu 11/5 the next one is for Mon 11/16 (Fri 11/13), not the Mon 11/9 whose trigger was Fri 11/6...
        Assert.Equal(new DateTime(2026, 11, 6, 10, 0, 0), ReminderPlanner.NextTrigger(s, R(3 * 1440), new DateTime(2026, 11, 5)));
        Assert.Equal(new DateTime(2026, 11, 13, 10, 0, 0), ReminderPlanner.NextTrigger(s, R(3 * 1440), new DateTime(2026, 11, 6, 10, 0, 0)));
    }

    [Fact]
    public void Series_that_ended_or_lie_beyond_the_horizon_return_null()
    {
        Assert.Null(ReminderPlanner.NextTrigger(Timed("2026-11-09T09:30", "FREQ=DAILY;COUNT=2"), R(0), new DateTime(2026, 11, 10, 9, 30, 0)));
        Assert.Null(ReminderPlanner.NextTrigger(Timed("2031-01-01T09:00"), R(0), new DateTime(2026, 11, 1)));             // > 800 days away
        Assert.NotNull(ReminderPlanner.NextTrigger(Timed("2031-01-01T09:00"), R(0), new DateTime(2026, 11, 1), horizonDays: 3000));
    }

    [Fact]
    public void Negative_offsets_are_treated_as_zero()
    {
        Assert.Equal(new DateTime(2026, 11, 9, 9, 30, 0), ReminderPlanner.NextTrigger(Timed("2026-11-09T09:30"), R(-5), new DateTime(2026, 11, 9, 9, 0, 0)));
    }

    [Fact]
    public void Subject_from_event_and_invalid_start()
    {
        var e = new EventEntry("1", "t", "", "2026-11-09T09:30", null, false, null, "FREQ=WEEKLY", null, 0, null, 1, 1);
        var s = ReminderPlanner.Subject(e, null);
        Assert.Equal(new TimeOnly(9, 30), s.StartTime);
        Assert.Equal(RepeatKind.Weekly, s.Rule.Kind);
        Assert.Throws<ArgumentException>(() => ReminderPlanner.Subject(e with { Start = "soon" }, null));
    }

    [Fact]
    public void Clock_parsing()
    {
        Assert.True(ReminderPlanner.TryParseClock("07:05", out var t)); Assert.Equal(new TimeOnly(7, 5), t);
        Assert.True(ReminderPlanner.TryParseClock("7:05", out _));
        Assert.False(ReminderPlanner.TryParseClock("25:00", out _));
        Assert.False(ReminderPlanner.TryParseClock(null, out _));
    }
}
