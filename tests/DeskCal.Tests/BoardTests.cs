using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Tests;

public class BoardLayoutTests
{
    // 2026-11-01 is a Sunday (2026-01-01 is a Thursday); November has 30 days.
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly DateOnly Today = new(2026, 11, 8);

    private static BoardGrid Build(DateOnly anchor, BoardSettings s, DateOnly? today = null) =>
        BoardLayout.Build(anchor, today ?? Today, s);

    private static IEnumerable<BoardCell> Cells(BoardGrid g) => g.Rows.SelectMany(r => r.Cells);

    // ---- Fixed month -------------------------------------------------------

    [Fact]
    public void Fixed_month_sunday_start_has_5_rows_and_blank_tail()
    {
        var g = Build(new DateOnly(2026, 11, 15), new BoardSettings { Mode = DisplayMode.FixedMonth });
        Assert.Equal(5, g.Rows.Count);
        Assert.Equal(7, g.Columns);
        Assert.Equal(Nov1, g.Rows[0].Cells[0].Date);
        Assert.Equal(Nov1, g.TitleStart);
        Assert.Equal(new DateOnly(2026, 11, 30), g.TitleEnd);
        var last = g.Rows[4].Cells;
        Assert.False(last[0].IsBlank); Assert.False(last[1].IsBlank);     // Nov 29, 30
        Assert.All(last.Skip(2), c => Assert.True(c.IsBlank));            // spill into December is blank
        Assert.Equal(30, Cells(g).Count(c => !c.IsBlank));
    }

    [Fact]
    public void Fixed_month_monday_start_needs_6_rows_and_starts_in_october()
    {
        var g = Build(Nov1, new BoardSettings { Mode = DisplayMode.FixedMonth, FirstDayOfWeek = DayOfWeek.Monday });
        Assert.Equal(6, g.Rows.Count);                                    // 6 leading + 30 days = 36 cells
        Assert.Equal(new DateOnly(2026, 10, 26), g.Rows[0].Cells[0].Date);
        Assert.Equal(6, g.Rows[0].Cells.Count(c => c.IsBlank));           // Oct 26-31 precede Sunday Nov 1
        Assert.Equal(DayOfWeek.Monday, g.HeaderDays[0]);
        Assert.Equal(DayOfWeek.Sunday, g.HeaderDays[6]);
    }

    [Fact]
    public void Fixed_month_february_2026_is_exactly_4_rows()
    {
        var g = Build(new DateOnly(2026, 2, 10), new BoardSettings { Mode = DisplayMode.FixedMonth });
        Assert.Equal(4, g.Rows.Count);                                    // Feb 1 is a Sunday, 28 days
        Assert.DoesNotContain(Cells(g), c => c.IsBlank);
    }

    // ---- Full month --------------------------------------------------------

    [Fact]
    public void Full_month_is_always_6_rows_with_dimmed_neighbours()
    {
        foreach (var month in new[] { new DateOnly(2026, 2, 1), new DateOnly(2026, 11, 1), new DateOnly(2027, 5, 1) })
        {
            var g = Build(month, new BoardSettings { Mode = DisplayMode.FullMonth });
            Assert.Equal(6, g.Rows.Count);
            Assert.DoesNotContain(Cells(g), c => c.IsBlank);
            Assert.Equal(DateTime.DaysInMonth(month.Year, month.Month), Cells(g).Count(c => c.InAnchorMonth));
        }
        var nov = Build(Nov1, new BoardSettings { Mode = DisplayMode.FullMonth });
        Assert.Equal(new DateOnly(2026, 12, 12), nov.Rows[5].Cells[6].Date);
        Assert.False(nov.Rows[5].Cells[6].InAnchorMonth);
    }

    // ---- Floating month ----------------------------------------------------

    [Fact]
    public void Floating_month_starts_at_the_anchors_week_and_flags_month_starts()
    {
        var g = Build(new DateOnly(2026, 11, 11), new BoardSettings { Mode = DisplayMode.FloatingMonth, MaxRows = 4 });
        Assert.Equal(4, g.Rows.Count);
        Assert.Equal(new DateOnly(2026, 11, 8), g.Rows[0].Cells[0].Date);   // Sunday of that week
        Assert.Equal(new DateOnly(2026, 12, 5), g.TitleEnd);
        Assert.All(Cells(g), c => { Assert.True(c.InAnchorMonth); Assert.False(c.IsBlank); });
        Assert.Equal([new DateOnly(2026, 12, 1)], Cells(g).Where(c => c.IsMonthStart).Select(c => c.Date));
    }

