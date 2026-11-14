using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DeskCal.Core;
using DeskCal.Data;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>Shared pieces of the small dialogs behind the day editor's option buttons (repeat, reminder, countdown).</summary>
internal static class ItemDialog
{
    private static readonly string[] Dow = ["일", "월", "화", "수", "목", "금", "토"];
    public static string DayName(DayOfWeek d) => Dow[(int)d];

    /// <summary>Dev aid (snapshot self-test): when set, dialogs hand their window to this instead of showing it modally.</summary>
    internal static Action<Window>? Preview { get; set; }

    public static void Open(Window win)
    {
        if (Preview is { } preview) preview(win); else win.ShowDialog();
    }

    public static Window Shell(Window? owner, string title, double width)
    {
        var w = Dlg.Create(title, width, 100);
        w.SizeToContent = SizeToContent.Height;
        w.ResizeMode = ResizeMode.NoResize;
        w.Owner = owner;
        w.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        return w;
    }

    /// <summary>The item's text, shown read-only at the top of each dialog.</summary>
    public static UIElement Header(string text, DateOnly day)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        p.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 });
        p.Children.Add(new TextBlock { Text = day.ToString("D", CultureInfo.CurrentCulture), Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        return p;
    }

    public static TextBlock Caption(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 3), FontWeight = FontWeights.SemiBold };

    /// <summary>Bottom row: an optional "remove" button on the left, cancel and apply on the right.</summary>
    public static UIElement Buttons(string? removeText, Action remove, Action apply)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        if (removeText is not null)
        {
            var rm = new Button { Content = removeText, Padding = new Thickness(10, 5, 10, 5) };
            rm.Click += (_, _) => remove();
            DockPanel.SetDock(rm, Dock.Left);
            dock.Children.Add(rm);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(new Button { Content = "취소", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true });
        var ok = new Button { Content = "적용", Padding = new Thickness(14, 5, 14, 5), FontWeight = FontWeights.SemiBold, IsDefault = true };
        ok.Click += (_, _) => apply();
        right.Children.Add(ok);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        return dock;
    }

    public static TextBlock Error() => new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 8, 0, 0) };
}

// ---------------------------------------------------------------------------------------------
/// <summary>"작업 반복": how often a day-cell item comes back, and when that stops.</summary>
internal static class RepeatDialog
{
    private sealed record Choice(string Label, RepeatKind Kind, MonthlyMode Mode = MonthlyMode.ByDate);

    /// <returns>Null when cancelled; <see cref="RecurrenceRule.None"/> when the user stopped the repetition.</returns>
    public static RecurrenceRule? Show(Window? owner, string itemText, DateOnly day, RecurrenceRule current)
    {
        int mask = current.Kind == RepeatKind.Weekly ? current.WeekdaysMask : 0;
        string weekly = mask == 0 || mask == 1 << (int)day.DayOfWeek
            ? $"매주 ({ItemDialog.DayName(day.DayOfWeek)}요일)"
            : "매주 (" + string.Join("·", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => ItemDialog.DayName((DayOfWeek)i))) + ")";
        int nth = (day.Day - 1) / 7 + 1;
        string nthText = nth >= 5 ? "마지막" : $"{nth}번째";

        var choices = new List<Choice>
        {
            new("반복 안 함", RepeatKind.None), new("매일", RepeatKind.Daily), new("평일 (월~금)", RepeatKind.Weekday),
            new("법정 근무일 (공휴일 제외)", RepeatKind.LegalWorkDay), new(weekly, RepeatKind.Weekly),
            new($"매월 ({day.Day}일)", RepeatKind.Monthly), new($"매월 ({nthText} {ItemDialog.DayName(day.DayOfWeek)}요일)", RepeatKind.Monthly, MonthlyMode.ByWeekday),
            new("매월 말일", RepeatKind.MonthLastDay), new("분기 말일 (3·6·9·12월)", RepeatKind.SeasonLastDay),
            new($"매년 ({day.Month}월 {day.Day}일)", RepeatKind.Yearly), new("음력 매년", RepeatKind.LunarYearly),
            new("에빙하우스 망각곡선 (+1·2·4·7·15·30일)", RepeatKind.Ebbinghaus),
        };

        RecurrenceRule? result = null;
        var win = ItemDialog.Shell(owner, "작업 반복", 400);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(ItemDialog.Header(itemText, day));

