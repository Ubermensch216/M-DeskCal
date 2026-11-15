using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskCal.Ai;
using DeskCal.Ai.Llm;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace DeskCal.App;

/// <summary>What the briefing window needs from the app.</summary>
internal sealed record BriefingHost(
    Func<DateOnly> Today, Func<DateOnly, DateOnly, Briefing> Build, Func<DateOnly, WeeklyReview> Review,
    Func<AiSettings> Ai, Func<AiSettings, ILlmClient> ClientFactory, BriefingBackoff Backoff, Action<DateOnly> GoTo,
    Func<int>? OverdueCount = null, Action? OpenRollover = null);

/// <summary>
/// "오늘 브리핑" and "지난 7일 돌아보기": the day (and the week behind) in a few lines read straight from the calendar. A model can be asked
/// to put the day into friendlier sentences, but the plain lines stay on screen underneath, and the model's text is thrown away unless it
/// still names every event and D-day (see <see cref="BriefingNarrator"/>).
/// </summary>
internal static class BriefingWindow
{
    private static Window? _open;
    internal static Window? CurrentWindow => _open;

    /// <param name="auto">Opened by the schedule, not by the user: do not take the keyboard focus, and ask the model only if it is switched on for this and not backing off.</param>
    public static void Show(BriefingHost host, bool auto = false, bool polishAutomatically = false)
    {
        if (_open is not null) { if (!auto) _open.Activate(); return; }
        var win = Build(host, out var polish);
        _open = win;
        win.Closed += (_, _) => _open = null;
        win.ShowActivated = !auto;
        win.Show();
        if (!auto) win.Activate();
        if (polishAutomatically && host.Backoff.MayTry()) polish(false);
    }

