using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskCal.Ai;
using DeskCal.Ai.Intent;
using DeskCal.Core;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DatePicker = System.Windows.Controls.DatePicker;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>What the review-plan window needs from the app.</summary>
internal sealed record ReviewPlanHost(
    McpChanges Changes, AiBatchService Batch, Func<DateOnly> Today, Func<DateOnly, bool> IsDayOff,
    Func<IReadOnlyList<(string Title, DateOnly Date)>> Countdowns, Action Changed, Action<DateOnly> GoTo);

/// <summary>
/// "복습 계획 만들기": a topic and a day (learned, or exam day) become a row of review to-dos on growing intervals (1·2·4·7·15·30
/// days). The dates are plain arithmetic (<see cref="ReviewPlanner"/>), the user ticks the ones to add, and they go in as one batch
/// that can be taken back like any other AI addition.
/// </summary>
internal static class ReviewPlanWindow
{
    private static Window? _open;

    public static void ShowOrActivate(ReviewPlanHost host)
    {
        if (_open is not null) { _open.Activate(); return; }
        var win = Build(host, out _, out _);
        _open = win;
        win.Closed += (_, _) => _open = null;
        win.Show();
        win.Activate();
    }

    private sealed class StepView
    {
        public required ReviewStep Step;
        public required CheckBox Check;
        public required Proposal? Proposal;
        public required TextBlock Detail;
        public bool Added;
    }