        panel.Children.Add(ItemDialog.Caption("반복"));
        var kind = new ComboBox();
        foreach (var c in choices) kind.Items.Add(c.Label);
        panel.Children.Add(kind);

        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var interval = new TextBox { Width = 46, Padding = new Thickness(4), TextAlignment = TextAlignment.Center, Text = "1", Margin = new Thickness(8, 0, 6, 0) };
        var unit = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        intervalRow.Children.Add(new TextBlock { Text = "간격", VerticalAlignment = VerticalAlignment.Center });
        intervalRow.Children.Add(interval); intervalRow.Children.Add(unit);
        panel.Children.Add(intervalRow);

        var endRow = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        endRow.Children.Add(ItemDialog.Caption("종료"));
        var endLine = new StackPanel { Orientation = Orientation.Horizontal };
        var endKind = new ComboBox { Width = 120 };
        foreach (var s in new[] { "무한", "횟수", "종료일" }) endKind.Items.Add(s);
        var count = new TextBox { Width = 46, Padding = new Thickness(4), TextAlignment = TextAlignment.Center, Text = "10", Margin = new Thickness(8, 0, 4, 0) };
        var countUnit = new TextBlock { Text = "회", VerticalAlignment = VerticalAlignment.Center };
        var until = new DatePicker { Width = 140, Margin = new Thickness(8, 0, 0, 0) };
        endLine.Children.Add(endKind); endLine.Children.Add(count); endLine.Children.Add(countUnit); endLine.Children.Add(until);
        endRow.Children.Add(endLine);
        panel.Children.Add(endRow);

        var error = ItemDialog.Error();
        panel.Children.Add(error);

