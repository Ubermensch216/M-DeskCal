using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private static readonly SolidColorBrush PurpleBrush = ModernUi.Frozen(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly SolidColorBrush PurpleSoftBrush = ModernUi.Frozen(Color.FromRgb(0xF5, 0xF3, 0xFF));
    private static readonly SolidColorBrush SubtleLineBrush = ModernUi.Frozen(Color.FromRgb(0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush FocusBorderHoverBrush = ModernUi.Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));

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
        ModernUi.ConfigureWindow(win, "캘린더 채팅", 740, 740, topmost: false);
        ModernUi.ApplyFlatComboBoxStyle(win.Resources);
        win.MinWidth = 560;
        win.MinHeight = 520;

        var messages = new StackPanel { Margin = new Thickness(0, 6, 4, 8) };
        var feed = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = messages,
            Padding = new Thickness(18, 4, 16, 4),
        };

        // ---- Collapsible advanced search filter drawer (flat rounded controls) ----
        var range = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var s in new[] { "전체 기간", "오늘", "이번 주", "이번 달", "지난 30일", "앞으로 30일" })
            range.Items.Add(s);
        range.SelectedIndex = 0;

        var kind = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var s in new[] { "할 일 + 일정", "할 일만", "일정만" }) kind.Items.Add(s);
        kind.SelectedIndex = 0;

        var done = new ComboBox { Width = 114, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var s in new[] { "완료 여부 무관", "완료한 것만", "미완료만" }) done.Items.Add(s);
        done.SelectedIndex = 0;

        var color = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 8, 0) };
        color.Items.Add("모든 색상");
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

        var meaning = MakeFilterCheckPill("의미 검색", ModernUi.Icons.Sparkles, host.Semantic is not null,
            host.Semantic is not null ? "표현이 달라도 뜻이 비슷한 일정·할 일을 찾습니다" : "설정 → AI 에서 의미 검색 색인을 만들면 쓸 수 있습니다");
        var deleted = MakeFilterCheckPill("삭제됨 포함", ModernUi.Icons.Trash, true, "삭제된 할 일도 검색 결과에 포함합니다");

        var filterRunBtn = MakeIconActionBtn(ModernUi.Icons.Search, "검색", Brushes.White, ModernUi.AccentBrush, ModernUi.AccentHoverBrush, Brushes.Transparent, (_, _) => { });
        filterRunBtn.Padding = new Thickness(12, 5, 12, 5);
        filterRunBtn.Margin = new Thickness(0, 0, 6, 0);

        var filterResetBtn = MakeIconActionBtn(ModernUi.Icons.RotateCcw, "초기화", ModernUi.InkBrush, ModernUi.CardBrush, ModernUi.HoverBrush, ModernUi.InputBorderBrush, (_, _) => { });
        filterResetBtn.Padding = new Thickness(10, 5, 10, 5);
        filterResetBtn.Margin = new Thickness(0);

        var filterRow1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        filterRow1.Children.Add(IconLabeledFilter(ModernUi.Icons.Calendar, "기간", range));
        filterRow1.Children.Add(IconLabeledFilter(ModernUi.Icons.Layers, "유형", kind));
        filterRow1.Children.Add(IconLabeledFilter(ModernUi.Icons.CheckSquare, "상태", done));
        filterRow1.Children.Add(IconLabeledFilter(ModernUi.Icons.Droplet, "색상", color));

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
        var filterDrawer = new Border
        {
            Background = ModernUi.CardAltBrush,
            BorderBrush = ModernUi.LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 10, 18, 10),
            Visibility = Visibility.Collapsed,
            Child = filterStack,
        };

        var filters = new FilterState(range, kind, done, color, meaning, deleted);

        // ---- Header Action Buttons (Filter, Undo, New Chat) ----
        var filterToggle = MakeHeaderToolBtn(ModernUi.Icons.GroupSettings, "필터", "상세 검색 필터 열기/닫기");
        Button undoBtn = null!;
        void UpdateUndo()
        {
            bool canUndo = host.Batch.Last() is not null;
            undoBtn.IsEnabled = canUndo;
            undoBtn.Opacity = canUndo ? 1.0 : 0.42;
        }
        undoBtn = MakeHeaderToolBtn(ModernUi.Icons.Undo, "되돌리기", "마지막 추가 되돌리기");
        undoBtn.Click += (_, _) => { AiUndo.Run(win, host.Batch, host.Changed); UpdateUndo(); };

        CancellationTokenSource? cts = null;
        bool busy = false;
        var promptHistory = new List<string>();
        int historyIdx = -1;
        string draftBeforeHistory = "";
        bool fromImage = false;

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
            bool open = filterDrawer.Visibility == Visibility.Visible;
            int activeCount = ActiveFilterCount();
            bool active = activeCount > 0;
            string label = active ? $"필터 ({activeCount})" : "필터";
            var fg = (open || active) ? ModernUi.AccentBrush : ModernUi.InkBrush;
            var bg = (open || active) ? ModernUi.AccentSoftBrush : ModernUi.CardBrush;
            var bd = (open || active) ? ModernUi.AccentBorderBrush : ModernUi.LineBrush;
            SetPillButtonVisual(filterToggle, label, fg, bg, bd);
        }

        filterToggle.Click += (_, _) =>
        {
            bool open = filterDrawer.Visibility == Visibility.Visible;
            filterDrawer.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
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

        // ---- Unified Elevated Composer ----
        var box = new TextBox
        {
            FontSize = 13.5,
            Padding = new Thickness(2, 4, 26, 4),
            AcceptsReturn = true,                     // a pasted notice keeps its lines; Enter sends, Shift+Enter breaks the line
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 140,
            MinHeight = 28,
            MaxLength = TextExtractor.MaxChars,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Foreground = ModernUi.InkBrush,
            BorderThickness = new Thickness(0),
            Template = BareTextBoxTemplate(),
        };
        input = box;

        var placeholder = new TextBlock
        {
            Text = "일정 등록·조회·검색, 또는 공지 글·캡처 이미지 붙여넣기…",
            FontSize = 13.5,
            Foreground = ModernUi.MutedBrush,
            Opacity = 0.68,
            Margin = new Thickness(4, 5, 28, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var clearInputBtn = new Button
        {
            Content = ModernUi.FlatIcon(ModernUi.Icons.Close, ModernUi.MutedBrush, 11.5, 2.2),
            Width = 22,
            Height = 22,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "입력 지우기",
            Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(11)),
        };
        clearInputBtn.Click += (_, _) => { box.Text = ""; box.Focus(); };

        var boxContainer = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        boxContainer.Children.Add(box);
        boxContainer.Children.Add(placeholder);
        boxContainer.Children.Add(clearInputBtn);

        // Composer bottom action bar: [Image OCR] [Live intent pill + preview] ...... [Send circle button]
        var imageBtn = new Button
        {
            Content = ModernUi.FlatIcon(ModernUi.Icons.Image, ModernUi.MutedBrush, 15.5, 1.9),
            Width = 30,
            Height = 30,
            Background = Brushes.Transparent,
            BorderBrush = ModernUi.LineBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "이미지에서 일정 읽기 (Ctrl+V 붙여넣기·드래그 지원)",
            Visibility = host.Extraction is null ? Visibility.Collapsed : Visibility.Visible,
            Template = PillButtonTemplate(ModernUi.HoverBrush, new CornerRadius(8)),
        };

        var sendBtn = new Button
        {
            Content = ModernUi.FlatIcon(ModernUi.Icons.ArrowUp, Brushes.White, 15.5, 2.3),
            Width = 32,
            Height = 32,
            Background = ModernUi.AccentBrush,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "보내기 (Enter · 줄바꿈 Shift+Enter)",
            IsDefault = true,
            Template = PillButtonTemplate(ModernUi.AccentHoverBrush, new CornerRadius(16)),
        };

        var liveBadgeIcon = ModernUi.FlatIcon(ModernUi.Icons.Sparkles, ModernUi.MutedBrush, 11, 2.1);
        liveBadgeIcon.Margin = new Thickness(0, 0, 4, 0);
        var liveBadgeText = new TextBlock
        {
            Text = "준비",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ModernUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var liveBadgeInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        liveBadgeInner.Children.Add(liveBadgeIcon);
        liveBadgeInner.Children.Add(liveBadgeText);
        var liveBadge = new Border
        {
            Background = ModernUi.HoverBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = liveBadgeInner,
        };

        var livePreview = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ModernUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "Enter 전송 · Shift+Enter 줄바꿈 · 이미지 Ctrl+V",
        };

        var composerBar = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        DockPanel.SetDock(sendBtn, Dock.Right);
        composerBar.Children.Add(sendBtn);
        if (host.Extraction is not null)
        {
            DockPanel.SetDock(imageBtn, Dock.Left);
            composerBar.Children.Add(imageBtn);
        }
        DockPanel.SetDock(liveBadge, Dock.Left);
        composerBar.Children.Add(liveBadge);
        composerBar.Children.Add(livePreview);

        var composerStack = new StackPanel();
        composerStack.Children.Add(boxContainer);
        composerStack.Children.Add(new Border { Height = 1, Background = SubtleLineBrush, Margin = new Thickness(0, 6, 0, 0) });
        composerStack.Children.Add(composerBar);

        var composerFrame = new Border
        {
            Background = ModernUi.CardBrush,
            BorderBrush = ModernUi.InputBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 9, 10, 8),
            Child = composerStack,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.06, Color = Color.FromRgb(0x0F, 0x17, 0x2A) },
        };
        box.GotKeyboardFocus += (_, _) => { composerFrame.BorderBrush = ModernUi.AccentBrush; composerFrame.BorderThickness = new Thickness(1.5); };
        box.LostKeyboardFocus += (_, _) => { composerFrame.BorderBrush = ModernUi.InputBorderBrush; composerFrame.BorderThickness = new Thickness(1); };

        // Compact 1-line quick action chips right above composer
        var quickRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 7) };

        var bottomDock = new Border
        {
            Background = ModernUi.BgBrush,
            Padding = new Thickness(18, 6, 18, 14),
        };
        var bottomStack = new StackPanel();
        bottomStack.Children.Add(quickRow);
        bottomStack.Children.Add(composerFrame);
        bottomDock.Child = bottomStack;

        void ScrollBottom()
        {
            feed.ScrollToEnd();
            win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => feed.ScrollToEnd());
        }

        void SetLiveBadge(string iconData, string tag, Brush fg, Brush bg, string message)
        {
            if (liveBadgeIcon.Child is System.Windows.Shapes.Path p)
            {
                p.Data = Geometry.Parse(iconData);
                p.Stroke = fg;
            }
            liveBadgeText.Text = tag;
            liveBadgeText.Foreground = fg;
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
                CornerRadius = new CornerRadius(16, 16, 4, 16),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(80, 4, 2, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = tb,
            };
            messages.Children.Add(bubble);
            ScrollBottom();
        }

        UIElement WrapWithAssistantAvatar(UIElement cardOrBubble)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 12, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(9),
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

        void AppendAssistantText(string text, Brush? bg = null, Brush? fg = null, string? iconData = null)
        {
            UIElement content;
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = fg ?? ModernUi.InkBrush,
                FontSize = 12.5,
                LineHeight = 19,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (iconData is not null)
            {
                var dp = new DockPanel();
                var ic = ModernUi.FlatIcon(iconData, fg ?? ModernUi.AccentBrush, 15, 2.0);
                ic.Margin = new Thickness(0, 2, 9, 0);
                ic.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(ic, Dock.Left);
                dp.Children.Add(ic);
                dp.Children.Add(tb);
                content = dp;
            }
            else
            {
                content = tb;
            }
            var bubble = new Border
            {
                Background = bg ?? ModernUi.CardBrush,
                BorderBrush = ModernUi.LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4, 14, 14, 14),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = content,
            };
            messages.Children.Add(WrapWithAssistantAvatar(bubble));
            ScrollBottom();
        }

        void AppendProposalCard(Proposal p, string rawInput)
        {
            var (title, when, more) = AiText.Describe(p);
            var targetDate = AiText.DateOf(p) ?? host.Today();
            bool viaAi = p.Source == ParseSource.Llm;
            var accentStrip = viaAi ? ModernUi.WarnBrush : ModernUi.AccentBrush;

            var cardBody = new StackPanel();

            // Top row: Icon-led source pill on left + flat ThumbsUp / ThumbsDown icon buttons on right
            var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var badge = MakeIconPill(
                viaAi ? ModernUi.Icons.Sparkles : ModernUi.Icons.CalendarPlus,
                viaAi ? "AI 해석 (날짜·시각 확인)" : "규칙 해석",
                viaAi ? ModernUi.WarnBrush : ModernUi.AccentBrush,
                viaAi ? ModernUi.WarnSoftBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(badge, Dock.Left);

            var rightBtn = MakeMiniIconBtn(ModernUi.Icons.ThumbUp, ModernUi.MutedBrush, "정확하게 해석됨");
            var wrongBtn = MakeMiniIconBtn(ModernUi.Icons.ThumbDown, ModernUi.MutedBrush, "잘못 해석됨");
            rightBtn.Margin = new Thickness(0, 0, 4, 0);
            var fbDoneText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            bool fbDone = false;
            void Mark(bool ok)
            {
                if (fbDone) return;
                fbDone = true;
                host.Accuracy.Record(p.Source, ok);
                fbDoneText.Text = ok ? "정확함 기록 ✓" : "오류 기록됨";
                fbDoneText.Foreground = ok ? ModernUi.SuccessBrush : ModernUi.MutedBrush;
                fbDoneText.Visibility = Visibility.Visible;
                rightBtn.Visibility = wrongBtn.Visibility = Visibility.Collapsed;
            }
            rightBtn.Click += (_, _) => Mark(true);
            wrongBtn.Click += (_, _) => Mark(false);

            var fbPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            fbPanel.Children.Add(fbDoneText);
            fbPanel.Children.Add(rightBtn);
            fbPanel.Children.Add(wrongBtn);
            DockPanel.SetDock(fbPanel, Dock.Right);

            topRow.Children.Add(badge);
            topRow.Children.Add(fbPanel);
            cardBody.Children.Add(topRow);

            // Title
            cardBody.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                TextWrapping = TextWrapping.Wrap,
            });

            // Date & time row with flat clock icon
            cardBody.Children.Add(IconMetaRow(ModernUi.Icons.Clock, when, ModernUi.InkBrush, ModernUi.AccentBrush, topMargin: 7));

            // Extra metadata (reminder / repeat) with flat bell icon
            if (more.Length > 0)
                cardBody.Children.Add(IconMetaRow(ModernUi.Icons.Bell, more, ModernUi.MutedBrush, ModernUi.WarnBrush, topMargin: 4));

            var notes = p.Warnings.Where(w => !w.StartsWith("AI 가 해석", StringComparison.Ordinal)).ToList();
            if (notes.Count > 0)
                cardBody.Children.Add(IconMetaRow(ModernUi.Icons.Info, string.Join(" · ", notes), ModernUi.MutedBrush, ModernUi.MutedBrush, topMargin: 4));

            if (p.ConflictList.Count > 0)
            {
                var clashRow = new DockPanel();
                var warnIcon = ModernUi.FlatIcon(ModernUi.Icons.AlertTriangle, ModernUi.DangerBrush, 14, 2.0);
                warnIcon.Margin = new Thickness(0, 1, 7, 0);
                warnIcon.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(warnIcon, Dock.Left);
                clashRow.Children.Add(warnIcon);
                clashRow.Children.Add(new TextBlock
                {
                    Text = string.Join("\n", p.ConflictList),
                    FontSize = 12,
                    Foreground = ModernUi.DangerBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
                cardBody.Children.Add(new Border
                {
                    Background = ModernUi.DangerSoftBrush,
                    BorderBrush = ModernUi.DangerBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = clashRow,
                });
            }

            var statusNote = new TextBlock { FontSize = 11.5, Foreground = ModernUi.SuccessBrush, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

            Button addBtn = null!;
            addBtn = MakeIconActionBtn(ModernUi.Icons.Plus, "캘린더에 추가", Brushes.White, ModernUi.AccentBrush, ModernUi.AccentHoverBrush, Brushes.Transparent, (_, _) =>
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
                addBtn.Opacity = 0.65;
                SetIconActionLabel(addBtn, ModernUi.Icons.Check, "추가됨", Brushes.White);
                statusNote.Foreground = ModernUi.SuccessBrush;
                statusNote.Text = $"{targetDate:yyyy-MM-dd} 날짜에 등록했습니다.";
                statusNote.Visibility = Visibility.Visible;
            });
            addBtn.Margin = new Thickness(0, 0, 8, 0);

            var editBtn = MakeIconActionBtn(ModernUi.Icons.Edit, "편집기", ModernUi.InkBrush, ModernUi.CardBrush, ModernUi.HoverBrush, ModernUi.InputBorderBrush, (_, _) => host.OpenEditor(targetDate));

            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 0) };
            actions.Children.Add(addBtn);
            actions.Children.Add(editBtn);

            cardBody.Children.Add(actions);
            cardBody.Children.Add(statusNote);

            // Card with sleek inset left accent pill
            var cardShell = new Grid();
            cardShell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardShell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                Width = 3.5,
                Background = accentStrip,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(10, 12, 0, 12),
            };
            var contentWrap = new Border { Padding = new Thickness(12, 12, 14, 12), Child = cardBody };
            Grid.SetColumn(bar, 0);
            Grid.SetColumn(contentWrap, 1);
            cardShell.Children.Add(bar);
            cardShell.Children.Add(contentWrap);

            var card = new Border
            {
                Background = ModernUi.CardBrush,
                BorderBrush = ModernUi.AccentBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = cardShell,
            };
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        // ---- pasted text / captured image → a check list of dated sentences, added only on confirmation ----
        void AppendExtractionCard(IReadOnlyList<ExtractedRow> rows, bool readFromImage)
        {
            var found = rows.Where(r => r.Candidate.Kind != CandidateKind.Skipped).ToList();
            var skipped = rows.Where(r => r.Candidate.Kind == CandidateKind.Skipped).ToList();
            var stack = new StackPanel();

            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var iconBadge = MakeSmallIconBadge(
                readFromImage ? ModernUi.Icons.Image : ModernUi.Icons.FileText,
                found.Count == 0 ? ModernUi.MutedBrush : PurpleBrush,
                found.Count == 0 ? ModernUi.HoverBrush : PurpleSoftBrush);
            DockPanel.SetDock(iconBadge, Dock.Left);

            var countPill = ModernUi.Pill(
                found.Count == 0 ? "없음" : $"{found.Count}건",
                found.Count == 0 ? ModernUi.MutedBrush : ModernUi.AccentBrush,
                found.Count == 0 ? ModernUi.HoverBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(countPill, Dock.Right);

            headerRow.Children.Add(iconBadge);
            headerRow.Children.Add(countPill);
            headerRow.Children.Add(new TextBlock
            {
                Text = found.Count == 0 ? "날짜가 있는 문장을 찾지 못했습니다" : "찾은 일정 확인",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(headerRow);

            if (found.Count > 0)
            {
                var infoBg = readFromImage ? ModernUi.WarnSoftBrush : ModernUi.CardAltBrush;
                var infoFg = readFromImage ? ModernUi.WarnBrush : ModernUi.MutedBrush;
                var infoRow = new DockPanel();
                var ic = ModernUi.FlatIcon(readFromImage ? ModernUi.Icons.AlertTriangle : ModernUi.Icons.Info, infoFg, 13, 2.0);
                ic.Margin = new Thickness(0, 1, 6, 0);
                ic.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(ic, Dock.Left);
                infoRow.Children.Add(ic);
                infoRow.Children.Add(new TextBlock
                {
                    Text = readFromImage
                        ? "이미지 OCR 결과입니다. 날짜·시각을 원문과 꼭 대조하세요."
                        : $"체크된 항목만 등록되며 제목을 바로 수정할 수 있습니다 (기준일 {host.Today():yyyy-MM-dd}).",
                    FontSize = 11.5,
                    Foreground = infoFg,
                    TextWrapping = TextWrapping.Wrap,
                });
                stack.Children.Add(new Border
                {
                    Background = infoBg,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = infoRow,
                });
            }

            var views = new List<(CheckBox Check, Func<ExtractedRow> Row, Action<string> Done)>();
            Button addBtn = null!;
            var statusNote = new TextBlock { FontSize = 11.5, Margin = new Thickness(2, 8, 2, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

            void UpdateCount()
            {
                int n = views.Count(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Row().Proposal is not null);
                SetIconActionLabel(addBtn, ModernUi.Icons.Plus, n == 0 ? "선택한 항목 등록" : $"선택한 {n}건 등록", Brushes.White);
                addBtn.IsEnabled = n > 0;
                addBtn.Opacity = n > 0 ? 1.0 : 0.45;
            }

            foreach (var first in found)
            {
                var row = first;
                bool added = false;
                var check = new CheckBox { IsChecked = row.SelectedByDefault, Margin = new Thickness(0, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand };
                bool todo = row.Candidate.Kind == CandidateKind.Todo;
                var kindPill = MakeIconPill(
                    todo ? ModernUi.Icons.CheckSquare : ModernUi.Icons.Calendar,
                    todo ? "할 일" : "일정",
                    todo ? ModernUi.SuccessBrush : ModernUi.AccentBrush,
                    todo ? ModernUi.SuccessSoftBrush : ModernUi.AccentSoftBrush);
                kindPill.VerticalAlignment = VerticalAlignment.Top;
                kindPill.Margin = new Thickness(0, 3, 10, 0);

                var title = new TextBox { Text = row.Candidate.Title, Padding = new Thickness(8, 5, 8, 5), FontWeight = FontWeights.SemiBold };
                var detailText = new TextBlock { Foreground = ModernUi.InkBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var detailRow = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
                var detailIc = ModernUi.FlatIcon(ModernUi.Icons.Clock, ModernUi.MutedBrush, 12, 2.0);
                detailIc.Margin = new Thickness(0, 1, 5, 0);
                detailIc.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(detailIc, Dock.Left);
                detailRow.Children.Add(detailIc);
                detailRow.Children.Add(detailText);

                var clash = new TextBlock { Foreground = ModernUi.WarnBrush, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                var notes = new TextBlock { Foreground = ModernUi.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                var source = new TextBlock { Text = "“" + row.Candidate.Source + "”", Foreground = ModernUi.MutedBrush, FontSize = 11, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

                void Refresh()
                {
                    if (row.Proposal is { } p)
                    {
                        var (_, when, more) = AiText.Describe(p);
                        detailText.Text = when + (more.Length > 0 ? "  ·  " + more : "") + (added ? "  ✓ 등록됨" : "");
                        if (added) detailText.Foreground = ModernUi.SuccessBrush;
                        check.IsEnabled = !added;
                    }
                    else { detailText.Text = "제목이 비어 있습니다."; check.IsChecked = false; check.IsEnabled = false; }
                    var n = row.Candidate.Parse.Event.Notes;
                    notes.Text = string.Join(" · ", n);
                    notes.Visibility = n.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                    var c = row.Conflicts.Select(x => "⚠ " + x).ToList();
                    if (row.Duplicate) c.Insert(0, "이미 달력에 있는 것 같습니다 (같은 제목·시각).");
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
                foreach (var e in new UIElement[] { title, detailRow, clash, notes, source }) body.Children.Add(e);
                var cardRow = ModernUi.CheckCardRow(check, body, badge: kindPill);
                cardRow.Margin = new Thickness(0, 0, 0, 6);
                stack.Children.Add(cardRow);
                Refresh();
            }

            addBtn = MakeIconActionBtn(ModernUi.Icons.Plus, "선택한 항목 등록", Brushes.White, ModernUi.AccentBrush, ModernUi.AccentHoverBrush, Brushes.Transparent, (_, _) =>
            {
                var picked = views.Where(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Row().Proposal is not null).ToList();
                if (picked.Count == 0) return;
                var result = host.Batch.Apply(picked.Select(v => v.Row().Proposal!).ToList());
                host.Changed();
                var failed = result.Failed.Select(f => f.Summary).ToHashSet();
                foreach (var v in picked.Where(v => !failed.Contains(v.Row().Proposal!.Summary))) v.Done("");
                string msg = $"{result.Applied.Count}건을 캘린더에 등록했습니다.";
                if (result.Failed.Count > 0) msg += $"\n실패 {result.Failed.Count}건: " + string.Join(" / ", result.Failed.Select(f => f.Error));
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
                    skippedBox.Children.Add(new TextBlock { Text = $"· {s.Candidate.Source}  →  {s.Candidate.Reason}", Foreground = ModernUi.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
                stack.Children.Add(new Expander
                {
                    Header = $"제외된 문장 ({skipped.Count})",
                    Content = skippedBox,
                    IsExpanded = found.Count == 0,
                    Margin = new Thickness(0, 8, 0, 0),
                    Foreground = ModernUi.MutedBrush,
                    FontSize = 11.5,
                });
            }

            var card = ModernUi.Card(stack, padding: new Thickness(14, 12, 14, 12), margin: new Thickness(0));
            card.CornerRadius = new CornerRadius(12);
            if (found.Count > 0) { card.BorderBrush = ModernUi.AccentBorderBrush; }
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        // ---- a picture instead of text: read with the OCR that comes with Windows (offline; the picture is not kept) ----
        async Task LoadImageAsync(Func<Task<OcrReading>> read)
        {
            if (host.Extraction is null || busy) return;
            busy = true;
            sendBtn.IsEnabled = imageBtn.IsEnabled = false;
            SetLiveBadge(ModernUi.Icons.Image, "이미지 읽는 중", PurpleBrush, PurpleSoftBrush, "이미지에서 글자를 읽고 있습니다…");
            try
            {
                var r = await read();
                if (string.IsNullOrWhiteSpace(r.Text))
                {
                    AppendAssistantText("이미지에서 글자를 찾지 못했습니다. 글자가 크고 또렷한 이미지를 사용해 주세요.", ModernUi.WarnSoftBrush, ModernUi.WarnBrush, ModernUi.Icons.AlertTriangle);
                    return;
                }
                box.Text = string.IsNullOrWhiteSpace(box.Text) ? r.Text : box.Text.TrimEnd() + "\n" + r.Text;
                fromImage = true;
                box.CaretIndex = box.Text.Length;
                box.Focus();
            }
            catch (OcrException ex) { AppendAssistantText(ex.Message, ModernUi.WarnSoftBrush, ModernUi.WarnBrush, ModernUi.Icons.AlertTriangle); }
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

            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, rows.Count == 0 ? 4 : 8) };
            var iconBadge = MakeSmallIconBadge(
                q.FreeTime ? ModernUi.Icons.Briefing : ModernUi.Icons.Calendar,
                q.FreeTime ? ModernUi.SuccessBrush : ModernUi.AccentBrush,
                q.FreeTime ? ModernUi.SuccessSoftBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(iconBadge, Dock.Left);

            var countPill = ModernUi.Pill(
                rows.Count == 0 ? "없음" : $"{rows.Count}건",
                rows.Count == 0 ? ModernUi.MutedBrush : ModernUi.AccentBrush,
                rows.Count == 0 ? ModernUi.HoverBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(countPill, Dock.Right);

            headerRow.Children.Add(iconBadge);
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
                    Margin = new Thickness(2, 2, 2, 2),
                });
            }
            else
            {
                stack.Children.Add(new Border { Height = 1, Background = SubtleLineBrush, Margin = new Thickness(0, 0, 0, 4) });
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
                        Background = SubtleLineBrush,
                        Margin = new Thickness(4, 2, 4, 2),
                    });
                }

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
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
                    kindBadge = MakeIconPill(
                        r.Done ? ModernUi.Icons.Check : ModernUi.Icons.CheckSquare,
                        r.Done ? "완료" : "할 일",
                        r.Done ? ModernUi.MutedBrush : ModernUi.SuccessBrush,
                        r.Done ? ModernUi.HoverBrush : ModernUi.SuccessSoftBrush);
                }
                else if (q.FreeTime)
                {
                    kindBadge = MakeIconPill(ModernUi.Icons.Clock, r.Time, ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush);
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

                var chevron = ModernUi.FlatIcon(ModernUi.Icons.ChevronRight, ModernUi.MutedBrush, 12, 2.0);
                chevron.Opacity = 0.65;
                chevron.Margin = new Thickness(8, 0, 2, 0);
                chevron.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(chevron, 3);

                rowGrid.Children.Add(dateBlock);
                rowGrid.Children.Add(kindBadge);
                rowGrid.Children.Add(titleBlock);
                rowGrid.Children.Add(chevron);

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
                    Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(7)),
                };
                rowBtn.Click += (_, _) => host.GoTo(r.Date);
                stack.Children.Add(rowBtn);
            }

            if (rows.Count > 40)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"…외 {rows.Count - 40}건 더 있습니다. (상세 필터로 기간을 좁혀 보세요)",
                    FontSize = 11,
                    Foreground = ModernUi.MutedBrush,
                    Margin = new Thickness(6, 6, 0, 0),
                });
            }

            var card = ModernUi.Card(stack, padding: new Thickness(14, 12, 14, 10), margin: new Thickness(0));
            card.CornerRadius = new CornerRadius(12);
            messages.Children.Add(WrapWithAssistantAvatar(card));
            ScrollBottom();
        }

        void AppendSearchCard(string titleText, IReadOnlyList<SearchHit> hits, string? note = null)
        {
            var stack = new StackPanel();
            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, hits.Count == 0 ? 4 : 8) };
            var iconBadge = MakeSmallIconBadge(ModernUi.Icons.Search, ModernUi.AccentBrush, ModernUi.AccentSoftBrush);
            DockPanel.SetDock(iconBadge, Dock.Left);

            var countPill = ModernUi.Pill(
                hits.Count == 0 ? "0건" : $"{hits.Count}건",
                hits.Count == 0 ? ModernUi.MutedBrush : ModernUi.AccentBrush,
                hits.Count == 0 ? ModernUi.HoverBrush : ModernUi.AccentSoftBrush);
            DockPanel.SetDock(countPill, Dock.Right);

            headerRow.Children.Add(iconBadge);
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
                    Margin = new Thickness(2, 2, 2, 2),
                });
            }
            else
            {
                stack.Children.Add(new Border { Height = 1, Background = SubtleLineBrush, Margin = new Thickness(0, 0, 0, 4) });
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
                    Width = 108,
                    FontSize = 11.5,
                    Foreground = hitDateFg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var pill = ModernUi.Pill(
                    h.Kind == SearchKind.Event ? "일정" : h.Done ? "완료" : "할 일",
                    h.Kind == SearchKind.Event ? ModernUi.AccentBrush : h.Done ? ModernUi.MutedBrush : ModernUi.SuccessBrush,
                    h.Kind == SearchKind.Event ? ModernUi.AccentSoftBrush : h.Done ? ModernUi.HoverBrush : ModernUi.SuccessSoftBrush);
                pill.Margin = new Thickness(0, 0, 8, 0);

                var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                if (h.Kind == SearchKind.Item && !h.Deleted)
                {
                    var histBtn = MakeMiniIconBtn(ModernUi.Icons.History, ModernUi.MutedBrush, "수정 이력 보기");
                    histBtn.Click += (_, _) =>
                    {
                        var item = host.Items.Get(h.Id);
                        if (item is not null && HistoryWindow.Show(win, host.Items, host.History, item)) host.Changed();
                    };
                    actionPanel.Children.Add(histBtn);
                }
                else if (h.Kind == SearchKind.Item && h.Deleted)
                {
                    Button restoreBtn = null!;
                    restoreBtn = MakeMiniIconBtn(ModernUi.Icons.RotateCcw, ModernUi.SuccessBrush, "삭제된 항목 복원");
                    restoreBtn.Click += (_, _) =>
                    {
                        if (host.Items.Undelete(h.Id))
                        {
                            host.Changed();
                            restoreBtn.IsEnabled = false;
                            restoreBtn.Opacity = 0.4;
                        }
                    };
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
                var chevron = ModernUi.FlatIcon(ModernUi.Icons.ChevronRight, ModernUi.MutedBrush, 12, 2.0);
                chevron.Opacity = 0.55;
                chevron.Margin = new Thickness(6, 0, 0, 0);
                chevron.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(chevron, Dock.Right);
                DockPanel.SetDock(dateText, Dock.Left);
                DockPanel.SetDock(pill, Dock.Left);
                itemContent.Children.Add(chevron);
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
                    Padding = new Thickness(6, 5, 6, 5),
                    Cursor = Cursors.Hand,
                    Content = itemContent,
                    ToolTip = $"{h.Date:yyyy-MM-dd} 날짜로 달력 이동",
                    Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(7)),
                };
                hitBtn.Click += (_, _) => host.GoTo(h.Date);

                if (actionPanel.Children.Count > 0)
                {
                    DockPanel.SetDock(actionPanel, Dock.Right);
                    row.Children.Add(actionPanel);
                }
                row.Children.Add(hitBtn);
                stack.Children.Add(row);
            }
            if (hits.Count > 35)
                stack.Children.Add(new TextBlock { Text = $"…외 {hits.Count - 35}건 더 있음 (검색어 또는 필터로 좁혀 보세요)", FontSize = 11, Foreground = ModernUi.MutedBrush, Margin = new Thickness(6, 6, 0, 0) });

            var card = ModernUi.Card(stack, padding: new Thickness(14, 12, 14, 10), margin: new Thickness(0));
            card.CornerRadius = new CornerRadius(12);
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
                AppendAssistantText("검색할 키워드를 함께 입력해 주세요.\n예) \"주간보고 찾아줘\", \"치과 검색\" 또는 상단 필터를 사용해 보세요.", iconData: ModernUi.Icons.Search);
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
                AppendAssistantText("일치하는 일정이 없고, 규칙으로 날짜·시간을 읽지 못했습니다.\n설정 → AI 에서 로컬/클라우드 AI 를 켜면 자연어 등록과 대화형 질문을 처리할 수 있습니다.", iconData: ModernUi.Icons.Info);
                return;
            }

            busy = true;
            sendBtn.IsEnabled = false;
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            SetLiveBadge(ModernUi.Icons.Sparkles, "AI 처리중", ModernUi.AccentBrush, ModernUi.AccentSoftBrush, "AI 가 생각하고 있습니다… (Esc 로 취소)");

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
                CornerRadius = new CornerRadius(4, 14, 14, 14),
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
                AppendAssistantText("AI 응답 중 오류가 발생했습니다: " + ex.Message, ModernUi.WarnSoftBrush, ModernUi.WarnBrush, ModernUi.Icons.AlertTriangle);
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
                SetLiveBadge(ModernUi.Icons.Sparkles, "준비", ModernUi.MutedBrush, ModernUi.HoverBrush,
                    "Enter 전송 · Shift+Enter 줄바꿈 · 이미지 Ctrl+V");
                return;
            }
            if (host.Extraction is not null && (fromImage || IsPastedText(typed)))
            {
                int n = TextExtractor.Extract(typed, host.Today(), host.WeekStart()).Count(c => c.Kind != CandidateKind.Skipped);
                SetLiveBadge(fromImage ? ModernUi.Icons.Image : ModernUi.Icons.FileText, "일정 추출", PurpleBrush, PurpleSoftBrush,
                    (fromImage ? "이미지 OCR · " : "") + (n == 0 ? "날짜 문장 없음" : $"날짜 문장 {n}건 발견") + " — Enter로 확인");
                return;
            }
            if (KoreanQueryParser.Parse(typed, host.Today(), host.WeekStart()) is { } q)
            {
                SetLiveBadge(q.FreeTime ? ModernUi.Icons.Briefing : ModernUi.Icons.Calendar,
                    q.FreeTime ? "빈 시간" : "일정 조회", ModernUi.AccentBrush, ModernUi.AccentSoftBrush,
                    $"{q.Label} — Enter로 바로 조회");
                return;
            }
            if (!LooksLikeSearchPrompt(typed) && host.Service.ParseRules(typed, host.Today(), host.WeekStart()).Proposal is { } p)
            {
                var (t, w, _) = AiText.Describe(p);
                SetLiveBadge(ModernUi.Icons.CalendarPlus, "일정 등록", ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush,
                    $"{t} · {w} — Enter로 카드 생성");
                return;
            }
            SetLiveBadge(ModernUi.Icons.Search,
                host.Service.AiAvailable ? "검색 · AI" : "검색", ModernUi.AccentBrush, ModernUi.AccentSoftBrush,
                host.Service.AiAvailable ? "Enter로 캘린더 검색 또는 AI 질문" : "Enter로 캘린더 키워드 검색");
        }

        // Compact 1-line quick prompt chips above composer
        (string Label, string Prompt, string Icon)[] quickChips =
        [
            ("이번 주 일정", "이번 주 일정 보여줘", ModernUi.Icons.Calendar),
            ("오늘 할 일", "오늘 할 일", ModernUi.Icons.CheckSquare),
            ("빈 시간", "이번 주 빈 시간 찾아줘", ModernUi.Icons.Briefing),
            ("빠른 일정 예시", "내일 오후 2시 팀 회의 1시간", ModernUi.Icons.CalendarPlus),
        ];
        foreach (var (label, prompt, icon) in quickChips)
        {
            var chip = MakePillButton(
                label,
                ModernUi.CardBrush,
                ModernUi.AccentSoftBrush,
                ModernUi.InkBrush,
                ModernUi.LineBrush,
                iconData: icon,
                iconBrush: ModernUi.AccentBrush);
            chip.ToolTip = $"클릭하여 실행: \"{prompt}\"";
            chip.Click += (_, _) => SendPrompt(prompt);
            quickRow.Children.Add(chip);
        }

        var clearBtn = MakeHeaderToolBtn(ModernUi.Icons.RotateCcw, "새 대화", "대화 지우기");
        clearBtn.Click += (_, _) =>
        {
            cts?.Cancel();
            messages.Children.Clear();
            AddWelcome(messages, host.Service.AiAvailable, p => SendPrompt(p));
            feed.ScrollToEnd();
            box.Focus();
        };

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

        // ---- Top Accent Strip + Compact Header with Action Tools ----
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerRight.Children.Add(filterToggle);
        headerRight.Children.Add(undoBtn);
        headerRight.Children.Add(clearBtn);

        var modeDot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = host.Service.AiAvailable ? ModernUi.SuccessBrush : ModernUi.MutedBrush,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var modeInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        modeInner.Children.Add(modeDot);
        modeInner.Children.Add(new TextBlock
        {
            Text = host.Service.AiAvailable ? "AI 비서" : "규칙·검색",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = host.Service.AiAvailable ? ModernUi.SuccessBrush : ModernUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var modePill = new Border
        {
            Background = host.Service.AiAvailable ? ModernUi.SuccessSoftBrush : ModernUi.HoverBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2.5, 8, 2.5),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = modeInner,
            ToolTip = host.Service.AiAvailable
                ? "자연어 일정 해석 및 AI 대화형 비서가 활성화되어 있습니다."
                : "규칙 기반 일정 해석 및 검색이 활성화되어 있습니다. (설정 → AI 에서 AI 모델 연결 가능)",
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = "캘린더 채팅",
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ModernUi.InkBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(modePill);

        var headerIcon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = ModernUi.AccentSoftBrush,
            BorderBrush = ModernUi.AccentBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = ModernUi.FlatIcon(ModernUi.Icons.Sparkles, ModernUi.AccentBrush, 17, 2.0),
        };

        var headerDock = new DockPanel();
        DockPanel.SetDock(headerIcon, Dock.Left);
        DockPanel.SetDock(headerRight, Dock.Right);
        headerDock.Children.Add(headerIcon);
        headerDock.Children.Add(headerRight);
        headerDock.Children.Add(titleRow);

        var headerBar = new Border
        {
            Background = ModernUi.CardBrush,
            BorderBrush = ModernUi.LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 11, 16, 11),
            Child = headerDock,
        };

        var topStack = new StackPanel();
        topStack.Children.Add(new Border { Height = 3.5, Background = ModernUi.AccentBrush });
        topStack.Children.Add(headerBar);
        topStack.Children.Add(filterDrawer);

        var shell = new DockPanel();
        DockPanel.SetDock(topStack, Dock.Top);
        DockPanel.SetDock(bottomDock, Dock.Bottom);
        shell.Children.Add(topStack);
        shell.Children.Add(bottomDock);
        shell.Children.Add(feed);

        win.Content = shell;
        UpdateUndo();
        win.Loaded += (_, _) => { box.Focus(); _ = host.Service.WarmUpAsync(CancellationToken.None); };
        return win;
    }

    // ---- Welcome Hero: Visual 2x2 Flat-Icon Interactive Cards ----
    private static void AddWelcome(StackPanel messages, bool aiAvailable, Action<string>? onExampleClick = null)
    {
        var hero = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };

        var greetRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 10) };
        var greetIcon = MakeSmallIconBadge(ModernUi.Icons.Sparkles, ModernUi.AccentBrush, ModernUi.AccentSoftBrush);
        greetRow.Children.Add(greetIcon);
        greetRow.Children.Add(new TextBlock
        {
            Text = "무엇을 도와드릴까요?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ModernUi.InkBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        hero.Children.Add(greetRow);

        (string Icon, Brush IconFg, Brush IconBg, string Title, string Hint, string Sample)[] cards =
        [
            (ModernUi.Icons.CalendarPlus, ModernUi.SuccessBrush, ModernUi.SuccessSoftBrush,
                "일정 등록", "\"다음 주 화요일 3시 치과 1시간\"", "다음 주 화요일 오후 3시 치과 1시간"),
            (ModernUi.Icons.Calendar, ModernUi.AccentBrush, ModernUi.AccentSoftBrush,
                "일정 조회", "\"이번 주 일정 보여줘\"", "이번 주 일정 보여줘"),
            (ModernUi.Icons.Search, ModernUi.InkBrush, ModernUi.HoverBrush,
                "통합 검색", "\"주간보고 찾아줘\" · 상세 필터", "주간보고 찾아줘"),
            (ModernUi.Icons.Image, PurpleBrush, PurpleSoftBrush,
                "공지·이미지 추출", "공지 글·캡처 붙여넣기 (Ctrl+V)",
                "[공지] 11월 정기 회의 안내\n- 일시: 11월 20일(금) 오후 2시\n- 장소: 3층 회의실\n- 제출 서류는 11월 18일까지 제출해 주세요."),
        ];

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var (icon, fg, bg, title, hint, sample) in cards)
        {
            var badge = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(9),
                Background = bg,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = ModernUi.FlatIcon(icon, fg, 16, 2.0),
            };
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
            });
            textStack.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11.5,
                Foreground = ModernUi.MutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });

            var row = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(textStack);

            var cardBtn = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = ModernUi.CardBrush,
                BorderBrush = ModernUi.LineBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(4, 0, 4, 8),
                Cursor = Cursors.Hand,
                ToolTip = $"클릭하여 실행:\n{sample}",
                Template = PillButtonTemplate(ModernUi.CardAltBrush, new CornerRadius(11), ModernUi.AccentBorderBrush),
            };
            if (onExampleClick is not null)
            {
                string captured = sample;
                cardBtn.Click += (_, _) => onExampleClick(captured);
            }
            grid.Children.Add(cardBtn);
        }
        hero.Children.Add(grid);

        if (!aiAvailable)
        {
            var noteRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var ic = ModernUi.FlatIcon(ModernUi.Icons.Info, ModernUi.MutedBrush, 12.5, 2.0);
            ic.Margin = new Thickness(0, 0, 6, 0);
            noteRow.Children.Add(ic);
            noteRow.Children.Add(new TextBlock
            {
                Text = "설정 → AI 에서 모델을 연결하면 자유로운 대화형 질문도 사용할 수 있습니다.",
                FontSize = 11.5,
                Foreground = ModernUi.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            hero.Children.Add(new Border
            {
                Background = ModernUi.CardBrush,
                BorderBrush = ModernUi.LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 0),
                Child = noteRow,
            });
        }

        messages.Children.Add(hero);
    }

    // ---- Small Flat UI Builders ----

    private static Border MakeSmallIconBadge(string iconData, Brush fg, Brush bg) => new()
    {
        Width = 24,
        Height = 24,
        CornerRadius = new CornerRadius(7),
        Background = bg,
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = ModernUi.FlatIcon(iconData, fg, 13, 2.0),
    };

    private static Border MakeIconPill(string iconData, string text, Brush fg, Brush bg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ic = ModernUi.FlatIcon(iconData, fg, 11, 2.1);
        ic.Margin = new Thickness(0, 0, 4, 0);
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2.5, 8, 2.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = sp,
        };
    }

    private static DockPanel IconMetaRow(string iconData, string text, Brush textFg, Brush iconFg, double topMargin = 4)
    {
        var dp = new DockPanel { Margin = new Thickness(0, topMargin, 0, 0) };
        var ic = ModernUi.FlatIcon(iconData, iconFg, 13, 2.0);
        ic.Margin = new Thickness(0, 2, 7, 0);
        ic.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(ic, Dock.Left);
        dp.Children.Add(ic);
        dp.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = textFg,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return dp;
    }

    private static Button MakeMiniIconBtn(string iconData, Brush stroke, string tooltip) => new()
    {
        Content = ModernUi.FlatIcon(iconData, stroke, 13, 2.0),
        Width = 26,
        Height = 26,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand,
        ToolTip = tooltip,
        Template = FlatHoverButtonTemplate(ModernUi.HoverBrush, new CornerRadius(7)),
    };

    private static Button MakeHeaderToolBtn(string iconData, string label, string tooltip)
    {
        var btn = MakePillButton(label, ModernUi.CardBrush, ModernUi.HoverBrush, ModernUi.InkBrush, ModernUi.LineBrush, iconData: iconData);
        btn.Padding = new Thickness(9, 5, 10, 5);
        btn.Margin = new Thickness(6, 0, 0, 0);
        btn.ToolTip = tooltip;
        return btn;
    }

    private static Button MakeIconActionBtn(string iconData, string text, Brush fg, Brush bg, Brush hoverBg, Brush border, RoutedEventHandler click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ic = ModernUi.FlatIcon(iconData, fg, 13, 2.1);
        ic.Margin = new Thickness(0, 0, 5, 0);
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });

        var btn = new Button
        {
            Content = sp,
            Background = bg,
            Foreground = fg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 13, 6),
            Cursor = Cursors.Hand,
            Template = PillButtonTemplate(hoverBg, new CornerRadius(8)),
        };
        btn.Click += click;
        return btn;
    }

    private static void SetIconActionLabel(Button btn, string iconData, string text, Brush fg)
    {
        if (btn.Content is StackPanel sp)
        {
            if (sp.Children.OfType<Viewbox>().FirstOrDefault()?.Child is System.Windows.Shapes.Path p)
            {
                p.Data = Geometry.Parse(iconData);
                p.Stroke = fg;
            }
            if (sp.Children.OfType<TextBlock>().FirstOrDefault() is { } tb)
                tb.Text = text;
        }
    }

    private static CheckBox MakeFilterCheckPill(string text, string iconData, bool enabled, string tooltip)
    {
        var icon = ModernUi.FlatIcon(iconData, ModernUi.MutedBrush, 12, 2.0);
        icon.Margin = new Thickness(0, 0, 5, 0);
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(icon);
        sp.Children.Add(label);

        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, ModernUi.CardBrush);
        bd.SetValue(Border.BorderBrushProperty, ModernUi.InputBorderBrush);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        bd.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(CheckBox)) { VisualTree = bd };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ModernUi.HoverBrush, "bd"));
        var chk = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        chk.Setters.Add(new Setter(Border.BackgroundProperty, ModernUi.AccentSoftBrush, "bd"));
        chk.Setters.Add(new Setter(Border.BorderBrushProperty, ModernUi.AccentBrush, "bd"));
        chk.Setters.Add(new Setter(Control.ForegroundProperty, ModernUi.AccentBrush));
        var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "bd"));
        tpl.Triggers.Add(hover);
        tpl.Triggers.Add(chk);
        tpl.Triggers.Add(dis);

        var cb = new CheckBox
        {
            Content = sp,
            Foreground = ModernUi.MutedBrush,
            IsEnabled = enabled,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Template = tpl,
        };
        cb.Checked += (_, _) => { if (icon.Child is System.Windows.Shapes.Path p) p.Stroke = ModernUi.AccentBrush; };
        cb.Unchecked += (_, _) => { if (icon.Child is System.Windows.Shapes.Path p) p.Stroke = ModernUi.MutedBrush; };
        return cb;
    }

    private static StackPanel IconLabeledFilter(string iconData, string label, UIElement control)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) };
        var ic = ModernUi.FlatIcon(iconData, ModernUi.MutedBrush, 12.5, 2.0);
        ic.Margin = new Thickness(0, 0, 4, 0);
        ic.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(ic);
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

    private static Button MakePillButton(string text, Brush bg, Brush hoverBg, Brush fg, Brush border, string? iconData = null, Brush? iconBrush = null)
    {
        var contentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (iconData is not null)
        {
            var icon = ModernUi.FlatIcon(iconData, iconBrush ?? fg, 12.5, 2.0);
            icon.Margin = new Thickness(0, 0, 5, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            contentPanel.Children.Add(icon);
        }
        contentPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Button
        {
            Content = contentPanel,
            Padding = new Thickness(10, 4.5, 11, 4.5),
            Margin = new Thickness(0, 0, 6, 2),
            Cursor = Cursors.Hand,
            Background = bg,
            Foreground = fg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Template = PillButtonTemplate(hoverBg, new CornerRadius(14)),
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

    private static ControlTemplate BareTextBoxTemplate()
    {
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = sv };
    }

    private static ControlTemplate PillButtonTemplate(Brush hoverBg, CornerRadius? radius = null, Brush? hoverBorder = null)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(Border.CornerRadiusProperty, radius ?? new CornerRadius(8));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, hoverBorder ?? FocusBorderHoverBrush, "bd"));
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
