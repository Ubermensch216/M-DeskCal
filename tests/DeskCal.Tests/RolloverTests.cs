using DeskCal.Ai;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Tests;

public class RolloverPlannerTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);                             // a Monday
    private static bool Off(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static Item It(string id, string day, string text = "x", bool done = false, bool deleted = false, string? series = null, string? countdown = null) =>
        new(id, day, text, null, 0, done, 0, 0, deleted, null, series, countdown);

    [Fact]
    public void Overdue_means_not_done_not_deleted_on_a_past_day_within_thirty_days_oldest_first()
    {
        var found = RolloverPlanner.FindOverdue(
        [
            It("a", "2026-11-13", "보고서"), It("b", "2026-11-10", "세금"), It("today", "2026-11-16"), It("future", "2026-11-17"),
            It("done", "2026-11-12", done: true), It("gone", "2026-11-12", deleted: true),
            It("series", "2026-11-12", series: "S1"), It("cd", "2026-11-12", countdown: "C1"),
            It("old", "2026-10-16"), It("edge", "2026-10-17"), It("bad", "not-a-date"),
        ], Today, new Dictionary<string, int> { ["b"] = 4 });
        Assert.Equal(["edge", "b", "a"], found.Select(o => o.Id));
        Assert.Equal([30, 6, 3], found.Select(o => o.DaysLate));
        Assert.Equal([0, 4, 0], found.Select(o => o.TimesMoved));
    }

    [Fact]
    public void The_first_day_is_today_or_the_next_working_day()
    {
        Assert.Equal(Today, RolloverPlanner.FirstDay(Today, Off));
        Assert.Equal(new DateOnly(2026, 11, 16), RolloverPlanner.FirstDay(new DateOnly(2026, 11, 14), Off));       // Saturday -> Monday
        Assert.Equal(new DateOnly(2026, 11, 16), RolloverPlanner.DayAfter(new DateOnly(2026, 11, 13), Off));        // Friday -> Monday
        Assert.Equal(new DateOnly(2026, 11, 14), RolloverPlanner.NextWorkingDay(new DateOnly(2026, 11, 14), _ => true));   // nothing works: unchanged
    }

    [Fact]
    public void Spreading_fills_the_emptiest_working_days_first_and_skips_days_off()
    {
        var items = Enumerable.Range(1, 6).Select(n => new OverdueItem("i" + n, "2026-11-10", "일" + n, 6, 0)).ToList();
        var moves = RolloverPlanner.Distribute(items, new DateOnly(2026, 11, 19), Off, _ => 0, _ => 0, days: 3);        // Thu, Fri, Mon
        Assert.All(moves, m => Assert.False(Off(m.To)));
        var perDay = moves.GroupBy(m => m.To).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal([new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23)], perDay.Keys.OrderBy(d => d));
        Assert.All(perDay.Values, n => Assert.Equal(2, n));
    }

    [Fact]
    public void A_day_with_open_to_dos_or_a_full_calendar_gets_fewer()
    {
        var items = Enumerable.Range(1, 4).Select(n => new OverdueItem("i" + n, "2026-11-10", "일" + n, 6, 0)).ToList();
        Func<DateOnly, int> open = d => d == new DateOnly(2026, 11, 16) ? 3 : 0;                                         // Monday already has three
        Func<DateOnly, int> busy = d => d == new DateOnly(2026, 11, 17) ? 480 : 0;                                       // Tuesday: eight hours of events
        var moves = RolloverPlanner.Distribute(items, Today, Off, open, busy, days: 3);
        var perDay = moves.GroupBy(m => m.To).ToDictionary(g => g.Key, g => g.Count());
        Assert.False(perDay.ContainsKey(new DateOnly(2026, 11, 16)) && perDay[new DateOnly(2026, 11, 16)] > 1);          // Monday (3 open) is not loaded further
        Assert.True(perDay[new DateOnly(2026, 11, 18)] >= perDay.GetValueOrDefault(new DateOnly(2026, 11, 17)));          // the free Wednesday takes the most
    }

    [Fact]
    public void Spreading_is_deterministic_and_puts_the_most_postponed_first()
    {
        var items = new List<OverdueItem> { new("a", "2026-11-12", "가", 4, 0), new("b", "2026-11-12", "나", 4, 5), new("c", "2026-11-10", "다", 6, 0) };
        var one = RolloverPlanner.Distribute(items, Today, Off, _ => 0, _ => 0, days: 3);
        var two = RolloverPlanner.Distribute(items.AsEnumerable().Reverse().ToList(), Today, Off, _ => 0, _ => 0, days: 3);
        Assert.Equal(one.Select(m => (m.Item.Id, m.To)), two.Select(m => (m.Item.Id, m.To)));
        Assert.Equal(Today, one.Single(m => m.Item.Id == "b").To);                                                        // moved five times: the earliest day
        Assert.Equal(Today.AddDays(1), one.Single(m => m.Item.Id == "c").To);                                             // then the oldest
        Assert.Empty(RolloverPlanner.Distribute([], Today, Off, _ => 0, _ => 0));
    }
}

