using DeskCal.Core;

namespace DeskCal.Tests;

public class FreeTimeTests
{
    private static readonly DateOnly Mon = new(2026, 11, 9);

    private static BusyInterval B(int day, string from, string to) => new(Mon.AddDays(day), Min(from), Min(to));
    private static int Min(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[3..]);
    private static string[] Show(IEnumerable<FreeSlot> slots) => slots.Select(s => $"{s.Date:MM-dd} {s.Start}-{s.End}").ToArray();
    private static List<FreeSlot> Find(IEnumerable<BusyInterval> busy, int days = 1, string start = "09:00", string end = "18:00", int min = 30, Func<DateOnly, bool>? skip = null) =>
        FreeTimeFinder.Find(Mon, Mon.AddDays(days - 1), Min(start), Min(end), min, busy, skip);

    [Fact]
    public void An_empty_day_is_one_slot_the_length_of_the_window()
    {
        var s = Assert.Single(Find([]));
        Assert.Equal(("09:00", "18:00", 540), (s.Start, s.End, s.Minutes));
    }

    [Fact]
    public void Gaps_between_busy_stretches_are_found_and_short_ones_are_dropped()
    {
        var busy = new[] { B(0, "10:00", "11:00"), B(0, "11:20", "12:00"), B(0, "14:00", "15:00") };
        Assert.Equal(["11-09 09:00-10:00", "11-09 12:00-14:00", "11-09 15:00-18:00"], Show(Find(busy)));          // 11:00-11:20 is under 30 minutes
        Assert.Equal(["11-09 12:00-14:00", "11-09 15:00-18:00"], Show(Find(busy, min: 90)));
    }

    [Fact]
    public void Overlapping_touching_and_unordered_busy_stretches_are_merged()
    {
        var busy = new[] { B(0, "13:00", "15:00"), B(0, "10:00", "12:00"), B(0, "11:00", "13:00"), B(0, "10:30", "11:00") };
        Assert.Equal(["11-09 09:00-10:00", "11-09 15:00-18:00"], Show(Find(busy)));
    }

    [Fact]
    public void Busy_time_outside_the_window_changes_nothing_and_one_that_straddles_it_is_clipped()
    {
        var busy = new[] { B(0, "07:00", "08:00"), B(0, "08:30", "09:30"), B(0, "17:30", "20:00"), B(0, "19:00", "21:00") };
        Assert.Equal(["11-09 09:30-17:30"], Show(Find(busy)));
    }

    [Fact]
    public void A_fully_busy_day_has_no_slot_and_days_can_be_skipped()
    {
        var busy = new[] { B(0, "00:00", "24:00") };
        Assert.Equal(["11-10 09:00-18:00"], Show(Find(busy, days: 2)));
        Assert.Equal(["11-10 09:00-18:00"], Show(Find([], days: 2, skip: d => d == Mon)));
    }

    [Fact]
    public void The_window_can_run_to_the_end_of_the_day()
    {
        var s = Assert.Single(Find([B(0, "09:00", "20:00")], end: "24:00"));
        Assert.Equal(("20:00", "24:00", 240), (s.Start, s.End, s.Minutes));
    }

    [Fact]
    public void Nonsense_windows_give_nothing()
    {
        Assert.Empty(FreeTimeFinder.Find(Mon, Mon, 18 * 60, 9 * 60, 30, []));                 // end before start
        Assert.Empty(FreeTimeFinder.Find(Mon, Mon, 9 * 60, 9 * 60, 30, []));                  // empty window
        Assert.Empty(FreeTimeFinder.Find(Mon.AddDays(1), Mon, 9 * 60, 18 * 60, 30, []));      // range backwards
    }

    // ---- turning events and the timetable into busy stretches ----

    private static EventEntry Ev(string start, string? end = null) =>
        new("e", "x", "", start, end, !start.Contains('T'), null, null, null, 0, null, 0, 0);

    private static EventOccurrence Occ(string start, string? end = null)
    {
        var occ = EventOccurrences.Expand([Ev(start, end)], null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        return Assert.Single(occ);
    }

    [Fact]
    public void A_timed_event_is_busy_from_start_to_end_or_for_an_hour_without_an_end()
    {
        var busy = FreeTimeFinder.BusyFromEvents([Occ("2026-11-09T10:00", "2026-11-09T11:30"), Occ("2026-11-09T14:00")]);
        Assert.Equal([B(0, "10:00", "11:30"), B(0, "14:00", "15:00")], busy);
    }

    [Fact]
    public void An_event_with_no_end_near_midnight_is_cut_at_midnight_and_all_day_events_do_not_block()
    {
        Assert.Equal([B(0, "23:30", "24:00")], FreeTimeFinder.BusyFromEvents([Occ("2026-11-09T23:30")]));
        Assert.Empty(FreeTimeFinder.BusyFromEvents([Occ("2026-11-09", "2026-11-10")]));
    }

    [Fact]
    public void A_timed_event_over_several_days_blocks_the_first_day_from_its_start_whole_days_between_and_the_last_until_its_end()
    {
        var busy = FreeTimeFinder.BusyFromEvents([Occ("2026-11-09T20:00", "2026-11-11T08:00")]);
        Assert.Equal([B(0, "20:00", "24:00"), B(1, "00:00", "24:00"), B(2, "00:00", "08:00")], busy);
    }

    [Fact]
    public void Timetable_squares_are_busy_every_week_in_their_periods()
    {
        var config = new TimetableConfig();                                                           // 09:00, 50-minute classes, 10-minute breaks
        var cells = new[]
        {
            new TimetableCell(DayOfWeek.Monday, 1, "수학", "", null),
            new TimetableCell(DayOfWeek.Monday, 3, "체육", "운동장", null),
            new TimetableCell(DayOfWeek.Wednesday, 2, "영어", "", null),
            new TimetableCell(DayOfWeek.Friday, 99, "없는 교시", "", null),                              // a period the day does not have
            new TimetableCell(DayOfWeek.Friday, 1, "", "", null),                                       // an emptied square
        };
        var busy = FreeTimeFinder.BusyFromTimetable(config, cells, Mon, Mon.AddDays(9));             // Mon 11-09 .. Wed 11-18
        Assert.Equal([B(0, "09:00", "09:50"), B(0, "11:00", "11:50"), B(2, "10:00", "10:50"), B(7, "09:00", "09:50"), B(7, "11:00", "11:50"), B(9, "10:00", "10:50")],
            busy.OrderBy(b => b.Date).ThenBy(b => b.StartMinute));
    }

    [Theory]
    [InlineData("09:00", true, 540)]
    [InlineData("9:05", true, 545)]
    [InlineData("00:00", true, 0)]
    [InlineData("24:00", true, 1440)]
    [InlineData("23:59", true, 1439)]
    [InlineData("25:00", false, 0)]
    [InlineData("9시", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void Times_of_day_are_read_as_minutes(string? text, bool ok, int minutes)
    {
        Assert.Equal(ok, FreeTimeFinder.TryParseMinute(text, out int m));
        if (ok) Assert.Equal(minutes, m);
    }
}