    /// <summary>The window, wired but not shown. <paramref name="polish"/> asks the model (true = the user pressed the button).</summary>
    internal static Window Build(BriefingHost host, out Action<bool> polish)
    {
        var win = new Window();
        ModernUi.ConfigureWindow(win, "브리핑", 600, 620);
        var day = host.Today();
        Briefing? current = null;

        // ---- today
        var dateText = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush, VerticalAlignment = VerticalAlignment.Center, MinWidth = 220, TextAlignment = TextAlignment.Center };
        var polished = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = ModernUi.InkBrush };
        var polishedNote = new TextBlock { Foreground = ModernUi.MutedBrush, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var polishedBox = new Border
        {
            Child = new StackPanel { Children = { polished, polishedNote } }, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10),
            Background = ModernUi.AccentSoftBrush, BorderBrush = ModernUi.AccentBorderBrush, BorderThickness = new Thickness(1.5),
            Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed,
        };
        var lines = new StackPanel();
        var status = new TextBlock { Foreground = ModernUi.MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        CancellationTokenSource? cts = null;

        void Refresh()
        {
            cts?.Cancel();
            current = host.Build(day, host.Today());
            dateText.Text = current.Header;
            polishedBox.Visibility = Visibility.Collapsed;
            lines.Children.Clear();
            foreach (var l in current.Lines())
            {
                bool isWarn = l.StartsWith("⚠", StringComparison.Ordinal);
                lines.Children.Add(new TextBlock
                {
                    Text = (isWarn ? "" : "• ") + l, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7), FontSize = 13.5,
                    Foreground = isWarn ? ModernUi.WarnBrush : ModernUi.InkBrush,
                    FontWeight = isWarn ? FontWeights.SemiBold : FontWeights.Normal,
                });
            }
            status.Text = "";
        }

        async void Polish(bool manual)
        {
            if (current is null) return;
            var ai = host.Ai();
            if (!ai.Enabled || ai.Model.Length == 0) { if (manual) status.Text = "설정 → AI 에서 AI 를 켜고 모델을 고르면 문장을 다듬어 줍니다. 아래 목록은 그대로 읽을 수 있습니다."; return; }
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var shown = current;
            status.Text = "AI 가 문장을 다듬는 중… (처음에는 모델을 불러오느라 시간이 걸릴 수 있습니다)";
            try
            {
                var (text, rejected) = await BriefingNarrator.PolishAsync(shown, host.ClientFactory(ai), token);
                if (token.IsCancellationRequested || !ReferenceEquals(shown, current)) return;
                if (text is not null)
                {
                    polished.Text = text;
                    polishedNote.Text = "AI 가 다듬은 문장입니다. 일정·시각은 아래 원본 목록과 같은지 확인하세요.";
                    polishedBox.Visibility = Visibility.Visible;
                    status.Text = "";
                    host.Backoff.Succeeded();
                }
                else { status.Text = (rejected ?? "") + " 아래의 원본 목록을 그대로 보여 드립니다."; host.Backoff.Failed(); }
            }
            catch (LlmException ex) { status.Text = ex.Message; host.Backoff.Failed(); }
            catch (OperationCanceledException) { }
        }
        polish = Polish;

        void Move(int delta) { day = delta == 0 ? host.Today() : day.AddDays(delta); Refresh(); }
        var navStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        var prevBtn = ModernUi.ChipBtn("◀", (_, _) => Move(-1));
        var nextBtn = ModernUi.ChipBtn("▶", (_, _) => Move(1));
        var todayBtn = ModernUi.ChipBtn("오늘", (_, _) => Move(0));
        todayBtn.Margin = new Thickness(8, 0, 0, 0);
        navStack.Children.Add(prevBtn); navStack.Children.Add(dateText); navStack.Children.Add(nextBtn); navStack.Children.Add(todayBtn);
        var navCard = ModernUi.Card(navStack, new Thickness(12, 9, 12, 9));

        var rawCard = ModernUi.Card(new StackPanel
        {
            Children =
            {
                ModernUi.FieldLabel("원본 (달력에서 읽은 그대로)", new Thickness(0, 0, 0, 8)),
                lines,
            }
        }, new Thickness(16, 14, 16, 10));

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(ModernUi.PrimaryBtn("AI 로 문장 다듬기", (_, _) => Polish(true)));
        actions.Children.Add(ModernUi.SecondaryBtn("달력에서 보기", (_, _) => host.GoTo(day)));
        if (host.OpenRollover is { } openRollover)
        {
            int overdue = host.OverdueCount?.Invoke() ?? 0;
            actions.Children.Add(ModernUi.SecondaryBtn(overdue > 0 ? $"미완료 정리 ({overdue}건)" : "미완료 정리", (_, _) => openRollover()));
        }
        var today = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        foreach (var e in new UIElement[] { navCard, polishedBox, rawCard, status, actions })
            today.Children.Add(e);

        // ---- the week behind
        var reviewItems = new StackPanel();
        void RefreshReview()
        {
            reviewItems.Children.Clear();
            var r = host.Review(host.Today());
            int n = 0;
            foreach (var l in r.Lines())
                reviewItems.Children.Add(new TextBlock
                {
                    Text = (n++ == 0 ? "" : "• ") + l, FontWeight = n == 1 ? FontWeights.SemiBold : FontWeights.Normal, FontSize = n == 1 ? 15 : 13.5,
                    Foreground = ModernUi.InkBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                });
            reviewItems.Children.Add(new TextBlock
            {
                Text = "끝낸 것·미룬 것의 기록은 이 기능이 켜진 날부터 쌓입니다. '미룸'은 할 일을 다른 날(더 뒤)로 옮긴 경우입니다.",
                Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });
        }
        var reviewCard = ModernUi.Card(reviewItems, new Thickness(16, 14, 16, 12), new Thickness(16, 14, 16, 14));

        var tabs = new TabControl { Margin = new Thickness(12, 10, 12, 12), Background = ModernUi.BgBrush, BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "브리핑", Content = new ScrollViewer { Content = today, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var reviewTab = new TabItem { Header = "지난 7일 돌아보기", Content = new ScrollViewer { Content = reviewCard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        tabs.Items.Add(reviewTab);
        tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == tabs && tabs.SelectedItem == reviewTab) RefreshReview(); };

        var header = ModernUi.Header(ModernUi.Icons.Briefing, "오늘 브리핑", "오늘 일정 요약과 지난 7일 돌아보기를 한눈에 확인합니다");
        var closeBtn = ModernUi.SecondaryBtn("닫기", (_, _) => win.Close());
        var footer = ModernUi.ActionFooter([], closeBtn);
        win.Content = ModernUi.PageShell(header, footer, tabs);
        win.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) win.Close(); };
        win.Closed += (_, _) => cts?.Cancel();
        Refresh();
        RefreshReview();
        return win;
    }
}
