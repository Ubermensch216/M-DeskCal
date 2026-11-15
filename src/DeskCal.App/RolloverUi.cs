using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskCal.Ai;
using DeskCal.Core;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using DatePicker = System.Windows.Controls.DatePicker;
using MessageBox = System.Windows.MessageBox;

namespace DeskCal.App;

/// <summary>What the "미완료 정리" window needs from the app.</summary>
internal sealed record RolloverHost(
    RolloverService Service, Func<DateOnly> Today, Func<DateOnly, bool> IsDayOff, Action Changed, Action<DateOnly> GoTo);

/// <summary>
/// "미완료 정리": the to-dos left undone on past days, listed with how late they are and how often they were already pushed, to be
/// moved to today (or the next working day) in one click, or spread over the next working days ("여유 있는 날로 나누기": fewest open
/// to-dos and least booked time first). The user sees and can change every day before anything moves, and can undo it afterwards.
/// </summary>
internal static class RolloverWindow
{
    private static Window? _open;

    public static void ShowOrActivate(RolloverHost host)
    {
        if (_open is not null) { _open.Activate(); return; }
        var win = Build(host, out _);
        _open = win;
        win.Closed += (_, _) => _open = null;
        win.Show();
        win.Activate();
    }

    private sealed class Row
    {
        public required OverdueItem Item;
        public required CheckBox Check;
        public required DatePicker To;
        public bool Moved;
    }

    internal static Window Build(RolloverHost host, out Action refresh)
    {
        var win = new Window();
        ModernUi.ConfigureWindow(win, "미완료 정리", 720, 680);

        var summary = new TextBlock { Margin = new Thickness(0, 2, 0, 8), FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush, TextWrapping = TextWrapping.Wrap };
        var list = new StackPanel();
        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ModernUi.MutedBrush };
        var rows = new List<Row>();
        var target = new DatePicker { Width = 136, VerticalAlignment = VerticalAlignment.Center };
        Button moveBtn = null!, undoBtn = null!;
        moveBtn = ModernUi.PrimaryBtn("선택한 건 옮기기", (_, _) => MoveSelected());
        moveBtn.IsEnabled = false;
        undoBtn = ModernUi.SecondaryBtn("마지막 이동 되돌리기", (_, _) => Undo());

        DateOnly Start() => RolloverPlanner.FirstDay(host.Today(), host.IsDayOff);

        void UpdateButtons()
        {
            int n = rows.Count(r => r.Check.IsChecked == true && r.Check.IsEnabled);
            moveBtn.Content = n == 0 ? "선택한 건 옮기기" : $"선택한 {n}건 옮기기";
            moveBtn.IsEnabled = n > 0;
            var last = host.Service.Last();
            undoBtn.IsEnabled = last is not null;
            undoBtn.Content = last is null ? "마지막 이동 되돌리기" : $"마지막 이동 되돌리기 ({last.Moves.Count}건)";
        }