    // ---- Single line -------------------------------------------------------

    [Fact]
    public void Single_line_shows_every_day_of_the_month_including_leap_february()
    {
        var g = Build(new DateOnly(2028, 2, 20), new BoardSettings { Mode = DisplayMode.SingleLine }, new DateOnly(2028, 2, 29));
        Assert.Single(g.Rows);
        Assert.Equal(29, g.Columns);
        Assert.Equal(29, g.HeaderDays.Count);
        Assert.Equal(new DateOnly(2028, 2, 1), g.Rows[0].Cells[0].Date);
        Assert.True(g.Rows[0].Cells[28].IsToday);
        Assert.Null(g.Rows[0].WeekNumber);
    }

    [Fact]
    public void Single_line_floating_starts_at_the_anchor_and_spans_line_days()
    {
        var g = Build(new DateOnly(2026, 12, 28), new BoardSettings { Mode = DisplayMode.SingleLineFloating, LineDays = 10 });
        Assert.Equal(10, g.Columns);
        Assert.Equal(new DateOnly(2026, 12, 28), g.Rows[0].Cells[0].Date);
        Assert.Equal(new DateOnly(2027, 1, 6), g.TitleEnd);                 // crosses the year boundary
        Assert.Equal(DayOfWeek.Monday, g.HeaderDays[0]);
    }

    // ---- Navigation --------------------------------------------------------

    [Fact]
    public void Step_moves_by_month_in_month_modes_and_handles_year_boundaries()
    {
        var s = new BoardSettings { Mode = DisplayMode.FixedMonth };
        Assert.Equal(new DateOnly(2026, 12, 1), BoardLayout.Step(new DateOnly(2026, 11, 15), +1, s));
        Assert.Equal(new DateOnly(2026, 10, 1), BoardLayout.Step(new DateOnly(2026, 11, 15), -1, s));
        Assert.Equal(new DateOnly(2027, 1, 1), BoardLayout.Step(new DateOnly(2026, 12, 31), +1, s));
        Assert.Equal(new DateOnly(2025, 12, 1), BoardLayout.Step(new DateOnly(2026, 1, 31), -1, s));
        Assert.Equal(new DateOnly(2026, 12, 1), BoardLayout.Step(new DateOnly(2026, 11, 15), +50, s)); // only the sign counts
        Assert.Equal(new DateOnly(2026, 11, 15), BoardLayout.Step(new DateOnly(2026, 11, 15), 0, new BoardSettings { Mode = DisplayMode.FloatingMonth }));
    }

    [Fact]
    public void Step_moves_by_page_in_floating_modes()
    {
        var anchor = new DateOnly(2026, 11, 8);
        Assert.Equal(anchor.AddDays(7 * 5), BoardLayout.Step(anchor, +1, new BoardSettings { Mode = DisplayMode.FloatingMonth, MaxRows = 5 }));
        Assert.Equal(anchor.AddDays(-14), BoardLayout.Step(anchor, -1, new BoardSettings { Mode = DisplayMode.SingleLineFloating, LineDays = 14 }));
    }

    // ---- Week numbers ------------------------------------------------------

    [Fact]
    public void Iso_week_numbers_handle_week_53_and_year_start()
    {
        var s = new BoardSettings { Mode = DisplayMode.FloatingMonth, FirstDayOfWeek = DayOfWeek.Monday, ShowWeekNumbers = true, WeekRule = WeekNumberRule.Iso };
        Assert.Equal(53, BoardLayout.WeekNumber(new DateOnly(2026, 12, 28), s));  // Thursday 2026-12-31 -> W53
        Assert.Equal(1, BoardLayout.WeekNumber(new DateOnly(2027, 1, 4), s));
        Assert.Equal(46, BoardLayout.WeekNumber(new DateOnly(2026, 11, 9), s));
        var g = Build(new DateOnly(2026, 12, 28), s with { MaxRows = 2 });
        Assert.Equal([53, 1], g.Rows.Select(r => r.WeekNumber!.Value));
        Assert.True(g.HasWeekNumbers);
    }

