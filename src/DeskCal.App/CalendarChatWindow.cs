using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskCal.Ai;
using DeskCal.Ai.Intent;
using DeskCal.Ai.Llm;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.App;

internal sealed record CalendarChatHost(
    QuickInputService Service,
    AiBatchService Batch,
    QueryService Query,
    AccuracyStore Accuracy,
    SearchService Search,
    ItemRepository Items,
    HistoryRepository History,
    Func<DateOnly> Today,
    Func<DayOfWeek> WeekStart,
    Action Changed,
    Action<DateOnly> GoTo,
    Action<DateOnly> OpenEditor,
    SemanticSearchService? Semantic = null,
    Func<Task>? SyncIndex = null,
    Func<AiSettings>? GetAiSettings = null,
    Func<AiSettings, ILlmClient>? LlmFactory = null,
    ExtractionService? Extraction = null);

/// <summary>
/// "캘린더 채팅": unified natural-language assistant that merges quick event/to-do entry,
/// schedule queries, keyword/semantic search, AI conversational help, and extraction of dated events from
/// pasted text or a captured image (a notice, an e-mail, a conversation) in one window. A pasted text of several
/// lines, or one read from a picture, is cut into sentences and offered as a check list; nothing is added until
/// the user confirms it.
/// </summary>
internal static class CalendarChatWindow
{
    private static Window? _open;
    private static Action<string>? _openSend;
    private static TextBox? _openInput;
    private static Action<string>? _openDraft;
    private static Action<System.Windows.Media.Imaging.BitmapSource>? _openImage;
    internal static Window? CurrentWindow => _open;

    /// <summary>
    /// Opens the window with text put in its input box (not sent: the user checks it and presses Enter) - a pasted notice
    /// from the calendar's one-line header box.
    /// </summary>
    public static void ShowWithDraft(CalendarChatHost host, string draft)
    {
        ShowOrActivate(host);
        _openDraft?.Invoke(draft);
    }

    /// <summary>Opens the window and reads a pasted picture into its input box (OCR).</summary>
    public static void ShowWithImage(CalendarChatHost host, System.Windows.Media.Imaging.BitmapSource image)
    {
        ShowOrActivate(host);
        _openImage?.Invoke(image);
    }

    private static readonly SolidColorBrush ChipHoverBrush = ModernUi.Frozen(Color.FromRgb(0xDB, 0xEA, 0xFE));
    private static readonly SolidColorBrush SundayBrush = ModernUi.Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush SaturdayBrush = ModernUi.Frozen(Color.FromRgb(0x25, 0x63, 0xEB));

    private sealed record FilterState(
        ComboBox Range, ComboBox Kind, ComboBox Done, ComboBox Color,
        CheckBox Meaning, CheckBox Deleted);

    public static void ShowOrActivate(CalendarChatHost host, string? initialPrompt = null)
    {
        if (_open is not null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Show();
            _open.Activate();
            if (!string.IsNullOrWhiteSpace(initialPrompt)) _openSend?.Invoke(initialPrompt);
            else _openInput?.Focus();
            return;
        }
        var win = Build(host, out var box, out _, out var sendPrompt, out var setDraft, out var pasteImage);
        _open = win;
        _openSend = sendPrompt;
        _openInput = box;
        _openDraft = setDraft;
        _openImage = pasteImage;
        win.Closed += (_, _) => { _open = null; _openSend = null; _openInput = null; _openDraft = null; _openImage = null; };
        win.Show();
        win.Activate();
        if (!string.IsNullOrWhiteSpace(initialPrompt))
            win.Dispatcher.BeginInvoke(() => sendPrompt(initialPrompt));
    }

    internal static Window Build(CalendarChatHost host, out TextBox input, out Action send)
        => Build(host, out input, out send, out _, out _, out _);

    private static Window Build(CalendarChatHost host, out TextBox input, out Action send, out Action<string> sendPrompt,
        out Action<string> setDraft, out Action<System.Windows.Media.Imaging.BitmapSource> pasteImage)
    {
        var win = new Window();
        ModernUi.ConfigureWindow(win, "캘린더 채팅", 760, 740, topmost: false);
        win.MinWidth = 580;
        win.MinHeight = 540;

        var messages = new StackPanel { Margin = new Thickness(0, 4, 4, 8) };
        var feed = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = messages,
            Padding = new Thickness(0, 2, 2, 4),
        };

        // ---- Collapsible advanced search filter bar (preserves full SearchWindow capabilities) ----
        var range = new ComboBox { Width = 114, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var s in new[] { "전체 기간", "오늘", "이번 주", "이번 달", "지난 30일", "앞으로 30일" })
            range.Items.Add(s);
        range.SelectedIndex = 0;