        void Refresh()
        {
            rows.Clear(); list.Children.Clear();
            var overdue = host.Service.Find(host.Today());
            var to = Start();
            if (target.SelectedDate is null) target.SelectedDate = to.ToDateTime(TimeOnly.MinValue);
            foreach (var o in overdue)
            {
                var check = new CheckBox { IsChecked = true, Margin = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                var picker = new DatePicker { SelectedDate = target.SelectedDate ?? to.ToDateTime(TimeOnly.MinValue), Width = 126, VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { Text = o.Content, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush };
                CountdownCalc.TryParseDate(o.Day, out var day);
                var detail = new TextBlock { Text = $"{day:M월 d일} ({AiText.Dow[(int)day.DayOfWeek]}) 의 할 일 · {o.DaysLate}일 지남", Foreground = ModernUi.MutedBrush, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
                var warn = new Border
                {
                    Background = ModernUi.WarnSoftBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 6, 0, 0),
                    Visibility = o.TimesMoved >= RolloverPlanner.ManyMoves ? Visibility.Visible : Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = $"이미 {o.TimesMoved}번 옮겼습니다 — 또 미루기보다 쪼개거나 지우는 편이 나을 수 있습니다.",
                        Foreground = ModernUi.WarnBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    },
                };
                var body = new StackPanel { Children = { title, detail, warn }, Margin = new Thickness(0, 0, 12, 0) };
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(body, 1); Grid.SetColumn(picker, 2);
                line.Children.Add(check); line.Children.Add(body); line.Children.Add(picker);
                list.Children.Add(ModernUi.Card(line, new Thickness(12, 10, 12, 10), new Thickness(0, 0, 0, 6)));
                check.Click += (_, _) => UpdateButtons();
                rows.Add(new Row { Item = o, Check = check, To = picker });
            }
            summary.Text = overdue.Count == 0
                ? $"지난 {RolloverPlanner.LookbackDays}일 안에 끝내지 못한 할 일이 없습니다."
                : $"끝내지 못한 할 일 {overdue.Count}개 (지난 {RolloverPlanner.LookbackDays}일, 오래된 순). 옮길 것만 체크하고 날짜는 바꿀 수 있습니다.";
            UpdateButtons();
        }
        refresh = Refresh;

        void SetAll(DateOnly day) { foreach (var r in rows.Where(r => !r.Moved)) r.To.SelectedDate = day.ToDateTime(TimeOnly.MinValue); }

        void Spread()
        {
            var pending = rows.Where(r => !r.Moved).Select(r => r.Item).ToList();
            var plan = host.Service.Spread(pending, host.Today());
            foreach (var m in plan) rows.First(r => r.Item.Id == m.Item.Id).To.SelectedDate = m.To.ToDateTime(TimeOnly.MinValue);
            var days = plan.Select(m => m.To).Distinct().OrderBy(d => d).ToList();
            status.Text = plan.Count == 0 ? "" : $"{plan.Count}개를 {days.Count}일에 나누었습니다 ({string.Join(", ", days.Select(d => $"{d:M/d}"))}). 그날의 남은 할 일 수와 일정이 잡힌 시간을 보고 정한 제안입니다 — 날짜는 바꿀 수 있습니다.";
        }

        void MoveSelected()
        {
            var picked = rows.Where(r => r.Check.IsChecked == true && r.Check.IsEnabled && r.To.SelectedDate is not null).ToList();
            if (picked.Count == 0) return;
            var result = host.Service.Apply(picked.Select(r => new RolloverMove(r.Item, DateOnly.FromDateTime(r.To.SelectedDate!.Value))).ToList());
            host.Changed();
            var movedIds = result.Moved.Select(m => m.ItemId).ToHashSet();
            foreach (var r in picked.Where(r => movedIds.Contains(r.Item.Id)))
            {
                r.Moved = true; r.Check.IsChecked = false; r.Check.IsEnabled = false; r.To.IsEnabled = false;
            }
            status.Text = $"{result.Moved.Count}건을 옮겼습니다." + (result.Skipped > 0 ? $" ({result.Skipped}건은 그 사이 바뀌어 옮기지 않았습니다.)" : "") + " '마지막 이동 되돌리기'로 한꺼번에 취소할 수 있습니다.";
            if (result.Moved.Count > 0) host.GoTo(result.Moved.Select(m => DateOnly.ParseExact(m.To, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Min());
            UpdateButtons();
        }

        void Undo()
        {
            var last = host.Service.Last();
            if (last is null) return;
            var ask = MessageBox.Show(win, $"마지막으로 옮긴 {last.Moves.Count}건을 원래 날짜로 되돌립니다.\n(그 뒤에 직접 옮기거나 고친 항목은 그대로 둡니다.)", "미완료 정리", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;
            var r = host.Service.UndoLast();
            host.Changed();
            status.Text = $"{r.Reverted}건을 원래 날짜로 되돌렸습니다." + (r.Changed > 0 ? $" ({r.Changed}건은 그 뒤에 바뀌어 그대로 두었습니다.)" : "");
            Refresh();
        }

        var todayBtn = ModernUi.ChipBtn("모두 오늘로", (_, _) => SetAll(Start()));
        var nextBtn = ModernUi.ChipBtn("모두 다음 업무일로", (_, _) => SetAll(RolloverPlanner.DayAfter(host.Today(), host.IsDayOff)));
        var spreadBtn = ModernUi.ChipBtn("여유 있는 날로 나누기", (_, _) => Spread());
        var applyAll = ModernUi.ChipBtn("이 날짜로 모두", (_, _) => { if (target.SelectedDate is { } d) SetAll(DateOnly.FromDateTime(d)); });
        applyAll.Margin = new Thickness(6, 0, 0, 0);
        var quickWrap = new WrapPanel();
        foreach (var b in new UIElement[] { todayBtn, nextBtn, spreadBtn, new TextBlock { Text = "날짜", VerticalAlignment = VerticalAlignment.Center, Foreground = ModernUi.MutedBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 6, 0) }, target, applyAll })
            quickWrap.Children.Add(b);
        var quickCard = ModernUi.Card(quickWrap, new Thickness(12, 10, 12, 10));

        var close = ModernUi.SecondaryBtn("닫기", (_, _) => win.Close());
        close.Margin = new Thickness(0);
        var leftBtns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        leftBtns.Children.Add(moveBtn); leftBtns.Children.Add(undoBtn);
        var rightBtns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        rightBtns.Children.Add(close);
        var footer = ModernUi.Footer(leftBtns, rightBtns);

        var header = ModernUi.Header(ModernUi.Icons.Rollover, "미완료 정리", "지난 날의 미완료 할 일을 오늘이나 다음 업무일로 옮깁니다 (반복·D-day 항목 제외)");
        var body = new StackPanel();
        foreach (var e in new UIElement[] { quickCard, summary, list, status })
            body.Children.Add(e);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 14, 18, 10), Content = body };

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        win.Content = dock;
        win.Loaded += (_, _) => Refresh();
        return win;
    }
}
