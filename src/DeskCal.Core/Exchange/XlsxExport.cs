using System.Globalization;

namespace DeskCal.Core.Exchange;

/// <summary>Short Korean descriptions used in the spreadsheet ("매주 월·수, 6회").</summary>
public static class RepeatText
{
    private static readonly string[] DayShort = ["일", "월", "화", "수", "목", "금", "토"];

    public static string Describe(RecurrenceRule r)
    {
        string main = r.Kind switch
        {
            RepeatKind.Daily => r.Interval > 1 ? $"{r.Interval}일마다" : "매일",
            RepeatKind.Weekly => (r.Interval > 1 ? $"{r.Interval}주마다" : "매주")
                                 + (r.WeekdaysMask != 0 ? " " + string.Join("·", Enumerable.Range(0, 7).Where(i => (r.WeekdaysMask & (1 << i)) != 0).Select(i => DayShort[i])) : ""),
            RepeatKind.Monthly => (r.Interval > 1 ? $"{r.Interval}개월마다" : "매월")
                                  + (r.Monthly == MonthlyMode.ByWeekday ? (r.Nth == -1 ? " 마지막 주" : r.Nth > 0 ? $" {r.Nth}번째 주" : " 같은 주") : ""),
            RepeatKind.Yearly => r.Interval > 1 ? $"{r.Interval}년마다" : "매년",
            RepeatKind.Weekday => "평일",
            RepeatKind.MonthLastDay => r.Interval > 1 ? $"{r.Interval}개월마다 말일" : "매월 말일",
            RepeatKind.SeasonLastDay => "분기 말일",
            RepeatKind.LegalWorkDay => "법정 근무일",
            RepeatKind.LunarYearly => r.Interval > 1 ? $"음력 {r.Interval}년마다" : "음력 매년",
            RepeatKind.Ebbinghaus => "에빙하우스 복습",
            _ => "",
        };
        if (main.Length == 0) return "";
        if (r.Count is { } c) main += $", {c}회";
        if (r.Until is { } u) main += $", {u:yyyy-MM-dd}까지";
        return main;
    }

    public static string Offset(ExReminder r)
    {
        string text = r.OffsetMinutes == 0 ? "정시" : r.OffsetMinutes % 1440 == 0 ? $"{r.OffsetMinutes / 1440}일 전"
            : r.OffsetMinutes % 60 == 0 ? $"{r.OffsetMinutes / 60}시간 전" : $"{r.OffsetMinutes}분 전";
        return r.CustomTime is { } t ? $"{text} {t}" : text;
    }
}

/// <summary>Builds the worksheets of the Excel export: one flat list, and one calendar grid per month.</summary>
public static class XlsxExport
{
    public const int MaxMonths = 24;
    private static readonly string[] Weekdays = ["일", "월", "화", "수", "목", "금", "토"];

    public static List<XlsxSheet> Build(
        ExchangeBundle bundle, DateOnly? from, DateOnly? to, DayOfWeek firstDay,
        Func<DateOnly, bool>? isDayOff = null, Func<DateOnly, string?>? holidayName = null)
    {
        var sheets = new List<XlsxSheet> { ListSheet(bundle) };
        sheets.AddRange(CalendarSheets(bundle, from, to, firstDay, isDayOff, holidayName));
        return sheets;
    }

    // ---- list sheet ---------------------------------------------------------------------

    private static XlsxSheet ListSheet(ExchangeBundle b)
    {
        var sheet = new XlsxSheet("목록") { FreezeFirstRow = true, AutoFilter = true };
        sheet.ColumnWidths.AddRange([8, 12, 8, 12, 8, 36, 40, 6, 22, 22]);
        string[] head = ["유형", "날짜", "시작", "종료일", "종료", "제목", "메모", "완료", "반복", "알림"];
        sheet.AddRow(head.Select(h => (XlsxCell?)new XlsxCell(h, XlsxStyle.Header)).ToArray());

        var rows = new List<(DateOnly Date, int Order, XlsxCell?[] Cells)>();
        foreach (var i in b.Items)
        {
            if (!CountdownCalc.TryParseDate(i.Day, out var d)) continue;
            rows.Add((d, 1, [C("할 일"), Dt(d), C(""), C(""), C(""), C(i.Content, XlsxStyle.Wrap), C(""), C(i.Done ? "✓" : ""), C(""), C(Reminders(i.Reminders), XlsxStyle.Wrap)]));
        }
        foreach (var e in b.Events)
        {
            if (!EventTime.TryParse(e.Start, out var d0, out var t0)) continue;
            string endDate = "", endTime = "";
            if (EventTime.TryParse(e.End, out var d1, out var t1)) { endDate = CountdownCalc.Format(d1); endTime = t1?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? ""; }
            var rule = RecurrenceRule.TryParse(e.RRule, e.ExtRule, out var rr) ? rr : RecurrenceRule.None;
            rows.Add((d0, 0, [C("일정"), Dt(d0), C(t0?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "종일"), C(endDate), C(endTime), C(e.Title, XlsxStyle.Wrap),
                C(e.Body, XlsxStyle.Wrap), C(""), C(RepeatText.Describe(rule), XlsxStyle.Wrap), C(Reminders(e.Reminders), XlsxStyle.Wrap)]));
        }
        foreach (var (_, _, cells) in rows.OrderBy(r => r.Date).ThenBy(r => r.Order)) sheet.AddRow(cells);
        return sheet;
    }