    [Fact]
    public void First_day_of_year_rule_makes_the_row_containing_jan_1_week_1()
    {
        var s = new BoardSettings { FirstDayOfWeek = DayOfWeek.Sunday, WeekRule = WeekNumberRule.FirstDayOfYear };
        Assert.Equal(1, BoardLayout.WeekNumber(new DateOnly(2026, 12, 27), s));   // Dec 27 - Jan 2 contains Jan 1 2027
        Assert.Equal(1, BoardLayout.WeekNumber(new DateOnly(2025, 12, 28), s));   // Dec 28 - Jan 3 contains Jan 1 2026
        Assert.Equal(2, BoardLayout.WeekNumber(new DateOnly(2026, 1, 4), s));
        Assert.Equal(46, BoardLayout.WeekNumber(new DateOnly(2026, 11, 8), s));
    }

    [Fact]
    public void Week_numbers_are_omitted_unless_enabled()
    {
        var off = Build(Nov1, new BoardSettings { Mode = DisplayMode.FixedMonth, ShowWeekNumbers = false });
        Assert.All(off.Rows, r => Assert.Null(r.WeekNumber));
        Assert.False(off.HasWeekNumbers);
        var on = Build(Nov1, new BoardSettings { Mode = DisplayMode.FixedMonth, ShowWeekNumbers = true });
        Assert.All(on.Rows, r => Assert.NotNull(r.WeekNumber));
    }

    // ---- Flags -------------------------------------------------------------

    [Fact]
    public void Today_is_flagged_once_when_visible_and_never_otherwise()
    {
        var inRange = Build(Today, new BoardSettings { Mode = DisplayMode.FixedMonth });
        Assert.Equal([Today], Cells(inRange).Where(c => c.IsToday).Select(c => c.Date));
        var elsewhere = Build(new DateOnly(2027, 3, 1), new BoardSettings { Mode = DisplayMode.FixedMonth });
        Assert.DoesNotContain(Cells(elsewhere), c => c.IsToday);
    }

    [Fact]
    public void Off_days_follow_the_mask()
    {
        var def = Build(Nov1, new BoardSettings());
        Assert.All(Cells(def).Where(c => !c.IsBlank), c =>
            Assert.Equal(c.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, c.IsOffDay));
        var friday = new BoardSettings { OffDaysMask = 1 << (int)DayOfWeek.Friday };
        Assert.All(Cells(Build(Nov1, friday)).Where(c => !c.IsBlank), c =>
            Assert.Equal(c.Date.DayOfWeek == DayOfWeek.Friday, c.IsOffDay));
    }

    // ---- Invariants over many inputs ---------------------------------------

    [Fact]
    public void Every_mode_produces_contiguous_dates_for_many_anchors()
    {
        foreach (var mode in Enum.GetValues<DisplayMode>())
        foreach (var fdow in new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Saturday })
        foreach (var anchor in AnchorsAcrossYears())
        {
            var s = new BoardSettings { Mode = mode, FirstDayOfWeek = fdow, ShowWeekNumbers = true };
            var g = Build(anchor, s);
            var all = Cells(g).ToList();
            Assert.All(g.Rows, r => Assert.Equal(g.Columns, r.Cells.Count));
            Assert.Equal(g.Columns, g.HeaderDays.Count);
            for (int i = 1; i < all.Count; i++)
                Assert.Equal(all[i - 1].Date.AddDays(1), all[i].Date);
            Assert.InRange(g.Rows.Count, 1, 8);
            Assert.True(g.TitleStart <= g.TitleEnd);
            if (mode == DisplayMode.FixedMonth)
                Assert.Equal(DateTime.DaysInMonth(anchor.Year, anchor.Month), all.Count(c => !c.IsBlank));
        }
    }

    private static IEnumerable<DateOnly> AnchorsAcrossYears()
    {
        for (var d = new DateOnly(2024, 1, 1); d < new DateOnly(2029, 1, 1); d = d.AddDays(13)) yield return d;
        yield return new DateOnly(2028, 2, 29); yield return new DateOnly(2026, 12, 31); yield return new DateOnly(2026, 1, 1);
    }

    // ---- Settings sanitising -----------------------------------------------

    [Fact]
    public void Sanitized_clamps_out_of_range_values()
    {
        var s = new BoardSettings
        {
            MaxRows = 99, LineDays = 1, FontSize = 1000, BackgroundOpacity = 5, CellGap = -3, OffDaysMask = 0xFFFF,
            Mode = (DisplayMode)42, FirstDayOfWeek = (DayOfWeek)9, WeekRule = (WeekNumberRule)7, Bullet = (BulletStyle)9,
        }.Sanitized();
        Assert.Equal(8, s.MaxRows); Assert.Equal(7, s.LineDays); Assert.Equal(48, s.FontSize);
        Assert.Equal(1.0, s.BackgroundOpacity); Assert.Equal(0, s.CellGap); Assert.Equal(0x7F, s.OffDaysMask);
        Assert.Equal(DisplayMode.FixedMonth, s.Mode); Assert.Equal(DayOfWeek.Sunday, s.FirstDayOfWeek);
        Assert.Equal(WeekNumberRule.Iso, s.WeekRule); Assert.Equal(BulletStyle.Dot, s.Bullet);
        // Build must not throw even with garbage input.
        var g = BoardLayout.Build(Nov1Public, Nov1Public, new BoardSettings { MaxRows = -5, Mode = (DisplayMode)42 });
        Assert.NotEmpty(g.Rows);
    }

    private static readonly DateOnly Nov1Public = new(2026, 11, 1);
}