        void Refresh()
        {
            var k = kind.SelectedIndex >= 0 ? choices[kind.SelectedIndex].Kind : RepeatKind.None;
            intervalRow.Visibility = k.UsesInterval() ? Visibility.Visible : Visibility.Collapsed;
            unit.Text = k switch
            {
                RepeatKind.Daily => "일마다", RepeatKind.Weekly => "주마다", RepeatKind.Monthly => "개월마다", RepeatKind.Yearly => "년마다",
                RepeatKind.MonthLastDay => "개월마다 (월말)", RepeatKind.LunarYearly => "년마다 (음력)", _ => "",
            };
            endRow.Visibility = k is RepeatKind.None or RepeatKind.Ebbinghaus ? Visibility.Collapsed : Visibility.Visible;
            count.Visibility = countUnit.Visibility = endKind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            until.Visibility = endKind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        kind.SelectionChanged += (_, _) => Refresh();
        endKind.SelectionChanged += (_, _) => Refresh();

        int selected = Math.Max(0, choices.FindIndex(c => c.Kind == current.Kind && (c.Kind != RepeatKind.Monthly || c.Mode == current.Monthly)));
        kind.SelectedIndex = selected;
        interval.Text = Math.Max(1, current.Interval).ToString(CultureInfo.InvariantCulture);
        endKind.SelectedIndex = current.Count is not null ? 1 : current.Until is not null ? 2 : 0;
        count.Text = (current.Count ?? 10).ToString(CultureInfo.InvariantCulture);
        until.SelectedDate = current.Until?.ToDateTime(TimeOnly.MinValue);
        Refresh();

        void Apply()
        {
            var choice = choices[Math.Max(0, kind.SelectedIndex)];
            if (choice.Kind != RepeatKind.None)
            {
                int n = 1;
                if (choice.Kind.UsesInterval() && (!int.TryParse(interval.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > 999))
                { error.Text = "반복 간격은 1~999 사이의 숫자여야 합니다."; return; }

                int? cnt = null; DateOnly? end = null;
                if (choice.Kind != RepeatKind.Ebbinghaus)
                {
                    if (endKind.SelectedIndex == 1)
                    {
                        if (!int.TryParse(count.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int c) || c < 1 || c > 2000)
                        { error.Text = "반복 횟수는 1~2000 사이의 숫자여야 합니다."; return; }
                        cnt = c;
                    }
                    else if (endKind.SelectedIndex == 2)
                    {
                        if (until.SelectedDate is not { } u) { error.Text = "반복 종료일을 선택하세요."; return; }
                        end = DateOnly.FromDateTime(u);
                        if (end < day) { error.Text = "종료일은 항목의 날짜 이후여야 합니다."; return; }
                    }
                }
                result = new RecurrenceRule
                {
                    Kind = choice.Kind, Interval = choice.Kind.UsesInterval() ? n : 1,
                    WeekdaysMask = choice.Kind == RepeatKind.Weekly ? mask : 0, Monthly = choice.Mode,
                    Nth = choice.Kind == RepeatKind.Monthly && choice.Mode == MonthlyMode.ByWeekday && current.Monthly == MonthlyMode.ByWeekday ? current.Nth : 0,
                    Count = cnt, Until = end,
                };
            }
            else result = RecurrenceRule.None;
            win.DialogResult = true;
        }

        panel.Children.Add(ItemDialog.Buttons(current.IsRepeating ? "반복 해제" : null, () => { result = RecurrenceRule.None; win.DialogResult = true; }, Apply));
        win.Content = panel;
        ItemDialog.Open(win);
        return result;
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>"알림": the time of day, and which days before the item's day the reminder rings.</summary>
internal static class ReminderDialog
{
    private static readonly int[] Presets = [0, 1440, 2880, 4320, 10080];

    /// <returns>Null when cancelled; an empty list when the reminders were removed.</returns>
    public static IReadOnlyList<ItemReminderSpec>? Show(Window? owner, string itemText, DateOnly day, IReadOnlyList<Reminder> current)
    {
        IReadOnlyList<ItemReminderSpec>? result = null;
        var win = ItemDialog.Shell(owner, "알림", 400);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(ItemDialog.Header(itemText, day));

        string time = current.Select(r => r.CustomTime).FirstOrDefault(t => t is not null) ?? "09:00";
        ReminderPlanner.TryParseClock(time, out var clock);

        panel.Children.Add(ItemDialog.Caption("알림 시각"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var hour = new ComboBox { Width = 64 };
        var minute = new ComboBox { Width = 64 };
        for (int h = 0; h < 24; h++) hour.Items.Add(h.ToString("00", CultureInfo.InvariantCulture));
        for (int m = 0; m < 60; m++) minute.Items.Add(m.ToString("00", CultureInfo.InvariantCulture));
        hour.SelectedIndex = clock.Hour; minute.SelectedIndex = clock.Minute;
        row.Children.Add(hour);
        row.Children.Add(new TextBlock { Text = ":", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(minute);
        panel.Children.Add(row);

        panel.Children.Add(ItemDialog.Caption("알림 시점 (여러 개 선택 가능)"));
        var boxes = new List<(CheckBox Box, int Offset)>();
        var wrap = new WrapPanel();
        var offsets = Presets.Concat(current.Select(r => r.OffsetMinutes).Where(o => !Presets.Contains(o))).Distinct().ToList();
        foreach (int offset in offsets)
        {
            var box = new CheckBox
            {
                Content = offset == 0 ? "당일" : ReminderText.DescribeOffset(offset), Margin = new Thickness(0, 0, 16, 6),
                IsChecked = current.Any(r => r.OffsetMinutes == offset),
            };
            boxes.Add((box, offset));
            wrap.Children.Add(box);
        }
        panel.Children.Add(wrap);

        panel.Children.Add(new TextBlock
        {
            Text = "알림 시각에서 선택한 만큼 앞서 울립니다. (예: 1일 전 + 09:15 → 전날 09:15)",
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        var error = ItemDialog.Error();
        panel.Children.Add(error);

        void Apply()
        {
            string t = $"{hour.SelectedIndex:00}:{minute.SelectedIndex:00}";
            var specs = boxes.Where(b => b.Box.IsChecked == true).Select(b => new ItemReminderSpec(b.Offset, t)).ToList();
            if (specs.Count == 0) { error.Text = "알림 시점을 하나 이상 선택하거나 '알림 제거'를 누르세요."; return; }
            result = specs;
            win.DialogResult = true;
        }

        panel.Children.Add(ItemDialog.Buttons(current.Count > 0 ? "알림 제거" : null, () => { result = []; win.DialogResult = true; }, Apply));
        win.Content = panel;
        ItemDialog.Open(win);
        return result;
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>What the countdown dialog decided: the kind of D-day and where it is shown, or removal.</summary>
internal sealed record CountdownChoice(CountdownType Type, bool ShowCard, bool ShowBottom, bool Remove);

/// <summary>"기념일까지 카운트다운": turn the item's day into a D-day (birthday, anniversary, exam...).</summary>
internal static class ItemCountdownDialog
{
    public static CountdownChoice? Show(Window? owner, string itemText, DateOnly day, Countdown? current)
    {
        CountdownChoice? result = null;
        var win = ItemDialog.Shell(owner, "기념일까지 카운트다운", 420);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(ItemDialog.Header(itemText, day));

        panel.Children.Add(ItemDialog.Caption("종류"));
        var type = new ComboBox();
        foreach (var t in CountdownWindow.Types) type.Items.Add(t.Label);
        panel.Children.Add(type);

        var preview = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) };
        panel.Children.Add(preview);

        var card = new CheckBox { Content = "바탕화면에 카드로 표시", Margin = new Thickness(0, 10, 0, 0), IsChecked = current?.ShowCard ?? true };
        var bottom = new CheckBox { Content = "달력 아래 줄에 표시", Margin = new Thickness(0, 4, 0, 0), IsChecked = current?.ShowBottom ?? false };
        panel.Children.Add(card); panel.Children.Add(bottom);

        type.SelectedIndex = Math.Max(0, Array.FindIndex(CountdownWindow.Types, t => t.Type == (current?.Type ?? CountdownType.Custom)));
        void Refresh()
        {
            var chosen = CountdownWindow.Types[Math.Max(0, type.SelectedIndex)];
            var status = CountdownCalc.Compute(new Countdown("", itemText, CountdownCalc.Format(day), chosen.Type, false, false, false), DateOnly.FromDateTime(DateTime.Today));
            preview.Text = status is null ? "" : "미리보기: " + CountdownCalc.Summary(new Countdown("", itemText, "", chosen.Type, false, false, false), status);
        }
        type.SelectionChanged += (_, _) => Refresh();
        Refresh();

        panel.Children.Add(ItemDialog.Buttons(current is not null ? "카운트다운 해제" : null,
            () => { result = new CountdownChoice(CountdownType.Custom, false, false, true); win.DialogResult = true; },
            () =>
            {
                result = new CountdownChoice(CountdownWindow.Types[Math.Max(0, type.SelectedIndex)].Type, card.IsChecked == true, bottom.IsChecked == true, false);
                win.DialogResult = true;
            }));
        win.Content = panel;
        ItemDialog.Open(win);
        return result;
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>The text-colour / cell-colour palette that drops down from the editor's colour button, with the colours used last.</summary>
internal static class ColorPickerPopup
{
    private const int Columns = 8;
    private const int RecentMax = 7;
    private const string RecentTextKey = "editor.recent_text_colors";
    private const string RecentCellKey = "editor.recent_cell_colors";

    // first cell of the first row is "default" (null); the last cell of the last row is the custom-colour picker
    private static readonly string?[] Palette =
    [
        null, "#FF9AA2", "#FFB86B", "#FFE08A", "#B9F0A2", "#A6D1FF", "#D3B8FF", "#E6C9A8",
        "#212121", "#E53935", "#FB8C00", "#F9C400", "#0B8A00", "#2F54EB", "#6A1B9A", "#26C6A6",
        "#4CAF50", "#8E8E93", "#FFC4D6", "#FFDCC2", "#C8F2EC", "#D6E4FF", "#DCD3F7", "#CUSTOM",
    ];

    public static List<string> Recent(SettingsStore? store, string key) =>
        (store?.GetString(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsHex).Take(RecentMax).ToList();

    private static bool IsHex(string s) => s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    private static void Remember(SettingsStore? store, string key, string hex)
    {
        if (store is null) return;
        var list = Recent(store, key);
        list.RemoveAll(h => h.Equals(hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex.ToUpperInvariant());
        store.SetString(key, string.Join(",", list.Take(RecentMax)));
    }

    /// <summary>Open the palette under <paramref name="anchor"/>. A pick (null = back to the default colour) calls the matching callback once the popup is closed.</summary>
    public static void Show(FrameworkElement anchor, SettingsStore? store, string? currentText, string? currentCell,
                            Action<string?> pickText, Action<string?> pickCell, Action closed)
    {
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        string? chosen = null; bool picked = false, isCell = false;

        popup.Child = BuildContent(store, currentText, currentCell,
            (hex, cell) =>
            {
                if (hex is not null) Remember(store, cell ? RecentCellKey : RecentTextKey, hex);
                chosen = hex; picked = true; isCell = cell;
                popup.IsOpen = false;
            },
            on => popup.StaysOpen = on);
        popup.Closed += (_, _) =>
        {
            if (picked) (isCell ? pickCell : pickText)(chosen);
            closed();
        };
        popup.IsOpen = true;
    }

    /// <summary>The palette itself (also rendered offscreen by the snapshot self-test).</summary>
    internal static FrameworkElement BuildContent(SettingsStore? store, string? currentText, string? currentCell,
                                                  Action<string?, bool> pick, Action<bool> keepOpen)
    {
        var body = new StackPanel { Margin = new Thickness(12, 10, 12, 12), Width = Columns * 30 + 4 };
        Section(body, "텍스트 색상", Recent(store, RecentTextKey), currentText, text: true, cell: false, pick, keepOpen);
        body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE1, 0xE5, 0xEC)), Margin = new Thickness(0, 8, 0, 4) });
        Section(body, "셀 색상 (배경)", Recent(store, RecentCellKey), currentCell, text: false, cell: true, pick, keepOpen);
        return new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD0, 0xDB)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(8), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 },
        };
    }

    private static void Section(StackPanel body, string title, List<string> recent, string? current, bool text, bool cell,
                                Action<string?, bool> pick, Action<bool> keepOpen)
    {
        var dark = new SolidColorBrush(Color.FromRgb(0x2B, 0x33, 0x40));
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = dark, Margin = new Thickness(2, 0, 0, 4) });
        if (recent.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "최근 사용", FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(2, 0, 0, 2) });
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var hex in recent) wrap.Children.Add(Swatch(hex, current, text, pick, cell));
            body.Children.Add(wrap);
        }
        var grid = new UniformGrid { Columns = Columns };
        foreach (var hex in Palette)
            grid.Children.Add(hex == "#CUSTOM" ? Custom(cell, pick, keepOpen) : Swatch(hex, current, text, pick, cell));
        body.Children.Add(grid);
    }

    private static FrameworkElement Swatch(string? hex, string? current, bool text, Action<string?, bool> pick, bool cell)
    {
        bool selected = hex is null ? string.IsNullOrEmpty(current) : string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
        var color = UiColors.Parse(hex, Colors.Transparent);
        var brush = new SolidColorBrush(color);

        var tile = new Border
        {
            Width = 24, Height = 24, Margin = new Thickness(3), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) : new SolidColorBrush(Color.FromRgb(0xD3, 0xD9, 0xE2)),
            Background = text ? new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)) : hex is null ? Brushes.White : brush,
            ToolTip = hex ?? "기본",
        };
        var inner = new System.Windows.Controls.Grid();
        if (text)
            inner.Children.Add(new TextBlock
            {
                Text = "A", FontWeight = FontWeights.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = hex is null ? new SolidColorBrush(Color.FromRgb(0x77, 0x80, 0x8F)) : brush,
            });
        if (hex is null)      // "default": a red slash through the tile
            inner.Children.Add(new System.Windows.Shapes.Line { X1 = 3, Y1 = 19, X2 = 19, Y2 = 3, Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), StrokeThickness = 1.5 });
        tile.Child = inner;
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(hex, cell); };
        return tile;
    }

    private static FrameworkElement Custom(bool cell, Action<string?, bool> pick, Action<bool> keepOpen)
    {
        var rainbow = new LinearGradientBrush(
        [
            new GradientStop(Colors.Red, 0), new GradientStop(Colors.Orange, 0.2), new GradientStop(Colors.Yellow, 0.4),
            new GradientStop(Colors.LimeGreen, 0.6), new GradientStop(Colors.DodgerBlue, 0.8), new GradientStop(Colors.MediumPurple, 1),
        ], 45);
        var tile = new Border
        {
            Width = 24, Height = 24, Margin = new Thickness(3), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
            Background = rainbow, ToolTip = "다른 색 선택…",
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            keepOpen(true);                       // the popup must not close itself while the colour dialog has the focus
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
            bool ok = dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK;
            keepOpen(false);
            if (ok) pick($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}", cell);
        };
        return tile;
    }
}