    /// <summary>The window, wired but not shown. A dev snapshot sets <paramref name="topic"/> and calls <paramref name="refresh"/>.</summary>
    internal static Window Build(ReviewPlanHost host, out TextBox topic, out Action refresh)
    {
        var win = Dlg.Create("복습 계획 만들기", 640, 640);
        win.ShowInTaskbar = true;

        topic = new TextBox { Padding = new Thickness(4, 3, 4, 3), MaxLength = ReviewPlanner.MaxTopic, FontSize = 13 };
        var topicBox = topic;
        var learned = new RadioButton { Content = "공부한 날부터 (1·2·4·7·15·30일 뒤)", IsChecked = true, GroupName = "mode", Margin = new Thickness(0, 0, 0, 3) };
        var exam = new RadioButton { Content = "시험일 앞으로 (30·14·7·3·1일 전)", GroupName = "mode" };
        var dayLabel = new TextBlock { Text = "공부한 날", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var day = new DatePicker { SelectedDate = host.Today().ToDateTime(TimeOnly.MinValue), Width = 130 };
        var pick = new ComboBox { Width = 220, Margin = new Thickness(12, 0, 0, 0), ToolTip = "등록해 둔 D-day 의 날짜를 시험일로 가져옵니다" };
        var skipOff = new CheckBox { Content = "주말·휴일은 피하기", Margin = new Thickness(0, 8, 0, 0), IsChecked = true };
        var summary = new TextBlock { Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold };
        var list = new StackPanel();
        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
        var views = new List<StepView>();
        Button addBtn = null!;
        addBtn = Dlg.Btn("선택한 건 추가", (_, _) => AddSelected());
        addBtn.IsEnabled = false;

        var countdowns = host.Countdowns();
        pick.Items.Add("D-day 에서 고르기…");
        foreach (var (title, date) in countdowns) pick.Items.Add($"{title}  ({date:M월 d일})");
        pick.SelectedIndex = 0;
        pick.Visibility = countdowns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ReviewMode Mode() => exam.IsChecked == true ? ReviewMode.BeforeExam : ReviewMode.AfterLearning;

        void UpdateCount()
        {
            int n = views.Count(v => v.Check.IsChecked == true && v.Check.IsEnabled);
            addBtn.Content = n == 0 ? "선택한 건 추가" : $"선택한 {n}건 추가";
            addBtn.IsEnabled = n > 0 && topicBox.Text.Trim().Length > 0;
        }

        void Refresh()
        {
            views.Clear(); list.Children.Clear();
            dayLabel.Text = Mode() == ReviewMode.BeforeExam ? "시험일" : "공부한 날";
            if (day.SelectedDate is not { } d) { summary.Text = "날짜를 고르세요."; UpdateCount(); return; }
            var mode = Mode();
            var steps = ReviewPlanner.Plan(DateOnly.FromDateTime(d), mode, host.Today(), host.IsDayOff, skipOff.IsChecked == true);
            string name = topicBox.Text.Trim().Length > 0 ? topicBox.Text : "(주제)";
            foreach (var step in steps)
            {
                string content = ReviewPlanner.Content(name, step, steps.Count, mode);
                var proposal = ProposalFactory.FromTodo(step.Date, content, ParseSource.Rules, host.Changes, out _);
                var check = new CheckBox { IsChecked = !step.Past && proposal is not null, IsEnabled = !step.Past && proposal is not null, Margin = new Thickness(0, 3, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                var line1 = new TextBlock { Text = $"{step.Date:yyyy년 M월 d일} ({AiText.Dow[(int)step.Date.DayOfWeek]})", FontWeight = FontWeights.SemiBold };
                string note = step.Past ? "이미 지난 날이라 추가하지 않습니다." : step.Shifted ? "주말·휴일을 피해 옮긴 날짜입니다." : "";
                var detail = new TextBlock { Text = content + (note.Length > 0 ? "   ·   " + note : ""), Foreground = step.Past ? Brushes.Gray : Brushes.Black, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                var body = new StackPanel { Children = { line1, detail } };
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(body, 1);
                row.Children.Add(check); row.Children.Add(body);
                list.Children.Add(new Border
                {
                    Child = row, Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xE7)),
                });
                check.Click += (_, _) => UpdateCount();
                views.Add(new StepView { Step = step, Check = check, Proposal = proposal, Detail = detail });
            }
            summary.Text = steps.Count == 0 ? "만들 수 있는 복습 날이 없습니다." : $"복습 {steps.Count}번 — 추가할 날만 체크하세요.";
            status.Text = "날짜는 계산으로만 정합니다 (망각곡선: 배운 직후에 가깝게, 점점 간격을 넓혀). 할 일 칸에 하나씩 들어가며, 추가한 뒤에도 '마지막 추가 되돌리기'로 한꺼번에 취소할 수 있습니다.";
            UpdateCount();
        }
        refresh = Refresh;

        void AddSelected()
        {
            var picked = views.Where(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Proposal is not null).ToList();
            if (picked.Count == 0 || topicBox.Text.Trim().Length == 0) return;
            var result = host.Batch.Apply(picked.Select(v => v.Proposal!).ToList());
            host.Changed();
            var failed = result.Failed.Select(f => f.Summary).ToHashSet();
            foreach (var v in picked.Where(v => !failed.Contains(v.Proposal!.Summary)))
            {
                v.Added = true; v.Check.IsChecked = false; v.Check.IsEnabled = false; v.Detail.Text += "   ✓ 추가했습니다";
            }
            string text = $"{result.Applied.Count}건을 추가했습니다." + (result.BackupPath is not null ? $" (추가 전 데이터베이스를 백업했습니다: {System.IO.Path.GetFileName(result.BackupPath)})" : "");
            if (result.Failed.Count > 0) text += $"\n추가하지 못한 {result.Failed.Count}건: " + string.Join(" / ", result.Failed.Select(f => f.Error));
            status.Text = text + "\n잘못 추가했다면 '마지막 추가 되돌리기'로 한꺼번에 취소할 수 있습니다.";
            if (picked.Min(v => v.Step.Date) is var first) host.GoTo(first);
            UpdateCount();
        }

        topicBox.TextChanged += (_, _) => Refresh();
        learned.Checked += (_, _) => Refresh();
        exam.Checked += (_, _) => Refresh();
        day.SelectedDateChanged += (_, _) => Refresh();
        skipOff.Click += (_, _) => Refresh();
        pick.SelectionChanged += (_, _) =>
        {
            int i = pick.SelectedIndex - 1;
            if (i < 0 || i >= countdowns.Count) return;
            exam.IsChecked = true;
            day.SelectedDate = countdowns[i].Date.ToDateTime(TimeOnly.MinValue);
        };

        var undo = Dlg.Btn("마지막 추가 되돌리기", (_, _) => AiUndo.Run(win, host.Batch, host.Changed));
        var close = Dlg.Btn("닫기", (_, _) => win.Close());
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(addBtn); buttons.Children.Add(undo); buttons.Children.Add(close);

        var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        dayRow.Children.Add(dayLabel); dayRow.Children.Add(day); dayRow.Children.Add(pick);

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
        foreach (var e in new UIElement[]
                 {
                     new TextBlock { Text = "무엇을 복습할지 적고 날짜를 고르면, 잊기 전에 다시 볼 날들을 할 일로 만들어 드립니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
                     new TextBlock { Text = "복습할 내용 (예: 영어 단어 Day 12)", Margin = new Thickness(0, 0, 0, 3) }, topicBox,
                     new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { learned, exam } },
                     dayRow, skipOff, summary,
                     new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, MaxHeight = 280 },
                     status, buttons,
                 })
            root.Children.Add(e);
        win.Content = root;
        win.Loaded += (_, _) => { Refresh(); topicBox.Focus(); };
        return win;
    }
}
