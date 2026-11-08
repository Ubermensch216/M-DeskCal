using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace DeskCal.App;

/// <summary>A black-on-white month page for paper: the same days, items and events as the board, laid out to fill a page.</summary>
internal static class PrintableMonth
{
    private const double LineHeight = 13.5, FontSize = 10.5;

    /// <summary>Everything a page needs, gathered once so the page itself is a pure function of this data.</summary>
    public sealed record PageData(
        DateOnly Month, DayOfWeek FirstDay,
        IReadOnlyDictionary<DateOnly, List<Item>> Items, IReadOnlyList<EventOccurrence> Events, KoreanDays? Days);

    public static PageData Gather(DateOnly month, DayOfWeek firstDay, ItemRepository items, EventRepository events, Func<DateOnly, bool> isDayOff, KoreanDays? days)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var byDay = items.GetRange(first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Where(i => DateOnly.TryParseExact(i.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .GroupBy(i => DateOnly.ParseExact(i.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.ToList());
        var occurrences = EventOccurrences.Expand(events.GetAllActive(), events.GetExceptions(), first, last, isDayOff);
        return new PageData(first, firstDay, byDay, occurrences, days);
    }

    /// <summary>The page, sized to exactly <paramref name="width"/> x <paramref name="height"/> (device-independent pixels).</summary>
    public static FrameworkElement Build(PageData data, double width, double height)
    {
        var first = data.Month;
        int lead = ((int)first.DayOfWeek - (int)data.FirstDay + 7) % 7;
        int days = DateTime.DaysInMonth(first.Year, first.Month);
        int weeks = (lead + days + 6) / 7;
        var gridStart = first.AddDays(-lead);

        var root = new Grid { Width = width, Height = height, Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        for (int w = 0; w < weeks; w++) root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < 7; c++) root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var font = new FontFamily("Malgun Gothic, Segoe UI");
        var title = new TextBlock { Text = first.ToString("Y", CultureInfo.CurrentCulture), FontFamily = font, FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4) };
        Grid.SetColumnSpan(title, 7); root.Children.Add(title);

        for (int c = 0; c < 7; c++)
        {
            var dow = (DayOfWeek)(((int)data.FirstDay + c) % 7);
            var head = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xEA, 0xF2)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5),
                Child = new TextBlock
                {
                    Text = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(dow), FontFamily = font, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = dow == DayOfWeek.Sunday ? Brushes.Firebrick : dow == DayOfWeek.Saturday ? Brushes.RoyalBlue : Brushes.Black,
                },
            };
            Grid.SetRow(head, 1); Grid.SetColumn(head, c); root.Children.Add(head);
        }

        double cellHeight = (height - 46 - 22) / weeks;
        int maxLines = Math.Max(0, (int)((cellHeight - 24) / LineHeight));

        for (int w = 0; w < weeks; w++)
            for (int c = 0; c < 7; c++)
            {
                var day = gridStart.AddDays(w * 7 + c);
                var cell = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Padding = new Thickness(3, 2, 3, 2), ClipToBounds = true };
                if (day.Month != first.Month) { cell.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF4)); }
                else cell.Child = DayContent(day, data, font, maxLines);
                Grid.SetRow(cell, w + 2); Grid.SetColumn(cell, c);
                root.Children.Add(cell);
            }

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return root;
    }

    private static UIElement DayContent(DateOnly day, PageData data, FontFamily font, int maxLines)
    {
        var info = data.Days?.Get(day);
        bool off = day.DayOfWeek is DayOfWeek.Sunday || info?.IsDayOff == true;
        bool sat = day.DayOfWeek == DayOfWeek.Saturday;

        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = day.Day.ToString(CultureInfo.InvariantCulture), FontFamily = font, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = off ? Brushes.Firebrick : sat ? Brushes.RoyalBlue : Brushes.Black });
        if (info?.HolidayName is { } hn) head.Children.Add(new TextBlock { Text = " " + hn, FontFamily = font, FontSize = 9, Foreground = Brushes.Firebrick, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 1) });
        else if (info?.FestivalName is { } fn) head.Children.Add(new TextBlock { Text = " " + fn, FontFamily = font, FontSize = 9, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 1) });
        stack.Children.Add(head);

        var lines = new List<(string Text, bool Done, string? Color)>();
        foreach (var o in data.Events.Where(o => o.CoversDay(day)))
            lines.Add(((o.StartTime is { } t && o.Date == day ? t.ToString("HH:mm", CultureInfo.InvariantCulture) + " " : o.Date < day ? "↳ " : "") + o.Event.Title, false, o.Event.Color));
        if (data.Items.TryGetValue(day, out var items))
            foreach (var i in items) lines.Add(((i.Done ? "✓ " : "• ") + i.Content, i.Done, null));

        int shown = lines.Count <= maxLines ? lines.Count : Math.Max(0, maxLines - 1);
        foreach (var (text, done, _) in lines.Take(shown))
            stack.Children.Add(new TextBlock
            {
                Text = text, FontFamily = font, FontSize = FontSize, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = done ? Brushes.Gray : Brushes.Black,
                TextDecorations = done ? TextDecorations.Strikethrough : null,
            });
        if (lines.Count > shown)
            stack.Children.Add(new TextBlock { Text = $"… 외 {lines.Count - shown}건", FontFamily = font, FontSize = 9.5, Foreground = Brushes.Gray });
        return stack;
    }
}

internal static class PrintUi
{
    /// <summary>Opens the Windows print dialog and prints the month as one landscape page.</summary>
    public static void Print(PrintableMonth.PageData data)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        try
        {
            dlg.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;
            // keep a margin inside the printable area
            double w = Math.Max(300, dlg.PrintableAreaWidth - 40), h = Math.Max(300, dlg.PrintableAreaHeight - 40);
            var page = PrintableMonth.Build(data, w, h);
            var host = new Grid { Width = dlg.PrintableAreaWidth, Height = dlg.PrintableAreaHeight };
            page.HorizontalAlignment = HorizontalAlignment.Center; page.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(page);
            host.Measure(new Size(host.Width, host.Height)); host.Arrange(new Rect(0, 0, host.Width, host.Height)); host.UpdateLayout();
            dlg.PrintVisual(host, $"M-DeskCal {data.Month:yyyy-MM}");
        }
        catch (Exception ex) when (ex is System.Printing.PrintQueueException or System.Printing.PrintSystemException or InvalidOperationException)
        {
            MessageBox.Show("인쇄하지 못했습니다.\n\n" + ex.Message, "인쇄", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