    private static string Reminders(IReadOnlyList<ExReminder> list) => string.Join("\n", list.Select(RepeatText.Offset));

    private static XlsxCell C(string text, XlsxStyle style = XlsxStyle.Normal) => new(text, style);
    private static XlsxCell Dt(DateOnly d) => new(d, XlsxStyle.Date);

    // ---- calendar sheets ------------------------------------------------------------------

    private static IEnumerable<XlsxSheet> CalendarSheets(
        ExchangeBundle b, DateOnly? from, DateOnly? to, DayOfWeek firstDay, Func<DateOnly, bool>? isDayOff, Func<DateOnly, string?>? holidayName)
    {
        var events = b.Events.Select(e => new EventEntry(e.Id, e.Title, e.Body, e.Start, e.End, e.AllDay, null, e.RRule, e.ExtRule, 0, e.Color, 0, 0)).ToList();
        var skips = b.Events.Where(e => e.Exceptions.Count > 0).ToDictionary(e => e.Id,
            e => (IReadOnlySet<DateOnly>)e.Exceptions.Select(x => CountdownCalc.TryParseDate(x, out var d) ? d : (DateOnly?)null).Where(d => d is not null).Select(d => d!.Value).ToHashSet());

        var months = MonthRange(b, from, to);
        var items = b.Items.Select(i => (Ok: CountdownCalc.TryParseDate(i.Day, out var d), Day: d, Item: i)).Where(x => x.Ok).ToLookup(x => x.Day, x => x.Item);

        foreach (var month in months)
        {
            var first = new DateOnly(month.Year, month.Month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            int lead = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
            var gridStart = first.AddDays(-lead);
            int weeks = (lead + last.Day + 6) / 7;
            var occurrences = EventOccurrences.Expand(events, skips, first, last, isDayOff);

            var sheet = new XlsxSheet($"{month.Year}-{month.Month:00}") { LandscapeFitToPage = true };
            sheet.ColumnWidths.AddRange(Enumerable.Repeat(22.0, 7));
            int title = sheet.AddRow(Enumerable.Range(0, 7).Select(i => (XlsxCell?)new XlsxCell(i == 0 ? $"{month.Year}년 {month.Month}월" : null, XlsxStyle.Title)).ToArray());
            sheet.RowHeights[title] = 30;
            sheet.Merges.Add((title, 0, title, 6));
            sheet.AddRow(Enumerable.Range(0, 7).Select(i => (XlsxCell?)new XlsxCell(Weekdays[((int)firstDay + i) % 7], XlsxStyle.Header)).ToArray());

            for (int w = 0; w < weeks; w++)
            {
                var cells = new XlsxCell?[7];
                int maxLines = 1;
                for (int d = 0; d < 7; d++)
                {
                    var day = gridStart.AddDays(w * 7 + d);
                    if (day.Month != month.Month) { cells[d] = new XlsxCell(null, XlsxStyle.CellMuted); continue; }

                    string? holiday = holidayName?.Invoke(day);
                    var lines = new List<string> { holiday is null ? day.Day.ToString(CultureInfo.InvariantCulture) : $"{day.Day}  {holiday}" };
                    foreach (var o in occurrences.Where(o => o.CoversDay(day)))
                        lines.Add((o.StartTime is { } t && o.Date == day ? t.ToString("HH:mm", CultureInfo.InvariantCulture) + " " : "") + o.Event.Title);
                    foreach (var it in items[day]) lines.Add((it.Done ? "✓ " : "• ") + it.Content);

                    bool off = day.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Saturday || holiday is not null;
                    cells[d] = new XlsxCell(string.Join("\n", lines), off ? XlsxStyle.CellOff : XlsxStyle.Cell);
                    maxLines = Math.Max(maxLines, lines.Sum(l => 1 + l.Length / 14));    // rough wrap estimate for a 22-wide column
                }
                int row = sheet.AddRow(cells);
                sheet.RowHeights[row] = Math.Clamp(maxLines * 14 + 8, 60, 400);
            }
            yield return sheet;
        }
    }

    /// <summary>The months to give a sheet each: the requested period, or the span of the data (at most <see cref="MaxMonths"/>).</summary>
    internal static List<DateOnly> MonthRange(ExchangeBundle b, DateOnly? from, DateOnly? to)
    {
        DateOnly lo, hi;
        if (from is not null && to is not null) (lo, hi) = (from.Value, to.Value);
        else
        {
            var dates = new List<DateOnly>();
            foreach (var i in b.Items) if (CountdownCalc.TryParseDate(i.Day, out var d)) dates.Add(d);
            foreach (var e in b.Events)
            {
                if (EventTime.TryParse(e.Start, out var d0, out _)) dates.Add(d0);
                if (EventTime.TryParse(e.End, out var d1, out _)) dates.Add(d1);
            }
            if (dates.Count == 0) { var t = DateOnly.FromDateTime(DateTime.Today); (lo, hi) = (t, t); }
            else (lo, hi) = (dates.Min(), dates.Max());
            if (from is { } f) lo = f;
            if (to is { } tt) hi = tt;
        }
        var months = new List<DateOnly>();
        for (var m = new DateOnly(lo.Year, lo.Month, 1); m <= hi && months.Count < MaxMonths; m = m.AddMonths(1)) months.Add(m);
        return months;
    }
}