public class RolloverServiceTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static readonly DateOnly Today = new(2026, 11, 16);

    private RolloverService Service() => new(_f.Items, new ActivityRepository(_f.Db, _f.Clock), _f.Settings, _f.Clock, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, _ => 0);

    [Fact]
    public void Finding_ignores_done_and_future_items_and_counts_earlier_moves()
    {
        var late = _f.Items.Add("2026-11-12", "보고서");
        var done = _f.Items.Add("2026-11-12", "끝낸 일"); _f.Items.SetDone(done.Id, true);
        _f.Items.Add("2026-11-17", "내일 일");
        _f.Items.Move(late.Id, "2026-11-13");                                       // it has been pushed once already
        _f.Items.Move(late.Id, "2026-11-12");
        var found = Service().Find(Today);
        var only = Assert.Single(found);
        Assert.Equal(("보고서", 4, 2), (only.Content, only.DaysLate, only.TimesMoved));
    }

    [Fact]
    public void Applying_moves_only_the_day_and_undo_puts_every_item_back()
    {
        var a = _f.Items.Add("2026-11-12", "A"); var b = _f.Items.Add("2026-11-13", "B");
        var svc = Service();
        var overdue = svc.Find(Today);
        var result = svc.Apply(RolloverPlanner.AllTo(overdue, Today));
        Assert.Equal(2, result.Moved.Count);
        Assert.Equal("2026-11-16", _f.Items.Get(a.Id)!.Day);
        Assert.Equal("A", _f.Items.Get(a.Id)!.Content);
        Assert.Equal(2, svc.Last()!.Moves.Count);

        var undo = svc.UndoLast();
        Assert.Equal((2, 0), (undo.Reverted, undo.Changed));
        Assert.Equal("2026-11-12", _f.Items.Get(a.Id)!.Day);
        Assert.Equal("2026-11-13", _f.Items.Get(b.Id)!.Day);
        Assert.Null(svc.Last());
    }

    [Fact]
    public void Items_changed_in_the_meantime_are_skipped_and_not_put_back_over_the_user_edit()
    {
        var a = _f.Items.Add("2026-11-12", "A"); var b = _f.Items.Add("2026-11-12", "B");
        var svc = Service();
        var plan = RolloverPlanner.AllTo(svc.Find(Today), Today);
        _f.Items.SetDone(b.Id, true);                                                // finished while the window was open
        var result = svc.Apply(plan);
        Assert.Equal((1, 1), (result.Moved.Count, result.Skipped));
        Assert.Equal("2026-11-12", _f.Items.Get(b.Id)!.Day);

        _f.Items.Move(a.Id, "2026-11-20");                                           // the user moved it again by hand
        var undo = svc.UndoLast();
        Assert.Equal((0, 1), (undo.Reverted, undo.Changed));
        Assert.Equal("2026-11-20", _f.Items.Get(a.Id)!.Day);
    }

    [Fact]
    public void A_spread_over_the_next_days_gives_every_item_a_working_day_and_changes_nothing_by_itself()
    {
        for (int i = 0; i < 4; i++) _f.Items.Add("2026-11-1" + i, "일" + i);
        var svc = Service();
        var overdue = svc.Find(new DateOnly(2026, 11, 14));                          // Saturday: the spread starts on Monday
        var spread = svc.Spread(overdue, new DateOnly(2026, 11, 14), days: 2);
        Assert.Equal(4, spread.Count);
        Assert.All(spread, m => Assert.Contains(m.To, new[] { new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17) }));
        Assert.All(overdue, o => Assert.Equal(o.Day, _f.Items.Get(o.Id)!.Day));
    }
}
