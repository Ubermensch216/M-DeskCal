using System.Globalization;

namespace DeskCal.Core;

public readonly record struct BoardCell(
    DateOnly Date,
    /// <summary>False for days that belong to a neighbouring month in month-based modes.</summary>
    bool InAnchorMonth,
    bool IsToday,
    bool IsOffDay,
    /// <summary>True when the cell exists only to keep the grid aligned (Fixed month); draw nothing.</summary>
    bool IsBlank,
    bool IsMonthStart);

public sealed record BoardRow(IReadOnlyList<BoardCell> Cells, int? WeekNumber);

public sealed record BoardGrid(
    DateOnly TitleStart,
    DateOnly TitleEnd,
    int Columns,
    /// <summary>Weekday shown above each column.</summary>
    IReadOnlyList<DayOfWeek> HeaderDays,
    IReadOnlyList<BoardRow> Rows,
    bool HasWeekNumbers);

/// <summary>Pure calendar-board geometry: which dates go in which cell. No UI, no clock, no culture.</summary>
public static class BoardLayout
{
    public static BoardGrid Build(DateOnly anchor, DateOnly today, BoardSettings settings)
    {
        var s = settings.Sanitized();
        return s.Mode switch
        {
            DisplayMode.FloatingMonth => Weeks(anchor, today, s, StartOfWeek(anchor, s.FirstDayOfWeek), s.MaxRows, blankOutside: false, anchor),
            DisplayMode.FixedMonth => Month(anchor, today, s, rows: null, blankOutside: true),
            DisplayMode.FullMonth => Month(anchor, today, s, rows: 6, blankOutside: false),
            DisplayMode.SingleLine => Line(anchor, today, s,
                new DateOnly(anchor.Year, anchor.Month, 1), DateTime.DaysInMonth(anchor.Year, anchor.Month), anchor),
            DisplayMode.SingleLineFloating => Line(anchor, today, s, anchor, s.LineDays, anchor),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>The anchor after "previous page" (-1) or "next page" (+1).</summary>
    public static DateOnly Step(DateOnly anchor, int direction, BoardSettings settings)
    {
        var s = settings.Sanitized();
        int dir = Math.Sign(direction);
        return s.Mode switch
        {
            DisplayMode.FloatingMonth => anchor.AddDays(dir * 7 * s.MaxRows),
            DisplayMode.SingleLineFloating => anchor.AddDays(dir * s.LineDays),
            _ => new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(dir),
        };
    }

    public static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDay)
    {
        int diff = ((int)date.DayOfWeek - (int)firstDay + 7) % 7;
        return date.AddDays(-diff);
    }

    // ---- builders ---------------------------------------------------------

    private static BoardGrid Month(DateOnly anchor, DateOnly today, BoardSettings s, int? rows, bool blankOutside)
    {
        var first = new DateOnly(anchor.Year, anchor.Month, 1);
        int days = DateTime.DaysInMonth(anchor.Year, anchor.Month);
        var start = StartOfWeek(first, s.FirstDayOfWeek);
        int offset = first.DayNumber - start.DayNumber;
        int needed = (offset + days + 6) / 7;
        var last = first.AddDays(days - 1);
        var grid = Weeks(anchor, today, s, start, rows ?? needed, blankOutside, anchor);
        return grid with { TitleStart = first, TitleEnd = last };
    }

    private static BoardGrid Weeks(DateOnly anchor, DateOnly today, BoardSettings s, DateOnly start, int rowCount,
                                   bool blankOutside, DateOnly anchorForMonth)
    {
        bool floating = s.Mode == DisplayMode.FloatingMonth;
        var rows = new List<BoardRow>(rowCount);
        for (int r = 0; r < rowCount; r++)
        {
            var cells = new List<BoardCell>(7);
            for (int c = 0; c < 7; c++)
            {
                var d = start.AddDays(r * 7 + c);
                // In floating mode there is no "current month": everything counts as inside.
                bool inMonth = floating || (d.Year == anchorForMonth.Year && d.Month == anchorForMonth.Month);
                cells.Add(MakeCell(d, today, s, inMonth, blank: blankOutside && !inMonth));
            }
            var rowStart = start.AddDays(r * 7);
            rows.Add(new BoardRow(cells, s.ShowWeekNumbers ? WeekNumber(rowStart, s) : null));
        }
        var header = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)s.FirstDayOfWeek + i) % 7)).ToList();
        var end = start.AddDays(rowCount * 7 - 1);
        // For floating mode the title spans the shown weeks; month modes override this afterwards.
        return new BoardGrid(start, end, 7, header, rows, s.ShowWeekNumbers);
    }

    private static BoardGrid Line(DateOnly anchor, DateOnly today, BoardSettings s, DateOnly start, int count, DateOnly anchorForMonth)
    {
        var cells = new List<BoardCell>(count);
        var header = new List<DayOfWeek>(count);
        for (int i = 0; i < count; i++)
        {
            var d = start.AddDays(i);
            bool inMonth = s.Mode == DisplayMode.SingleLineFloating || (d.Year == anchorForMonth.Year && d.Month == anchorForMonth.Month);
            cells.Add(MakeCell(d, today, s, inMonth, blank: false));
            header.Add(d.DayOfWeek);
        }
        return new BoardGrid(start, start.AddDays(count - 1), count, header, [new BoardRow(cells, null)], HasWeekNumbers: false);
    }

    private static BoardCell MakeCell(DateOnly d, DateOnly today, BoardSettings s, bool inMonth, bool blank) =>
        new(d, inMonth, d == today, s.IsOffDay(d.DayOfWeek), blank, d.Day == 1);

    // ---- week numbers -----------------------------------------------------

    /// <summary>Week number of the 7-day row that starts on <paramref name="rowStart"/>.</summary>
    public static int WeekNumber(DateOnly rowStart, BoardSettings s)
    {
        switch (s.WeekRule)
        {
            case WeekNumberRule.FirstDayOfYear:
            {
                // The row containing January 1st is week 1, even when it began in December.
                var rowEnd = rowStart.AddDays(6);
                if (rowEnd.Year != rowStart.Year) return 1;
                var cal = CultureInfo.InvariantCulture.Calendar;
                return cal.GetWeekOfYear(rowEnd.ToDateTime(TimeOnly.MinValue), CalendarWeekRule.FirstDay, s.FirstDayOfWeek);
            }
            default:
                // Use the row's middle day so a row never straddles two ISO weeks ambiguously.
                return ISOWeek.GetWeekOfYear(rowStart.AddDays(3).ToDateTime(TimeOnly.MinValue));
        }
    }
}