public class BoardSettingsStoreTests
{
    [Fact]
    public void Empty_database_loads_defaults()
    {
        using var f = new DbFixture();
        Assert.Equal(new BoardSettings(), new BoardSettingsStore(f.Settings).Load());
    }

    [Fact]
    public void Save_then_load_roundtrips_every_field()
    {
        using var f = new DbFixture();
        var store = new BoardSettingsStore(f.Settings);
        var custom = new BoardSettings
        {
            Bullet = BulletStyle.Number, ShowCompleted = false, CompleteSound = false,
            Mode = DisplayMode.SingleLineFloating, FirstDayOfWeek = DayOfWeek.Monday, MaxRows = 6, LineDays = 21,
            ShowWeekNumbers = true, WeekRule = WeekNumberRule.FirstDayOfYear, OffDaysMask = 1 << (int)DayOfWeek.Sunday,
            ShowOffDays = false, FontFamily = "맑은 고딕", FontSize = 17.5, TextColor = "#EEEEEE", OffDayColor = "#FF0000",
            BackgroundColor = "#223344", BackgroundOpacity = 0.35, Shadow = false, CellGap = 5,
        };
        store.Save(custom);
        Assert.Equal(custom, store.Load());
    }

    [Fact]
    public void Corrupt_values_fall_back_or_are_clamped()
    {
        using var f = new DbFixture();
        f.Settings.SetString("board.mode", "Nonsense");
        f.Settings.SetString("board.first_dow", "99");
        f.Settings.SetString("board.font_size", "NaN");
        f.Settings.SetString("board.bg_opacity", "∞");
        f.Settings.SetInt("board.max_rows", 999);
        f.Settings.SetString("board.week_numbers", "maybe");
        var s = new BoardSettingsStore(f.Settings).Load();
        Assert.Equal(DisplayMode.FixedMonth, s.Mode);
        Assert.Equal(DayOfWeek.Sunday, s.FirstDayOfWeek);
        Assert.Equal(15, s.FontSize);
        Assert.Equal(0.6, s.BackgroundOpacity);
        Assert.Equal(8, s.MaxRows);
        Assert.False(s.ShowWeekNumbers);
    }

    [Fact]
    public void ResetToDefaults_keeps_window_geometry()
    {
        using var f = new DbFixture();
        var store = new BoardSettingsStore(f.Settings);
        store.Save(new BoardSettings { Mode = DisplayMode.FullMonth, ShowWeekNumbers = true });
        foreach (var k in new[] { "board.x", "board.y", "board.w", "board.h" }) f.Settings.SetInt(k, 11);
        store.ResetToDefaults();
        Assert.Equal(new BoardSettings(), store.Load());
        Assert.Equal(11, f.Settings.GetInt("board.x", 0));
        Assert.Equal(11, f.Settings.GetInt("board.h", 0));
    }
}
