using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Tests;

/// <summary>Day-cell item options: repeat (one real item per occurrence) and the D-day an item counts down to.</summary>
public class ItemOptionsTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9);

    private static ItemSeriesRepository Series(DbFixture f, Func<DateOnly, bool>? dayOff = null) => new(f.Db, f.Clock, dayOff);

    [Fact]
    public void Weekly_repeat_creates_an_item_per_week_for_a_year_and_copies_look_and_reminders()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-09", "챌린지 제출", "#FFE066");
        f.Items.SetTextColor(item.Id, "#FF6B6B");
        f.Reminders.Add(ReminderOwner.Item, item.Id, 1440, "09:15");

        int made = Series(f).SetRepeat(item.Id, new RecurrenceRule { Kind = RepeatKind.Weekly }, Monday);

        var all = f.Items.GetRange("2026-11-09", "2028-01-01");
        Assert.Equal(made + 1, all.Count);
        Assert.InRange(made, 52, 53);
        Assert.All(all, i => Assert.Equal(DayOfWeek.Monday, DateOnly.ParseExact(i.Day, "yyyy-MM-dd").DayOfWeek));
        Assert.All(all, i => Assert.Equal(all[0].SeriesId, i.SeriesId));
        Assert.NotNull(all[0].SeriesId);

        var later = all[5];
        Assert.Equal("챌린지 제출", later.Content);
        Assert.Equal("#FFE066", later.BgColor);
        Assert.Equal("#FF6B6B", later.TextColor);
        Assert.False(later.Done);
        var rem = Assert.Single(f.Reminders.GetByOwner(ReminderOwner.Item, later.Id));
        Assert.Equal((1440, "09:15"), (rem.OffsetMinutes, rem.CustomTime));
    }

    [Fact]
    public void Count_and_until_limit_the_repeat()
    {
        using var f = new DbFixture();
        var a = f.Items.Add("2026-11-09", "열 번");
        Series(f).SetRepeat(a.Id, new RecurrenceRule { Kind = RepeatKind.Daily, Count = 10 }, Monday);
        Assert.Equal(10, f.Items.GetRange("2026-11-01", "2030-01-01").Count(i => i.Content == "열 번"));

        var b = f.Items.Add("2026-11-09", "이달 말까지");
        Series(f).SetRepeat(b.Id, new RecurrenceRule { Kind = RepeatKind.Weekly, Until = new DateOnly(2026, 11, 30) }, Monday);
        Assert.Equal(["2026-11-09", "2026-11-16", "2026-11-23", "2026-11-30"],
            f.Items.GetRange("2026-11-01", "2030-01-01").Where(i => i.Content == "이달 말까지").Select(i => i.Day));
    }

    [Fact]
    public void Reapplying_a_rule_replaces_the_later_occurrences_but_keeps_earlier_ones()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-09", "스터디");
        var repo = Series(f);
        repo.SetRepeat(item.Id, new RecurrenceRule { Kind = RepeatKind.Daily, Count = 5 }, Monday);

        var third = f.Items.GetByDay("2026-11-11").Single();
        f.Items.SetDone(f.Items.GetByDay("2026-11-10").Single().Id, true);
        repo.SetRepeat(third.Id, new RecurrenceRule { Kind = RepeatKind.Weekly, Count = 3 }, Monday);

        var days = f.Items.GetRange("2026-11-01", "2030-01-01").Select(i => i.Day).ToList();
        Assert.Equal(["2026-11-09", "2026-11-10", "2026-11-11", "2026-11-18", "2026-11-25"], days);
        Assert.True(f.Items.GetByDay("2026-11-10").Single().Done);          // the past stays as it was
    }

    [Fact]
    public void Clearing_the_repeat_removes_later_occurrences_and_their_reminders()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-09", "그만두기");
        f.Reminders.Add(ReminderOwner.Item, item.Id, 0, "09:00");
        var repo = Series(f);
        repo.SetRepeat(item.Id, new RecurrenceRule { Kind = RepeatKind.Daily, Count = 4 }, Monday);
        Assert.Equal(4, f.Reminders.ListAll().Count);

        repo.ClearRepeat(f.Items.GetByDay("2026-11-10").Single().Id);

        Assert.Equal(["2026-11-09", "2026-11-10"], f.Items.GetRange("2026-11-01", "2030-01-01").Select(i => i.Day));
        Assert.All(f.Items.GetRange("2026-11-01", "2030-01-01"), i => Assert.Null(i.SeriesId));
        Assert.Equal(2, f.Reminders.ListAll().Count);
    }

    [Fact]
    public void Extend_tops_up_an_endless_series_as_time_passes_and_leaves_finished_ones_alone()
    {
        using var f = new DbFixture();
        var endless = f.Items.Add("2026-11-09", "매주");
        var finite = f.Items.Add("2026-11-09", "세 번");
        var repo = Series(f);
        repo.SetRepeat(endless.Id, new RecurrenceRule { Kind = RepeatKind.Weekly }, Monday);
        repo.SetRepeat(finite.Id, new RecurrenceRule { Kind = RepeatKind.Weekly, Count = 3 }, Monday);

        Assert.Equal(0, repo.Extend(Monday));                               // nothing new on the same day

        int before = f.Items.GetRange("2026-11-01", "2040-01-01").Count(i => i.Content == "매주");
        int added = repo.Extend(Monday.AddDays(120));
        Assert.InRange(added, 16, 18);                                      // about 17 more weeks
        Assert.Equal(before + added, f.Items.GetRange("2026-11-01", "2040-01-01").Count(i => i.Content == "매주"));
        Assert.Equal(3, f.Items.GetRange("2026-11-01", "2040-01-01").Count(i => i.Content == "세 번"));
    }

    [Fact]
    public void Legal_work_day_repeat_skips_days_off()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-09", "출근 체크");
        Series(f, d => d == new DateOnly(2026, 11, 11)).SetRepeat(item.Id, new RecurrenceRule { Kind = RepeatKind.LegalWorkDay, Count = 4 }, Monday);
        Assert.Equal(["2026-11-09", "2026-11-10", "2026-11-12", "2026-11-13"], f.Items.GetRange("2026-11-01", "2030-01-01").Select(i => i.Day));
    }

    [Fact]
    public void Repeated_items_are_independent_and_reminders_resolve_in_bulk()
    {
        using var f = new DbFixture();
        f.Clock.NowMs = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();     // reminders are armed "now"
        var item = f.Items.Add("2026-11-09", "독립");
        f.Reminders.Add(ReminderOwner.Item, item.Id, 0, "09:00", armedAtMs: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        Series(f).SetRepeat(item.Id, new RecurrenceRule { Kind = RepeatKind.Daily, Count = 3 }, Monday);

        f.Items.SetDone(f.Items.GetByDay("2026-11-10").Single().Id, true);
        f.Items.SoftDelete(f.Items.GetByDay("2026-11-11").Single().Id);
        Assert.False(f.Items.GetByDay("2026-11-09").Single().Done);

        var many = f.Items.GetMany(f.Reminders.ListAll().Select(r => r.OwnerId));
        Assert.Equal(3, many.Count);

        var due = new ReminderProcessor(f.Db, f.Clock).CollectDue(new DateTime(2026, 11, 10, 10, 0, 0), TimeSpan.FromDays(30));
        Assert.Equal(["2026-11-09", "2026-11-10"], due.Select(m => m.Occurrence.ToString("yyyy-MM-dd")).Order());   // the deleted one does not ring
    }

    [Fact]
    public void Countdown_link_follows_the_item_text_day_and_deletion()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-09", "수료식");
        var cd = f.Countdowns.Add("수료식", "2026-11-09", CountdownType.Festival, showCard: true);
        Assert.True(f.Items.SetCountdown(item.Id, cd.Id));
        Assert.Equal(cd.Id, f.Items.Get(item.Id)!.CountdownId);

        f.Items.ReplaceDay("2026-11-09", ["수료식 (본관)"]);                 // edited line: same item, the D-day takes the new text
        Assert.Equal("수료식 (본관)", f.Countdowns.List().Single().Title);

        f.Items.Move(item.Id, "2026-11-20");
        Assert.Equal("2026-11-20", f.Countdowns.List().Single().Date);

        f.Items.ReplaceDay("2026-11-20", []);                               // line removed: the D-day goes with it
        Assert.Empty(f.Countdowns.List());
    }

    [Fact]
    public void Soft_delete_through_the_menu_drops_the_countdown_too()
    {
        using var f = new DbFixture();
        var item = f.Items.Add("2026-11-09", "시험");
        f.Items.SetCountdown(item.Id, f.Countdowns.Add("시험", "2026-11-09", CountdownType.Exam).Id);
        Assert.True(f.Items.SoftDelete(item.Id));
        Assert.Empty(f.Countdowns.List());
    }
}