        var kind = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var s in new[] { "할 일 + 일정", "할 일만", "일정만" }) kind.Items.Add(s);
        kind.SelectedIndex = 0;

        var done = new ComboBox { Width = 114, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var s in new[] { "완료 여부 무관", "완료한 것만", "미완료만" }) done.Items.Add(s);
        done.SelectedIndex = 0;

        var color = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        color.Items.Add("모든 글자색");
        color.Items.Add("기본색");
        foreach (var (name, hex) in MainWindow.Palette.Where(p => p.Hex is not null))
        {
            var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            swatchRow.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = UiColors.TryBrush(hex) ?? ModernUi.MutedBrush,
                BorderBrush = ModernUi.InputBorderBrush,
                BorderThickness = new Thickness(0.8),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            swatchRow.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = ModernUi.InkBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            color.Items.Add(new ComboBoxItem { Content = swatchRow, Tag = hex });
        }
        color.SelectedIndex = 0;

        var meaning = new CheckBox
        {
            Content = "의미 검색",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            IsEnabled = host.Semantic is not null,
            ToolTip = host.Semantic is not null ? "표현이 달라도 뜻이 비슷한 일정·할 일을 찾습니다" : "설정 → AI 에서 의미 검색 색인을 만들면 쓸 수 있습니다",
        };
        var deleted = new CheckBox { Content = "삭제된 할 일 포함", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };

        var filterRunBtn = ModernUi.PrimaryBtn("필터로 검색", (_, _) => { });
        filterRunBtn.Padding = new Thickness(12, 5, 12, 5);
        filterRunBtn.FontSize = 12;

        var filterResetBtn = ModernUi.SecondaryBtn("초기화", (_, _) => { });
        filterResetBtn.Padding = new Thickness(10, 5, 10, 5);
        filterResetBtn.FontSize = 12;
        filterResetBtn.Margin = new Thickness(0);

        var filterRow1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        filterRow1.Children.Add(LabeledFilter("기간", range));
        filterRow1.Children.Add(LabeledFilter("유형", kind));
        filterRow1.Children.Add(LabeledFilter("상태", done));
        filterRow1.Children.Add(LabeledFilter("색상", color));

        var filterRow2 = new DockPanel();
        var filterActions = new StackPanel { Orientation = Orientation.Horizontal };
        filterActions.Children.Add(filterRunBtn);
        filterActions.Children.Add(filterResetBtn);
        DockPanel.SetDock(filterActions, Dock.Right);
        var filterChecks = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        filterChecks.Children.Add(meaning);
        filterChecks.Children.Add(deleted);
        filterRow2.Children.Add(filterActions);
        filterRow2.Children.Add(filterChecks);

        var filterStack = new StackPanel();
        filterStack.Children.Add(filterRow1);
        filterStack.Children.Add(filterRow2);
        var filterCard = ModernUi.Card(filterStack, padding: new Thickness(14, 10, 14, 10), margin: new Thickness(0, 0, 0, 10), background: ModernUi.CardAltBrush);
        filterCard.Visibility = Visibility.Collapsed;

        var filters = new FilterState(range, kind, done, color, meaning, deleted);

        // ---- Quick prompt chips + Filter toggle ----
        var chipRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var filterToggle = MakePillButton(
            "상세 필터 ▾",
            ModernUi.CardBrush,
            ModernUi.HoverBrush,
            ModernUi.InkBrush,
            ModernUi.InputBorderBrush,
            iconData: ModernUi.Icons.GroupSettings);

        int ActiveFilterCount()
        {
            int count = 0;
            if (range.SelectedIndex != 0) count++;
            if (kind.SelectedIndex != 0) count++;
            if (done.SelectedIndex != 0) count++;
            if (color.SelectedIndex != 0) count++;
            if (meaning.IsChecked == true) count++;
            if (deleted.IsChecked == true) count++;
            return count;
        }
        bool HasActiveFilter() => ActiveFilterCount() > 0;

        void UpdateFilterToggleVisual()
        {
            bool open = filterCard.Visibility == Visibility.Visible;
            int activeCount = ActiveFilterCount();
            bool active = activeCount > 0;
            string label = active ? $"상세 필터 ({activeCount})" : "상세 필터";
            var fg = (open || active) ? ModernUi.AccentBrush : ModernUi.InkBrush;
            var bg = (open || active) ? ModernUi.AccentSoftBrush : ModernUi.CardBrush;
            var bd = (open || active) ? ModernUi.AccentBorderBrush : ModernUi.InputBorderBrush;
            SetPillButtonVisual(filterToggle, open ? $"{label} ▴" : $"{label} ▾", fg, bg, bd);
        }

        filterToggle.Click += (_, _) =>
        {
            bool open = filterCard.Visibility == Visibility.Visible;
            filterCard.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            UpdateFilterToggleVisual();
        };

        filterResetBtn.Click += (_, _) =>
        {
            range.SelectedIndex = 0;
            kind.SelectedIndex = 0;
            done.SelectedIndex = 0;
            color.SelectedIndex = 0;
            meaning.IsChecked = false;
            deleted.IsChecked = false;
            UpdateFilterToggleVisual();
        };

        foreach (var c in new[] { range, kind, done, color })
            c.SelectionChanged += (_, _) => UpdateFilterToggleVisual();
        meaning.Click += (_, _) =>
        {
            UpdateFilterToggleVisual();
            if (meaning.IsChecked == true && host.SyncIndex is not null) _ = host.SyncIndex();
        };
        deleted.Click += (_, _) => UpdateFilterToggleVisual();

        chipRow.Children.Add(filterToggle);

        // ---- Composer ----
        var box = new TextBox
        {
            FontSize = 13.5,
            Padding = new Thickness(12, 9, 32, 9),
            AcceptsReturn = true,                     // a pasted notice keeps its lines; Enter sends, Shift+Enter breaks the line
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 150,
            MaxLength = TextExtractor.MaxChars,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ModernUi.CardBrush,
            Foreground = ModernUi.InkBrush,
            BorderBrush = ModernUi.InputBorderBrush,
            BorderThickness = new Thickness(1),
        };
        input = box;

        var placeholder = new TextBlock
        {
            Text = "일정 등록·조회·검색, 또는 공지 글·캡처 이미지 붙여넣기",
            FontSize = 13,
            Foreground = ModernUi.MutedBrush,
            Opacity = 0.75,
            Margin = new Thickness(13, 10, 34, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var clearInputBtn = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            FontSize = 11,
            Foreground = ModernUi.MutedBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 6, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "입력 지우기",
            Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(11)),
        };
        clearInputBtn.Click += (_, _) => { box.Text = ""; box.Focus(); };

        var boxContainer = new Grid();
        boxContainer.Children.Add(box);
        boxContainer.Children.Add(placeholder);
        boxContainer.Children.Add(clearInputBtn);

        var sendBtn = ModernUi.PrimaryBtn("보내기 (Enter)", (_, _) => { }, isDefault: true);
        sendBtn.Margin = new Thickness(8, 0, 0, 0);
        sendBtn.Padding = new Thickness(16, 9, 16, 9);
        sendBtn.VerticalAlignment = VerticalAlignment.Bottom;

        var imageBtn = ModernUi.SecondaryBtn("이미지", (_, _) => { });
        imageBtn.Margin = new Thickness(8, 0, 0, 0);
        imageBtn.Padding = new Thickness(12, 9, 12, 9);
        imageBtn.VerticalAlignment = VerticalAlignment.Bottom;
        imageBtn.ToolTip = "캡처 이미지 파일에서 글자를 읽어 입력칸에 넣습니다 (Ctrl+V 로 붙여넣거나 창에 끌어다 놓아도 됩니다)";
        imageBtn.Visibility = host.Extraction is null ? Visibility.Collapsed : Visibility.Visible;

        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(boxContainer, 0);
        Grid.SetColumn(imageBtn, 1);
        Grid.SetColumn(sendBtn, 2);
        inputGrid.Children.Add(boxContainer);
        inputGrid.Children.Add(imageBtn);
        inputGrid.Children.Add(sendBtn);

        var liveBadge = ModernUi.Pill("안내", ModernUi.MutedBrush, ModernUi.HoverBrush);
        liveBadge.Margin = new Thickness(0, 0, 8, 0);

        var livePreview = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ModernUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "\"이번 주 일정 보여줘\" · \"오늘 빈 시간\" · \"주간보고 찾아줘\" · \"다음 주 화요일 3시 치과\"",
        };

        var previewRow = new DockPanel { Margin = new Thickness(2, 7, 2, 0) };
        DockPanel.SetDock(liveBadge, Dock.Left);
        previewRow.Children.Add(liveBadge);
        previewRow.Children.Add(livePreview);

        var composerStack = new StackPanel();
        composerStack.Children.Add(inputGrid);
        composerStack.Children.Add(previewRow);
        var composerCard = ModernUi.Card(composerStack, padding: new Thickness(12, 10, 12, 9), margin: new Thickness(0, 8, 0, 0));

        CancellationTokenSource? cts = null;
        bool busy = false;
        var promptHistory = new List<string>();
        int historyIdx = -1;
        string draftBeforeHistory = "";
        bool fromImage = false;                       // the text in the box was read from a picture (OCR can misread numbers)

        // ---- Footer ----
        Button undo = null!;
        void UpdateUndo() => undo.IsEnabled = host.Batch.Last() is not null;
        undo = ModernUi.SecondaryBtn("마지막 추가 되돌리기", (_, _) => { AiUndo.Run(win, host.Batch, host.Changed); UpdateUndo(); });
        var clearBtn = ModernUi.SecondaryBtn("대화 지우기", (_, _) =>
        {
            cts?.Cancel();
            messages.Children.Clear();
            AddWelcome(messages, host.Service.AiAvailable, p => SendPrompt(p));
            feed.ScrollToEnd();
            box.Focus();
        });
        var closeBtn = ModernUi.SecondaryBtn("닫기 (Esc)", (_, _) => win.Close());
        var footer = ModernUi.ActionFooter([undo, clearBtn], closeBtn);

        void ScrollBottom()
        {
            feed.ScrollToEnd();
            win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => feed.ScrollToEnd());
        }

        void SetLiveBadge(string tag, Brush fg, Brush bg, string message)
        {
            if (liveBadge.Child is TextBlock tb)
            {
                tb.Text = tag;
                tb.Foreground = fg;
            }
            liveBadge.Background = bg;
            livePreview.Foreground = fg == ModernUi.MutedBrush ? ModernUi.MutedBrush : fg;
            livePreview.Text = message;
        }

        void AppendUserBubble(string text)
        {
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            if (lines.Length > 8) text = string.Join("\n", lines.Take(8)) + $"\n… (외 {lines.Length - 8}줄)";
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13,
                LineHeight = 19,
            };
            var bubble = new Border
            {
                Background = ModernUi.AccentBrush,
                CornerRadius = new CornerRadius(14, 14, 3, 14),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(72, 4, 2, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = tb,
            };
            messages.Children.Add(bubble);
            ScrollBottom();
        }

        UIElement WrapWithAssistantAvatar(UIElement cardOrBubble)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 16, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                Background = ModernUi.AccentSoftBrush,
                BorderBrush = ModernUi.AccentBorderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 2, 10, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = ModernUi.FlatIcon(ModernUi.Icons.Sparkles, ModernUi.AccentBrush, 14, 2.0),
            };
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(cardOrBubble, 1);
            row.Children.Add(avatar);
            row.Children.Add(cardOrBubble);
            return row;
        }

        void AppendAssistantText(string text, Brush? bg = null, Brush? fg = null)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = fg ?? ModernUi.InkBrush,
                FontSize = 12.5,
                LineHeight = 19,
            };
            var bubble = new Border
            {
                Background = bg ?? ModernUi.CardBrush,
                BorderBrush = ModernUi.LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3, 12, 12, 12),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = tb,
            };
            messages.Children.Add(WrapWithAssistantAvatar(bubble));
            ScrollBottom();
        }

        void AppendProposalCard(Proposal p, string rawInput)
        {
            var (title, when, more) = AiText.Describe(p);
            var targetDate = AiText.DateOf(p) ?? host.Today();
            bool viaAi = p.Source == ParseSource.Llm;

            var cardBody = new StackPanel();

            // Top row: Interpretation badge on left + Accuracy feedback on right
            var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var badge = ModernUi.Pill(
                viaAi ? "AI 해석 (날짜·시각 확인)" : "규칙 해석",
                viaAi ? ModernUi.WarnBrush : ModernUi.AccentBrush,
                viaAi ? ModernUi.WarnSoftBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(badge, Dock.Left);

            var rightBtn = ModernUi.ChipBtn("맞음", (_, _) => { });
            var wrongBtn = ModernUi.ChipBtn("틀림", (_, _) => { });
            rightBtn.Padding = wrongBtn.Padding = new Thickness(8, 2, 8, 2);
            rightBtn.FontSize = wrongBtn.FontSize = 11;
            rightBtn.Margin = new Thickness(0, 0, 4, 0);
            wrongBtn.Margin = new Thickness(0);
            var fbLabel = new TextBlock
            {
                Text = "해석 정확도:",
                FontSize = 11,
                Foreground = ModernUi.MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            bool fbDone = false;
            void Mark(bool ok)
            {
                if (fbDone) return;
                fbDone = true;
                host.Accuracy.Record(p.Source, ok);
                fbLabel.Text = ok ? "기록됨 (맞음 ✓)" : "기록됨 (틀림)";
                rightBtn.Visibility = wrongBtn.Visibility = Visibility.Collapsed;
            }
            rightBtn.Click += (_, _) => Mark(true);
            wrongBtn.Click += (_, _) => Mark(false);

            var fbPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            fbPanel.Children.Add(fbLabel);
            fbPanel.Children.Add(rightBtn);
            fbPanel.Children.Add(wrongBtn);
            DockPanel.SetDock(fbPanel, Dock.Right);

            topRow.Children.Add(badge);
            topRow.Children.Add(fbPanel);
            cardBody.Children.Add(topRow);

            cardBody.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            cardBody.Children.Add(new TextBlock
            {
                Text = when,
                FontSize = 12.5,
                Foreground = ModernUi.InkBrush,
                Margin = new Thickness(0, 4, 0, 0),
            });
            if (more.Length > 0)
                cardBody.Children.Add(new TextBlock
                {
                    Text = more,
                    FontSize = 12,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            var notes = p.Warnings.Where(w => !w.StartsWith("AI 가 해석", StringComparison.Ordinal)).ToList();
            if (notes.Count > 0)
                cardBody.Children.Add(new TextBlock
                {
                    Text = string.Join("\n", notes.Select(n => "· " + n)),
                    FontSize = 11.5,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            if (p.ConflictList.Count > 0)
            {
                var clashBox = new Border
                {
                    Background = ModernUi.DangerSoftBrush,
                    BorderBrush = ModernUi.DangerBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "⚠ " + string.Join("\n⚠ ", p.ConflictList),
                        FontSize = 12,
                        Foreground = ModernUi.DangerBrush,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                cardBody.Children.Add(clashBox);
            }

            var statusNote = new TextBlock { FontSize = 11.5, Foreground = ModernUi.SuccessBrush, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

            Button addBtn = null!;
            Button editBtn = null!;
            addBtn = ModernUi.PrimaryBtn("캘린더에 추가", (_, _) =>
            {
                var res = host.Batch.Apply([p]);
                if (res.Failed.Count > 0)
                {
                    statusNote.Foreground = ModernUi.DangerBrush;
                    statusNote.Text = "추가하지 못했습니다: " + res.Failed[0].Error;
                    statusNote.Visibility = Visibility.Visible;
                    return;
                }
                host.Changed();
                host.GoTo(targetDate);
                UpdateUndo();
                addBtn.IsEnabled = false;
                addBtn.Content = "추가됨 ✓";
                statusNote.Foreground = ModernUi.SuccessBrush;
                statusNote.Text = $"{targetDate:yyyy-MM-dd} 날짜에 등록하고 달력을 이동했습니다.";
                statusNote.Visibility = Visibility.Visible;
            });
            editBtn = ModernUi.SecondaryBtn("편집기로 열기", (_, _) => host.OpenEditor(targetDate));

            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(addBtn);
            actions.Children.Add(editBtn);

            cardBody.Children.Add(actions);
            cardBody.Children.Add(statusNote);

            var card = ModernUi.Card(cardBody, padding: new Thickness(14, 12, 14, 12), margin: new Thickness(0));
            card.BorderBrush = ModernUi.AccentBorderBrush;
            card.BorderThickness = new Thickness(1.5);
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        // ---- pasted text / captured image → a check list of dated sentences, added only on confirmation ----
        void AppendExtractionCard(IReadOnlyList<ExtractedRow> rows, bool readFromImage)
        {
            var found = rows.Where(r => r.Candidate.Kind != CandidateKind.Skipped).ToList();
            var skipped = rows.Where(r => r.Candidate.Kind == CandidateKind.Skipped).ToList();
            var stack = new StackPanel();

            var headerRow = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
            var countPill = ModernUi.Pill(
                found.Count == 0 ? "없음" : $"{found.Count}건",
                found.Count == 0 ? ModernUi.MutedBrush : ModernUi.AccentBrush,
                found.Count == 0 ? ModernUi.HoverBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(countPill, Dock.Right);
            headerRow.Children.Add(countPill);
            headerRow.Children.Add(new TextBlock
            {
                Text = found.Count == 0 ? "날짜가 있는 문장을 찾지 못했습니다" : "글에서 찾은 일정 — 캘린더에 등록할까요?",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(headerRow);

            if (found.Count > 0)
                stack.Children.Add(new TextBlock
                {
                    Text = readFromImage
                        ? "이미지에서 읽은 글자 기준입니다. OCR 은 숫자를 틀리게 읽을 수 있으니 날짜·시각을 이미지와 꼭 대조하세요."
                        : $"등록할 항목만 체크하고, 제목은 고칠 수 있습니다. 상대 표현(내일·다음 주 …)은 오늘({host.Today():yyyy-MM-dd}) 기준입니다.",
                    FontSize = 11.5,
                    Foreground = readFromImage ? ModernUi.WarnBrush : ModernUi.MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 0, 2, 8),
                });

            var views = new List<(CheckBox Check, Func<ExtractedRow> Row, Action<string> Done)>();
            Button addBtn = null!;
            var statusNote = new TextBlock { FontSize = 11.5, Margin = new Thickness(2, 8, 2, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

            void UpdateCount()
            {
                int n = views.Count(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Row().Proposal is not null);
                addBtn.Content = n == 0 ? "선택한 항목 등록" : $"선택한 {n}건 캘린더에 등록";
                addBtn.IsEnabled = n > 0;
            }

            foreach (var first in found)
            {
                var row = first;
                bool added = false;
                var check = new CheckBox { IsChecked = row.SelectedByDefault, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                bool todo = row.Candidate.Kind == CandidateKind.Todo;
                var kindPill = ModernUi.Pill(todo ? "할 일" : "일정", todo ? ModernUi.SuccessBrush : ModernUi.AccentBrush, todo ? ModernUi.SuccessSoftBrush : ModernUi.AccentSoftBrush);
                kindPill.VerticalAlignment = VerticalAlignment.Top;
                kindPill.Margin = new Thickness(0, 3, 10, 0);

                var title = new TextBox { Text = row.Candidate.Title, Padding = new Thickness(8, 5, 8, 5), FontWeight = FontWeights.SemiBold };
                var detail = new TextBlock { Margin = new Thickness(0, 5, 0, 0), Foreground = ModernUi.InkBrush, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                var clash = new TextBlock { Foreground = ModernUi.WarnBrush, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                var notes = new TextBlock { Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                var source = new TextBlock { Text = "원문: " + row.Candidate.Source, Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

                void Refresh()
                {
                    if (row.Proposal is { } p)
                    {
                        var (_, when, more) = AiText.Describe(p);
                        detail.Text = when + (more.Length > 0 ? "   ·   " + more : "") + (added ? "   ✓ 등록했습니다" : "");
                        check.IsEnabled = !added;
                    }
                    else { detail.Text = "제목이 비어 있습니다."; check.IsChecked = false; check.IsEnabled = false; }
                    var n = row.Candidate.Parse.Event.Notes;
                    notes.Text = string.Join("\n", n.Select(x => "· " + x));
                    notes.Visibility = n.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                    var c = row.Conflicts.Select(x => "⚠ " + x).ToList();
                    if (row.Duplicate) c.Insert(0, "이미 달력에 있는 것 같습니다 (같은 제목·같은 시각).");
                    clash.Text = string.Join("\n", c);
                    clash.Visibility = c.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                }

                title.LostFocus += (_, _) =>
                {
                    string t = title.Text.Trim();
                    if (added || t == row.Candidate.Title || host.Extraction is null) return;
                    row = host.Extraction.WithTitle(row, t);
                    check.IsChecked = row.SelectedByDefault;
                    Refresh();
                    UpdateCount();
                };
                check.Click += (_, _) => UpdateCount();
                views.Add((check, () => row, _ => { added = true; check.IsChecked = false; title.IsReadOnly = true; Refresh(); }));

                var body = new StackPanel();
                foreach (var e in new UIElement[] { title, detail, clash, notes, source }) body.Children.Add(e);
                var cardRow = ModernUi.CheckCardRow(check, body, badge: kindPill);
                cardRow.Margin = new Thickness(0, 0, 0, 6);
                stack.Children.Add(cardRow);
                Refresh();
            }

            addBtn = ModernUi.PrimaryBtn("선택한 항목 등록", (_, _) =>
            {
                var picked = views.Where(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Row().Proposal is not null).ToList();
                if (picked.Count == 0) return;
                var result = host.Batch.Apply(picked.Select(v => v.Row().Proposal!).ToList());
                host.Changed();
                var failed = result.Failed.Select(f => f.Summary).ToHashSet();
                foreach (var v in picked.Where(v => !failed.Contains(v.Row().Proposal!.Summary))) v.Done("");
                string msg = $"{result.Applied.Count}건을 캘린더에 등록했습니다.";
                if (result.Failed.Count > 0) msg += $"\n등록하지 못한 {result.Failed.Count}건: " + string.Join(" / ", result.Failed.Select(f => f.Error));
                msg += "\n잘못 등록했다면 아래 '마지막 추가 되돌리기'로 한꺼번에 취소할 수 있습니다.";
                statusNote.Text = msg;
                statusNote.Foreground = result.Failed.Count > 0 ? ModernUi.WarnBrush : ModernUi.SuccessBrush;
                statusNote.Visibility = Visibility.Visible;
                UpdateUndo();
                UpdateCount();
                if (picked.Select(v => AiText.DateOf(v.Row().Proposal!)).Where(d => d is not null).Min() is { } firstDay) host.GoTo(firstDay);
            });

            if (found.Count > 0)
            {
                var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                actions.Children.Add(addBtn);
                stack.Children.Add(actions);
                stack.Children.Add(statusNote);
                UpdateCount();
            }

            if (skipped.Count > 0)
            {
                var skippedBox = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var s in skipped)
                    skippedBox.Children.Add(new TextBlock { Text = $"· {s.Candidate.Source}   →   {s.Candidate.Reason}", Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
                stack.Children.Add(new Expander
                {
                    Header = $"제외된 문장 ({skipped.Count}) — 날짜가 없거나 읽을 수 없는 문장",
                    Content = skippedBox,
                    IsExpanded = found.Count == 0,
                    Margin = new Thickness(0, 8, 0, 0),
                    Foreground = ModernUi.MutedBrush,
                });
            }

            var card = ModernUi.Card(stack, padding: new Thickness(14, 12, 14, 12), margin: new Thickness(0));
            if (found.Count > 0) { card.BorderBrush = ModernUi.AccentBorderBrush; card.BorderThickness = new Thickness(1.5); }
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        // ---- a picture instead of text: read with the OCR that comes with Windows (offline; the picture is not kept) ----
        async Task LoadImageAsync(Func<Task<OcrReading>> read)
        {
            if (host.Extraction is null || busy) return;
            busy = true;
            sendBtn.IsEnabled = imageBtn.IsEnabled = false;
            SetLiveBadge("이미지 읽는 중", ModernUi.AccentBrush, ModernUi.AccentSoftBrush, "이미지에서 글자를 읽고 있습니다…");
            try
            {
                var r = await read();
                if (string.IsNullOrWhiteSpace(r.Text))
                {
                    AppendAssistantText("이미지에서 글자를 찾지 못했습니다. 글자가 크고 또렷한 이미지를 쓰세요.", ModernUi.WarnSoftBrush, ModernUi.WarnBrush);
                    return;
                }
                box.Text = string.IsNullOrWhiteSpace(box.Text) ? r.Text : box.Text.TrimEnd() + "\n" + r.Text;
                fromImage = true;
                box.CaretIndex = box.Text.Length;
                box.Focus();
            }
            catch (OcrException ex) { AppendAssistantText(ex.Message, ModernUi.WarnSoftBrush, ModernUi.WarnBrush); }
            catch (OperationCanceledException) { }
            finally
            {
                busy = false;
                sendBtn.IsEnabled = imageBtn.IsEnabled = true;
                UpdateLivePreview();
            }
        }

        imageBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "글자를 읽을 이미지", Filter = "그림 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|모든 파일|*.*" };
            if (dlg.ShowDialog(win) == true) _ = LoadImageAsync(() => OcrReader.ReadFileAsync(dlg.FileName));
        };
        if (host.Extraction is not null)
        {
            win.AllowDrop = true;
            win.PreviewDragOver += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            win.PreviewDrop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
                e.Handled = true;
                _ = LoadImageAsync(() => OcrReader.ReadFileAsync(files[0]));
            };
        }

        void AppendQueryCard(QueryRequest q, IReadOnlyList<QueryRow> rows)
        {
            var stack = new StackPanel();

            var headerRow = new DockPanel { Margin = new Thickness(2, 0, 2, rows.Count == 0 ? 0 : 8) };
            var countPill = ModernUi.Pill(
                rows.Count == 0 ? "없음" : $"{rows.Count}건",
                rows.Count == 0 ? ModernUi.MutedBrush : ModernUi.AccentBrush,
                rows.Count == 0 ? ModernUi.HoverBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(countPill, Dock.Right);
            headerRow.Children.Add(countPill);
            headerRow.Children.Add(new TextBlock
            {
                Text = q.Label,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(headerRow);

            if (rows.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "해당 기간에 등록된 일정이나 할 일이 없습니다.",
                    FontSize = 12.5,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(2, 4, 2, 2),
                });
            }
            else
            {
                stack.Children.Add(new Border { Height = 1, Background = ModernUi.LineBrush, Margin = new Thickness(0, 0, 0, 4) });
            }

            DateOnly? prevDate = null;
            foreach (var r in rows.Take(40))
            {
                bool sameDayAsPrev = prevDate == r.Date;
                bool isFirstRow = prevDate is null;
                prevDate = r.Date;

                if (!isFirstRow && !sameDayAsPrev)
                {
                    stack.Children.Add(new Border
                    {
                        Height = 1,
                        Background = ModernUi.CardAltBrush,
                        Margin = new Thickness(4, 2, 4, 2),
                    });
                }

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Brush dateFg = r.Date.DayOfWeek switch
                {
                    DayOfWeek.Sunday => SundayBrush,
                    DayOfWeek.Saturday => SaturdayBrush,
                    _ => ModernUi.InkBrush,
                };

                var dateBlock = new TextBlock
                {
                    Text = sameDayAsPrev ? "" : $"{r.Date:MM-dd} ({AiText.Dow[(int)r.Date.DayOfWeek]})",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = dateFg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(dateBlock, 0);

                Border kindBadge;
                if (r.IsItem)
                {
                    kindBadge = ModernUi.Pill(
                        r.Done ? "✓ 완료" : "할 일",
                        r.Done ? ModernUi.MutedBrush : ModernUi.SuccessBrush,
                        r.Done ? ModernUi.HoverBrush : ModernUi.SuccessSoftBrush);
                }
                else if (q.FreeTime)
                {
                    kindBadge = ModernUi.Pill(r.Time, ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush);
                }
                else
                {
                    kindBadge = ModernUi.Pill(
                        r.Time,
                        r.Time == "종일" ? ModernUi.AccentBrush : ModernUi.InkBrush,
                        r.Time == "종일" ? ModernUi.AccentSoftBrush : ModernUi.CardAltBrush,
                        border: r.Time == "종일" ? null : ModernUi.LineBrush);
                }
                kindBadge.HorizontalAlignment = HorizontalAlignment.Left;
                kindBadge.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(kindBadge, 1);

                var titleBlock = new TextBlock
                {
                    Text = r.Title,
                    FontSize = 12.5,
                    Foreground = r.IsItem && r.Done ? ModernUi.MutedBrush : ModernUi.InkBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (r.IsItem && r.Done)
                {
                    titleBlock.TextDecorations = TextDecorations.Strikethrough;
                    titleBlock.Opacity = 0.65;
                }
                Grid.SetColumn(titleBlock, 2);

                var jumpHint = new TextBlock
                {
                    Text = "이동 ›",
                    FontSize = 11,
                    Foreground = ModernUi.MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 2, 0),
                };
                Grid.SetColumn(jumpHint, 3);

                rowGrid.Children.Add(dateBlock);
                rowGrid.Children.Add(kindBadge);
                rowGrid.Children.Add(titleBlock);
                rowGrid.Children.Add(jumpHint);

                var rowBtn = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 5, 6, 5),
                    Margin = new Thickness(0, 1, 0, 1),
                    Cursor = Cursors.Hand,
                    Content = rowGrid,
                    ToolTip = $"{r.Date:yyyy-MM-dd} 날짜로 달력 이동",
                    Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(6)),
                };
                rowBtn.Click += (_, _) => host.GoTo(r.Date);
                stack.Children.Add(rowBtn);
            }

            if (rows.Count > 40)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"…외 {rows.Count - 40}건 더 있습니다. (상세 필터나 더 짧은 기간으로 좁혀 보세요)",
                    FontSize = 11,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(4, 6, 0, 0),
                });
            }
            else if (rows.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "항목을 클릭하면 달력이 해당 날짜로 이동합니다.",
                    FontSize = 11,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(4, 6, 0, 0),
                });
            }

            var card = ModernUi.Card(stack, padding: new Thickness(14, 12, 14, 10), margin: new Thickness(0));
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        void AppendSearchCard(string titleText, IReadOnlyList<SearchHit> hits, string? note = null)
        {
            var stack = new StackPanel();
            var headerRow = new DockPanel { Margin = new Thickness(2, 0, 2, hits.Count == 0 ? 0 : 8) };
            var countPill = ModernUi.Pill(
                hits.Count == 0 ? "0건" : $"{hits.Count}건",
                hits.Count == 0 ? ModernUi.MutedBrush : ModernUi.AccentBrush,
                hits.Count == 0 ? ModernUi.HoverBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(countPill, Dock.Right);
            headerRow.Children.Add(countPill);
            headerRow.Children.Add(new TextBlock
            {
                Text = titleText,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(headerRow);

            if (!string.IsNullOrWhiteSpace(note))
                stack.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = ModernUi.MutedBrush, Margin = new Thickness(2, 0, 2, 6), TextWrapping = TextWrapping.Wrap });

            if (hits.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "일치하는 일정이나 할 일을 찾지 못했습니다.",
                    FontSize = 12.5,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(2, 4, 2, 2),
                });
            }
            else
            {
                stack.Children.Add(new Border { Height = 1, Background = ModernUi.LineBrush, Margin = new Thickness(0, 0, 0, 4) });
            }

            foreach (var h in hits.Take(35))
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                Brush hitDateFg = h.Date.DayOfWeek switch
                {
                    DayOfWeek.Sunday => SundayBrush,
                    DayOfWeek.Saturday => SaturdayBrush,
                    _ => ModernUi.MutedBrush,
                };
                var dateText = new TextBlock
                {
                    Text = h.Date.ToString("yyyy-MM-dd (ddd)", CultureInfo.CurrentCulture),
                    Width = 112,
                    FontSize = 11.5,
                    Foreground = hitDateFg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var pill = ModernUi.Pill(
                    h.Kind == SearchKind.Event ? "일정" : h.Done ? "완료" : "할 일",
                    h.Kind == SearchKind.Event ? ModernUi.AccentBrush : h.Done ? ModernUi.MutedBrush : ModernUi.SuccessBrush,
                    h.Kind == SearchKind.Event ? ModernUi.AccentSoftBrush : h.Done ? ModernUi.HoverBrush : ModernUi.SuccessSoftBrush);
                pill.Margin = new Thickness(0, 0, 8, 0);

                var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                var goBtn = ModernUi.SecondaryBtn("이동", (_, _) => host.GoTo(h.Date));
                goBtn.Padding = new Thickness(8, 2, 8, 2);
                goBtn.FontSize = 11;
                goBtn.Margin = new Thickness(0);
                actionPanel.Children.Add(goBtn);

                if (h.Kind == SearchKind.Item && !h.Deleted)
                {
                    var histBtn = ModernUi.SecondaryBtn("이력", (_, _) =>
                    {
                        var item = host.Items.Get(h.Id);
                        if (item is not null && HistoryWindow.Show(win, host.Items, host.History, item)) host.Changed();
                    });
                    histBtn.Padding = new Thickness(8, 2, 8, 2);
                    histBtn.FontSize = 11;
                    histBtn.Margin = new Thickness(4, 0, 0, 0);
                    actionPanel.Children.Add(histBtn);
                }
                else if (h.Kind == SearchKind.Item && h.Deleted)
                {
                    Button restoreBtn = null!;
                    restoreBtn = ModernUi.SecondaryBtn("복원", (_, _) =>
                    {
                        if (host.Items.Undelete(h.Id)) { host.Changed(); restoreBtn.IsEnabled = false; restoreBtn.Content = "복원됨"; }
                    });
                    restoreBtn.Padding = new Thickness(8, 2, 8, 2);
                    restoreBtn.FontSize = 11;
                    restoreBtn.Margin = new Thickness(4, 0, 0, 0);
                    actionPanel.Children.Add(restoreBtn);
                }

                var label = new TextBlock
                {
                    Text = h.Text.ReplaceLineEndings(" "),
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ModernUi.InkBrush,
                };
                if (h.Done || h.Deleted) { label.TextDecorations = TextDecorations.Strikethrough; label.Opacity = 0.6; }

                var itemContent = new DockPanel();
                DockPanel.SetDock(dateText, Dock.Left);
                DockPanel.SetDock(pill, Dock.Left);
                itemContent.Children.Add(dateText);
                itemContent.Children.Add(pill);
                if (h.TextColor is { Length: > 0 } hex && UiColors.TryBrush(hex) is { } colorDotBrush)
                {
                    var dot = new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(4),
                        Background = colorDotBrush,
                        BorderBrush = ModernUi.InputBorderBrush,
                        BorderThickness = new Thickness(0.6),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    DockPanel.SetDock(dot, Dock.Left);
                    itemContent.Children.Add(dot);
                }
                itemContent.Children.Add(label);

                var hitBtn = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 4, 6, 4),
                    Cursor = Cursors.Hand,
                    Content = itemContent,
                    ToolTip = "클릭하면 달력에서 해당 날짜로 이동합니다",
                    Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(6)),
                };
                hitBtn.Click += (_, _) => host.GoTo(h.Date);

                DockPanel.SetDock(actionPanel, Dock.Right);
                row.Children.Add(actionPanel);
                row.Children.Add(hitBtn);
                stack.Children.Add(row);
            }
            if (hits.Count > 35)
                stack.Children.Add(new TextBlock { Text = $"…외 {hits.Count - 35}건 더 있음 (검색어 또는 상세 필터로 좁혀 보세요)", FontSize = 11, Foreground = ModernUi.MutedBrush, Margin = new Thickness(4, 6, 0, 0) });

            var card = ModernUi.Card(stack, padding: new Thickness(14, 12, 14, 10), margin: new Thickness(0));
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        async Task RunFilterSearchAsync(string keyword)
        {
            var q = BuildSearchQuery(keyword, filters, host.Today(), host.WeekStart());
            if (filters.Meaning.IsChecked == true && host.Semantic is not null)
            {
                cts?.Cancel();
                cts = new CancellationTokenSource();
                var o = await host.Semantic.SearchAsync(q, host.Today(), host.WeekStart(), cts.Token, 100);
                AppendSearchCard(string.IsNullOrWhiteSpace(keyword) ? "상세 필터 의미 검색" : $"'{keyword}' 의미 검색", o.Hits, o.Note);
            }
            else
            {
                var hits = host.Search.Run(q, 200);
                AppendSearchCard(string.IsNullOrWhiteSpace(keyword) ? "상세 필터 검색" : $"'{keyword}' 검색 결과", hits);
            }
        }

        filterRunBtn.Click += async (_, _) => await RunFilterSearchAsync(box.Text.Trim());

        async void SendPrompt(string? forcedPrompt = null)
        {
            string typed = (forcedPrompt ?? box.Text).Trim();
            if (typed.Length == 0 || busy) return;
            if (promptHistory.Count == 0 || promptHistory[^1] != typed)
                promptHistory.Add(typed);
            historyIdx = -1;
            draftBeforeHistory = "";
            bool readFromImage = forcedPrompt is null && fromImage;
            if (forcedPrompt is null) { box.Text = ""; fromImage = false; }
            AppendUserBubble(typed);

            var today = host.Today();
            var weekStart = host.WeekStart();

            // 0) Pasted text of several lines, or text read from a picture: find every dated sentence and ask before adding
            if (host.Extraction is not null && (readFromImage || IsPastedText(typed)))
            {
                AppendExtractionCard(host.Extraction.Analyze(typed, today, weekStart), readFromImage);
                return;
            }

            // 1) Fast deterministic period / free-time query
            if (KoreanQueryParser.Parse(typed, today, weekStart) is { } queryReq)
            {
                var rows = host.Query.Run(queryReq);
                AppendQueryCard(queryReq, rows);
                return;
            }

            // 2) Explicit search cue vs rule-based event/item creation
            bool looksLikeSearch = LooksLikeSearchPrompt(typed);
            if (!looksLikeSearch)
            {
                var ruleOutcome = host.Service.ParseRules(typed, today, weekStart);
                if (ruleOutcome.Proposal is { } ruleProp)
                {
                    AppendProposalCard(ruleProp, typed);
                    return;
                }
            }

            // 3) Keyword / semantic search against local calendar
            var searchTerm = StripSearchCue(typed);
            if (looksLikeSearch && string.IsNullOrWhiteSpace(searchTerm))
            {
                if (HasActiveFilter())
                {
                    await RunFilterSearchAsync("");
                    return;
                }
                AppendAssistantText("검색할 키워드를 함께 입력해 주세요.\n예) \"주간보고 찾아줘\", \"치과 검색\"\n또는 상단의 '상세 필터'를 열어 조건으로 바로 검색할 수 있습니다.");
                return;
            }

            var searchQ = BuildSearchQuery(searchTerm, filters, today, weekStart);
            string searchTitle = string.IsNullOrWhiteSpace(searchTerm) ? "상세 필터 검색" : $"'{searchTerm}'";
            if (filters.Meaning.IsChecked == true && host.Semantic is not null)
            {
                cts?.Cancel();
                cts = new CancellationTokenSource();
                var sem = await host.Semantic.SearchAsync(searchQ, today, weekStart, cts.Token, 50);
                if (sem.Hits.Count > 0 || looksLikeSearch)
                {
                    AppendSearchCard($"{searchTitle} 의미 검색", sem.Hits, sem.Note);
                    return;
                }
            }
            else
            {
                var hits = host.Search.Run(searchQ, 100);
                if (hits.Count > 0 || looksLikeSearch)
                {
                    AppendSearchCard($"{searchTitle} 검색 결과", hits);
                    return;
                }
            }

            // 4) AI fallback: parse as event OR answer conversationally with schedule context
            if (!host.Service.AiAvailable)
            {
                AppendAssistantText("일치하는 일정이 없고, 규칙으로 날짜·시간을 읽지 못했습니다.\n설정 → AI 에서 로컬/클라우드 AI 를 켜면 자연어 등록과 대화형 질문을 처리할 수 있습니다.");
                return;
            }

            busy = true;
            sendBtn.IsEnabled = false;
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            SetLiveBadge("AI 처리중", ModernUi.AccentBrush, ModernUi.AccentSoftBrush, "AI 가 생각하고 있습니다… (Esc 로 취소)");

            var cancelInline = ModernUi.ChipBtn("취소", (_, _) => cts?.Cancel());
            cancelInline.Padding = new Thickness(8, 2, 8, 2);
            cancelInline.FontSize = 11;
            cancelInline.Margin = new Thickness(10, 0, 0, 0);
            var thinkingRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            thinkingRow.Children.Add(new TextBlock
            {
                Text = "AI가 일정을 확인하고 답변을 준비 중입니다…",
                FontSize = 12.5,
                Foreground = ModernUi.MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            thinkingRow.Children.Add(cancelInline);
            var thinkingBubble = new Border
            {
                Background = ModernUi.CardBrush,
                BorderBrush = ModernUi.LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3, 12, 12, 12),
                Padding = new Thickness(14, 9, 14, 9),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = thinkingRow,
            };
            var thinkingElement = WrapWithAssistantAvatar(thinkingBubble);
            messages.Children.Add(thinkingElement);
            ScrollBottom();

            try
            {
                var aiOutcome = await host.Service.ParseAsync(typed, today, weekStart, token);
                messages.Children.Remove(thinkingElement);
                if (aiOutcome.Proposal is { } aiProp)
                {
                    AppendProposalCard(aiProp, typed);
                    return;
                }

                if (host.GetAiSettings is not null && host.LlmFactory is not null)
                {
                    var settings = host.GetAiSettings();
                    var llm = host.LlmFactory(settings);
                    var context = BuildScheduleContext(host.Query, today, weekStart);
                    var sys = $"당신은 M-DeskCal 캘린더 비서입니다. 오늘 날짜는 {today:yyyy-MM-dd} ({AiText.Dow[(int)today.DayOfWeek]}) 입니다. " +
                              "아래 사용자의 일정 요약만 근거로 한국어로 간결하고 친절하게 답하세요. 없는 일정은 추측하지 마세요.\n\n" + context;
                    var resp = await llm.ChatAsync(new LlmRequest([LlmMessage.System(sys), LlmMessage.User(typed)], MaxTokens: 450, Temperature: 0.2), token);
                    AppendAssistantText(string.IsNullOrWhiteSpace(resp.Text) ? "답변을 생성하지 못했습니다." : resp.Text.Trim());
                }
                else
                {
                    AppendAssistantText(aiOutcome.Message ?? "요청을 이해하지 못했습니다. 날짜나 키워드를 포함해 다시 입력해 보세요.");
                }
            }
            catch (OperationCanceledException)
            {
                messages.Children.Remove(thinkingElement);
                AppendAssistantText("AI 요청을 취소했습니다.");
            }
            catch (Exception ex)
            {
                messages.Children.Remove(thinkingElement);
                AppendAssistantText("AI 응답 중 오류가 발생했습니다: " + ex.Message, ModernUi.WarnSoftBrush, ModernUi.WarnBrush);
            }
            finally
            {
                messages.Children.Remove(thinkingElement);
                busy = false;
                sendBtn.IsEnabled = true;
                UpdateLivePreview();
            }
        }

        void UpdateLivePreview()
        {
            string raw = box.Text;
            placeholder.Visibility = string.IsNullOrEmpty(raw) ? Visibility.Visible : Visibility.Collapsed;
            clearInputBtn.Visibility = string.IsNullOrEmpty(raw) ? Visibility.Collapsed : Visibility.Visible;

            if (busy) return;
            string typed = raw.Trim();
            if (typed.Length == 0)
            {
                SetLiveBadge("안내", ModernUi.MutedBrush, ModernUi.HoverBrush,
                    "\"이번 주 일정 보여줘\" · \"오늘 빈 시간\" · \"주간보고 찾아줘\" · 공지·캡처 이미지 붙여넣기");
                return;
            }
            if (host.Extraction is not null && (fromImage || IsPastedText(typed)))
            {
                int n = TextExtractor.Extract(typed, host.Today(), host.WeekStart()).Count(c => c.Kind != CandidateKind.Skipped);
                SetLiveBadge("일정 추출", ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush,
                    (fromImage ? "이미지에서 읽은 글 · " : "") + (n == 0 ? "날짜가 있는 문장이 아직 없습니다" : $"날짜가 있는 문장 {n}개") + " — Enter로 등록 확인 카드 만들기");
                return;
            }
            if (KoreanQueryParser.Parse(typed, host.Today(), host.WeekStart()) is { } q)
            {
                SetLiveBadge(q.FreeTime ? "빈 시간 조회" : "일정 조회", ModernUi.AccentBrush, ModernUi.AccentSoftBrush,
                    $"{q.Label} — Enter를 누르면 바로 조회합니다");
                return;
            }
            if (!LooksLikeSearchPrompt(typed) && host.Service.ParseRules(typed, host.Today(), host.WeekStart()).Proposal is { } p)
            {
                var (t, w, _) = AiText.Describe(p);
                SetLiveBadge("일정 등록", ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush,
                    $"{t} · {w} — Enter로 등록 카드 만들기");
                return;
            }
            SetLiveBadge(host.Service.AiAvailable ? "검색 · AI" : "캘린더 검색", ModernUi.AccentBrush, ModernUi.AccentSoftBrush,
                host.Service.AiAvailable
                    ? "Enter를 누르면 캘린더 검색 또는 AI 자연어 처리를 실행합니다"
                    : "Enter를 누르면 캘린더에서 키워드로 검색합니다");
        }

        // Add quick prompt chips
        (string Label, string Prompt, string Icon)[] quickChips =
        [
            ("이번 주 일정 보여줘", "이번 주 일정 보여줘", ModernUi.Icons.GroupTools),
            ("오늘 할 일", "오늘 할 일", ModernUi.Icons.CheckSquare),
            ("빈 시간 찾아줘", "이번 주 빈 시간 찾아줘", ModernUi.Icons.Briefing),
            ("내일 오후 2시 팀 회의 1시간", "내일 오후 2시 팀 회의 1시간", ModernUi.Icons.CalendarPlus),
        ];
        foreach (var (label, prompt, icon) in quickChips)
        {
            var chip = MakePillButton(
                label,
                ModernUi.AccentSoftBrush,
                ChipHoverBrush,
                ModernUi.AccentBrush,
                ModernUi.AccentBorderBrush,
                iconData: icon);
            chip.Click += (_, _) => SendPrompt(prompt);
            chipRow.Children.Add(chip);
        }

        sendBtn.Click += (_, _) => SendPrompt();
        send = () => SendPrompt();
        sendPrompt = p => SendPrompt(p);
        setDraft = t =>
        {
            box.Text = t;
            fromImage = false;
            box.CaretIndex = box.Text.Length;
            box.Focus();
        };
        pasteImage = bmp => _ = LoadImageAsync(() => OcrReader.ReadBitmapAsync(bmp));

        box.TextChanged += (_, _) =>
        {
            if (box.Text.Length == 0) fromImage = false;
            UpdateLivePreview();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;     // Shift+Enter: a new line
                e.Handled = true;
                SendPrompt();
                return;
            }
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && host.Extraction is not null)
            {
                // Ctrl+V with a picture (and no text) on the clipboard reads the picture into the box
                try
                {
                    if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && Clipboard.GetImage() is { } bmp)
                    {
                        e.Handled = true;
                        _ = LoadImageAsync(() => OcrReader.ReadBitmapAsync(bmp));
                    }
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
                return;
            }
            bool multiLine = box.Text.Contains('\n');                       // in a pasted text the arrows move between lines
            if (e.Key == Key.Up && promptHistory.Count > 0 && !multiLine)
            {
                if (historyIdx == -1)
                {
                    draftBeforeHistory = box.Text;
                    historyIdx = promptHistory.Count - 1;
                }
                else if (historyIdx > 0)
                {
                    historyIdx--;
                }
                box.Text = promptHistory[historyIdx];
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down && historyIdx != -1 && !multiLine)
            {
                if (historyIdx < promptHistory.Count - 1)
                {
                    historyIdx++;
                    box.Text = promptHistory[historyIdx];
                }
                else
                {
                    historyIdx = -1;
                    box.Text = draftBeforeHistory;
                }
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
            }
        };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (busy) cts?.Cancel(); else win.Close();
        };
        win.Closed += (_, _) => cts?.Cancel();

        AddWelcome(messages, host.Service.AiAvailable, p => SendPrompt(p));

        var body = new Grid { Margin = new Thickness(18, 12, 18, 12) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(chipRow, 0);
        Grid.SetRow(filterCard, 1);
        Grid.SetRow(feed, 2);
        Grid.SetRow(composerCard, 3);
        body.Children.Add(chipRow);
        body.Children.Add(filterCard);
        body.Children.Add(feed);
        body.Children.Add(composerCard);

        var modePill = ModernUi.Pill(
            host.Service.AiAvailable ? "● AI 비서 연결됨" : "● 규칙·검색 모드",
            host.Service.AiAvailable ? ModernUi.SuccessBrush : ModernUi.MutedBrush,
            host.Service.AiAvailable ? ModernUi.SuccessSoftBrush : ModernUi.HoverBrush);
        modePill.ToolTip = host.Service.AiAvailable
            ? "자연어 일정 해석 및 AI 대화형 비서가 활성화되어 있습니다."
            : "규칙 기반 일정 해석 및 검색이 활성화되어 있습니다. (설정 → AI 에서 AI 모델 연결 가능)";

        var header = ModernUi.Header(ModernUi.Icons.Sparkles, "캘린더 채팅", "자연어로 일정을 찾고, 등록하고, 물어보세요 · 공지·캡처 이미지에서 일정 추출", right: modePill);
        win.Content = ModernUi.PageShell(header, footer, body);
        UpdateUndo();
        win.Loaded += (_, _) => { box.Focus(); _ = host.Service.WarmUpAsync(CancellationToken.None); };
        return win;
    }

    private static void AddWelcome(StackPanel messages, bool aiAvailable, Action<string>? onExampleClick = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "안녕하세요! 무엇을 도와드릴까요?",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ModernUi.InkBrush,
            Margin = new Thickness(0, 0, 0, 8),
        });

        (string Badge, Brush Fg, Brush Bg, string Desc, string Sample)[] guides =
        [
            ("일정 등록", ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush, "\"다음 주 화요일 오후 3시 치과 1시간\"", "다음 주 화요일 오후 3시 치과 1시간"),
            ("일정 조회", ModernUi.AccentBrush, ModernUi.AccentSoftBrush, "\"이번 주 일정 보여줘\", \"오늘 빈 시간\"", "이번 주 일정 보여줘"),
            ("통합 검색", ModernUi.InkBrush, ModernUi.HoverBrush, "\"주간보고\", \"유엔파인 찾아줘\" (상세 필터 지원)", "주간보고 찾아줘"),
            ("일정 추출", ModernUi.WarnBrush, ModernUi.WarnSoftBrush, "공지·메일·대화 글이나 캡처 이미지를 붙여넣고 Enter (이미지: Ctrl+V·끌어다 놓기)",
                "[공지] 11월 정기 회의 안내\n- 일시: 11월 20일(금) 오후 2시\n- 장소: 3층 회의실\n- 제출 서류는 11월 18일까지 제출해 주세요."),
        ];

        foreach (var (badge, fg, bg, desc, sample) in guides)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var pill = ModernUi.Pill(badge, fg, bg);
            pill.MinWidth = 64;
            pill.Margin = new Thickness(0, 0, 8, 0);
            if (pill.Child is TextBlock ptb) ptb.HorizontalAlignment = HorizontalAlignment.Center;
            DockPanel.SetDock(pill, Dock.Left);
            row.Children.Add(pill);

            if (onExampleClick is not null)
            {
                var tryBtn = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 3, 6, 3),
                    Cursor = Cursors.Hand,
                    ToolTip = $"클릭하여 실행:\n{sample}",
                    Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(5)),
                    Content = new TextBlock
                    {
                        Text = desc,
                        FontSize = 12.5,
                        Foreground = ModernUi.InkBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                string captured = sample;
                tryBtn.Click += (_, _) => onExampleClick(captured);
                row.Children.Add(tryBtn);
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 12.5,
                    Foreground = ModernUi.InkBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            stack.Children.Add(row);
        }

        string aiNote = aiAvailable
            ? "✨ AI 비서가 연결되어 있어 자유로운 자연어 질문과 복합 일정 해석도 가능합니다."
            : "💡 현재 규칙·검색 모드로 동작 중입니다. 설정 → AI 에서 모델을 연결하면 대화형 질의도 사용할 수 있습니다.";

        stack.Children.Add(new Border
        {
            Background = ModernUi.CardAltBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock
            {
                Text = aiNote,
                FontSize = 11.5,
                Foreground = ModernUi.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        var bubble = new Border
        {
            Background = ModernUi.CardBrush,
            BorderBrush = ModernUi.LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3, 12, 12, 12),
            Padding = new Thickness(14, 12, 14, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = stack,
        };

        var wrapRow = new Grid { Margin = new Thickness(0, 2, 24, 8) };
        wrapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = ModernUi.AccentSoftBrush,
            BorderBrush = ModernUi.AccentBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = ModernUi.FlatIcon(ModernUi.Icons.Sparkles, ModernUi.AccentBrush, 14, 2.0),
        };
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(bubble, 1);
        wrapRow.Children.Add(avatar);
        wrapRow.Children.Add(bubble);
        messages.Children.Add(wrapRow);
    }

    private static StackPanel LabeledFilter(string label, UIElement control)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ModernUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        sp.Children.Add(control);
        return sp;
    }

    private static Button MakePillButton(string text, Brush bg, Brush hoverBg, Brush fg, Brush border, string? iconData = null)
    {
        var contentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (iconData is not null)
        {
            var icon = ModernUi.FlatIcon(iconData, fg, 12.5, 2.0);
            icon.Margin = new Thickness(0, 0, 5, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            contentPanel.Children.Add(icon);
        }
        contentPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Button
        {
            Content = contentPanel,
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(0, 0, 6, 4),
            Cursor = Cursors.Hand,
            Background = bg,
            Foreground = fg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Template = PillButtonTemplate(hoverBg),
        };
    }

    private static void SetPillButtonVisual(Button btn, string text, Brush fg, Brush bg, Brush border)
    {
        btn.Foreground = fg;
        btn.Background = bg;
        btn.BorderBrush = border;
        if (btn.Content is StackPanel sp)
        {
            if (sp.Children.OfType<Viewbox>().FirstOrDefault()?.Child is System.Windows.Shapes.Path path)
                path.Stroke = fg;
            if (sp.Children.OfType<TextBlock>().FirstOrDefault() is { } tb)
                tb.Text = text;
        }
        else
        {
            btn.Content = text;
        }
    }

    private static ControlTemplate PillButtonTemplate(Brush hoverBg)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
        tpl.Triggers.Add(hover);
        return tpl;
    }

    private static ControlTemplate FlatHoverButtonTemplate(Brush hoverBg, CornerRadius radius)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, radius);
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
        tpl.Triggers.Add(hover);
        return tpl;
    }

    private static SearchQuery BuildSearchQuery(string keyword, FilterState f, DateOnly today, DayOfWeek weekStart)
    {
        int diff = ((int)today.DayOfWeek - (int)weekStart + 7) % 7;
        var (from, to) = f.Range.SelectedIndex switch
        {
            1 => (today, today),
            2 => (today.AddDays(-diff), today.AddDays(6 - diff)),
            3 => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month))),
            4 => (today.AddDays(-30), today),
            5 => (today, today.AddDays(30)),
            _ => ((DateOnly?)null, (DateOnly?)null),
        };
        string? color = f.Color.SelectedIndex switch { 0 => null, 1 => "", _ => (f.Color.SelectedItem as ComboBoxItem)?.Tag as string };
        return new SearchQuery(
            keyword, from, to,
            f.Kind.SelectedIndex switch { 1 => SearchKind.Item, 2 => SearchKind.Event, _ => SearchKind.Any },
            f.Done.SelectedIndex switch { 1 => DoneFilter.Done, 2 => DoneFilter.NotDone, _ => DoneFilter.Any },
            color, f.Deleted.IsChecked == true);
    }

    /// <summary>A notice, an e-mail or a conversation pasted in: more than one non-empty line.</summary>
    private static bool IsPastedText(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)) > 1;

    private static bool LooksLikeSearchPrompt(string text)
    {
        string[] cues = ["찾아줘", "찾아", "검색", "어디", "있어?", "있나", "내역"];
        return cues.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripSearchCue(string text)
    {
        string cleaned = text;
        foreach (var cue in new[] { "찾아줘", "찾아", "검색해줘", "검색", "있어?", "있나요?", "있나" })
            cleaned = cleaned.Replace(cue, "", StringComparison.OrdinalIgnoreCase);
        return cleaned.Trim();
    }

    private static string BuildScheduleContext(QueryService query, DateOnly today, DayOfWeek weekStart)
    {
        var req = KoreanQueryParser.Parse("이번 주 일정 보여줘", today, weekStart)
                  ?? new QueryRequest(today.AddDays(-3), today.AddDays(10), "최근·예정 일정", Events: true, Items: true);
        var rows = query.Run(req).Take(30).ToList();
        if (rows.Count == 0) return "이번 주 등록된 일정이나 할 일이 없습니다.";
        var sb = new StringBuilder("이번 주 일정/할 일 목록:\n");
        foreach (var r in rows)
            sb.AppendLine($"- {r.Date:yyyy-MM-dd} ({AiText.Dow[(int)r.Date.DayOfWeek]}) {(r.IsItem ? (r.Done ? "[완료] " : "[할일] ") : r.Time + " ")}{r.Title}");
        return sb.ToString();
    }
}
