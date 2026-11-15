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
    Func<AiSettings, ILlmClient>? LlmFactory = null);

/// <summary>
/// "캘린더 채팅": unified natural-language assistant that merges quick event/to-do entry,
/// schedule queries, keyword/semantic search, and AI conversational help in one window.
/// </summary>
internal static class CalendarChatWindow
{
    private static Window? _open;
    private static Action<string>? _openSend;
    internal static Window? CurrentWindow => _open;

    private sealed record FilterState(
        ComboBox Range, ComboBox Kind, ComboBox Done, ComboBox Color,
        CheckBox Meaning, CheckBox Deleted);

    public static void ShowOrActivate(CalendarChatHost host, string? initialPrompt = null)
    {
        if (_open is not null)
        {
            _open.Activate();
            if (!string.IsNullOrWhiteSpace(initialPrompt)) _openSend?.Invoke(initialPrompt);
            return;
        }
        var win = Build(host, out _, out var send, out var sendPrompt);
        _open = win;
        _openSend = sendPrompt;
        win.Closed += (_, _) => { _open = null; _openSend = null; };
        win.Show();
        win.Activate();
        if (!string.IsNullOrWhiteSpace(initialPrompt))
            win.Dispatcher.BeginInvoke(() => sendPrompt(initialPrompt));
    }

    internal static Window Build(CalendarChatHost host, out TextBox input, out Action send)
        => Build(host, out input, out send, out _);

    private static Window Build(CalendarChatHost host, out TextBox input, out Action send, out Action<string> sendPrompt)
    {
        var win = new Window
        {
            Title = "캘린더 채팅",
            Width = 740,
            Height = 720,
            MinWidth = 560,
            MinHeight = 520,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = ModernUi.BgBrush,
            FontFamily = ModernUi.AppFont,
        };

        var messages = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        var feed = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = messages,
        };

        // ---- Collapsible advanced search filter bar (preserves full SearchWindow capabilities) ----
        var range = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var s in new[] { "전체 기간", "오늘", "이번 주", "이번 달", "지난 30일", "앞으로 30일" })
            range.Items.Add(s);
        range.SelectedIndex = 0;

        var kind = new ComboBox { Width = 104, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var s in new[] { "할 일 + 일정", "할 일만", "일정만" }) kind.Items.Add(s);
        kind.SelectedIndex = 0;

        var done = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var s in new[] { "완료 여부 무관", "완료한 것만", "미완료만" }) done.Items.Add(s);
        done.SelectedIndex = 0;

        var color = new ComboBox { Width = 104, Margin = new Thickness(0, 0, 8, 0) };
        color.Items.Add("모든 글자색");
        color.Items.Add("기본색");
        foreach (var (name, hex) in MainWindow.Palette.Where(p => p.Hex is not null))
            color.Items.Add(new ComboBoxItem { Content = name, Tag = hex, Foreground = UiColors.TryBrush(hex), Background = Brushes.Black });
        color.SelectedIndex = 0;

        var meaning = new CheckBox
        {
            Content = "의미 검색",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            IsEnabled = host.Semantic is not null,
            ToolTip = host.Semantic is not null ? "표현이 달라도 뜻이 비슷한 일정·할 일을 찾습니다" : "설정 → AI 에서 의미 검색 색인을 만들면 쓸 수 있습니다",
        };
        var deleted = new CheckBox { Content = "삭제된 할 일 포함", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

        var filterRunBtn = ModernUi.SecondaryBtn("필터로 검색", (_, _) => { });
        filterRunBtn.Padding = new Thickness(10, 4, 10, 4);
        filterRunBtn.FontSize = 12;

        var filterRow1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        filterRow1.Children.Add(range);
        filterRow1.Children.Add(kind);
        filterRow1.Children.Add(done);
        filterRow1.Children.Add(color);

        var filterRow2 = new WrapPanel { Orientation = Orientation.Horizontal };
        filterRow2.Children.Add(meaning);
        filterRow2.Children.Add(deleted);
        filterRow2.Children.Add(filterRunBtn);

        var filterStack = new StackPanel();
        filterStack.Children.Add(filterRow1);
        filterStack.Children.Add(filterRow2);
        var filterCard = ModernUi.Card(filterStack, padding: new Thickness(12, 10, 12, 10), margin: new Thickness(0, 0, 0, 8));
        filterCard.Visibility = Visibility.Collapsed;

        var filters = new FilterState(range, kind, done, color, meaning, deleted);

        // ---- Quick prompt chips + Filter toggle ----
        var chipRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var filterToggle = new Button
        {
            Content = "상세 필터 ▾",
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 6, 4),
            Cursor = Cursors.Hand,
            Background = ModernUi.CardBrush,
            Foreground = ModernUi.MutedBrush,
            BorderBrush = ModernUi.InputBorderBrush,
            BorderThickness = new Thickness(1),
        };
        filterToggle.Click += (_, _) =>
        {
            bool open = filterCard.Visibility == Visibility.Visible;
            filterCard.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            filterToggle.Content = open ? "상세 필터 ▾" : "상세 필터 ▴";
        };
        chipRow.Children.Add(filterToggle);

        // ---- Composer ----
        var box = new TextBox
        {
            FontSize = 13.5,
            Padding = new Thickness(10, 8, 10, 8),
            AcceptsReturn = false,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ModernUi.CardBrush,
            Foreground = ModernUi.InkBrush,
            BorderBrush = ModernUi.InputBorderBrush,
            BorderThickness = new Thickness(1),
        };
        input = box;

        var sendBtn = ModernUi.PrimaryBtn("보내기 (Enter)", (_, _) => { }, isDefault: true);
        sendBtn.Margin = new Thickness(8, 0, 0, 0);

        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 0);
        Grid.SetColumn(sendBtn, 1);
        inputGrid.Children.Add(box);
        inputGrid.Children.Add(sendBtn);

        var livePreview = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ModernUi.MutedBrush,
            Margin = new Thickness(2, 6, 2, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "예) \"내일 오후 2시 팀 회의 1시간\" · \"이번 주 일정 보여줘\" · \"유엔파인\" · \"다음 주 비는 시간 알려줘\"",
        };

        var composerStack = new StackPanel();
        composerStack.Children.Add(inputGrid);
        composerStack.Children.Add(livePreview);
        var composerCard = ModernUi.Card(composerStack, padding: new Thickness(12, 10, 12, 10), margin: new Thickness(0, 8, 0, 0));

        // ---- Footer ----
        Button undo = null!;
        void UpdateUndo() => undo.IsEnabled = host.Batch.Last() is not null;
        undo = ModernUi.SecondaryBtn("마지막 추가 되돌리기", (_, _) => { AiUndo.Run(win, host.Batch, host.Changed); UpdateUndo(); });
        var clearBtn = ModernUi.SecondaryBtn("대화 지우기", (_, _) =>
        {
            messages.Children.Clear();
            AddWelcome(messages, host.Service.AiAvailable);
            feed.ScrollToEnd();
        });
        var closeBtn = ModernUi.SecondaryBtn("닫기 (Esc)", (_, _) => win.Close());
        var footer = ModernUi.ActionFooter([undo, clearBtn], closeBtn);

        CancellationTokenSource? cts = null;
        bool busy = false;

        void ScrollBottom() => win.Dispatcher.BeginInvoke(() => feed.ScrollToEnd());

        void AppendUserBubble(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13,
            };
            var bubble = new Border
            {
                Background = ModernUi.AccentBrush,
                CornerRadius = new CornerRadius(12, 12, 2, 12),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(64, 4, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = tb,
            };
            messages.Children.Add(bubble);
            ScrollBottom();
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
                CornerRadius = new CornerRadius(12, 12, 12, 2),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 4, 48, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = tb,
            };
            messages.Children.Add(bubble);
            ScrollBottom();
        }

        void AppendProposalCard(Proposal p, string rawInput)
        {
            var (title, when, more) = AiText.Describe(p);
            var targetDate = AiText.DateOf(p) ?? host.Today();
            bool viaAi = p.Source == ParseSource.Llm;

            var cardBody = new StackPanel();
            var badge = ModernUi.Pill(
                viaAi ? "AI 해석 (날짜·시각 확인)" : "규칙 해석",
                viaAi ? ModernUi.WarnBrush : ModernUi.AccentBrush,
                viaAi ? ModernUi.WarnSoftBrush : ModernUi.AccentSoftBrush);
            badge.HorizontalAlignment = HorizontalAlignment.Left;
            badge.Margin = new Thickness(0, 0, 0, 6);
            cardBody.Children.Add(badge);

            cardBody.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush, TextWrapping = TextWrapping.Wrap });
            cardBody.Children.Add(new TextBlock { Text = when, FontSize = 12.5, Foreground = ModernUi.InkBrush, Margin = new Thickness(0, 3, 0, 0) });
            if (more.Length > 0)
                cardBody.Children.Add(new TextBlock { Text = more, FontSize = 12, Foreground = ModernUi.MutedBrush, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            if (p.ConflictList.Count > 0)
                cardBody.Children.Add(new TextBlock { Text = "⚠ " + string.Join("\n⚠ ", p.ConflictList), FontSize = 12, Foreground = ModernUi.DangerBrush, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });

            var statusNote = new TextBlock { FontSize = 11.5, Foreground = ModernUi.SuccessBrush, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

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
                statusNote.Text = $"{targetDate:yyyy-MM-dd} 날짜에 등록했습니다.";
                statusNote.Visibility = Visibility.Visible;
            });
            editBtn = ModernUi.SecondaryBtn("편집기로 열기", (_, _) =>
            {
                host.OpenEditor(targetDate);
            });

            var rightBtn = ModernUi.SecondaryBtn("맞음", (_, _) => { });
            var wrongBtn = ModernUi.SecondaryBtn("틀림", (_, _) => { });
            rightBtn.Padding = wrongBtn.Padding = new Thickness(8, 3, 8, 3);
            rightBtn.FontSize = wrongBtn.FontSize = 11.5;
            var fbLabel = new TextBlock { Text = "정확도:", FontSize = 11.5, Foreground = ModernUi.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            bool fbDone = false;
            void Mark(bool ok)
            {
                if (fbDone) return;
                fbDone = true;
                host.Accuracy.Record(p.Source, ok);
                fbLabel.Text = ok ? "기록됨 (맞음)" : "기록됨 (틀림)";
                rightBtn.Visibility = wrongBtn.Visibility = Visibility.Collapsed;
            }
            rightBtn.Click += (_, _) => Mark(true);
            wrongBtn.Click += (_, _) => Mark(false);

            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            addBtn.Margin = new Thickness(0, 0, 6, 4);
            editBtn.Margin = new Thickness(0, 0, 6, 4);
            rightBtn.Margin = new Thickness(0, 0, 4, 4);
            wrongBtn.Margin = new Thickness(0, 0, 0, 4);
            actions.Children.Add(addBtn);
            actions.Children.Add(editBtn);
            actions.Children.Add(fbLabel);
            actions.Children.Add(rightBtn);
            actions.Children.Add(wrongBtn);

            cardBody.Children.Add(actions);
            cardBody.Children.Add(statusNote);

            var card = ModernUi.Card(cardBody, padding: new Thickness(12, 10, 12, 10), margin: new Thickness(0, 4, 36, 8));
            card.BorderBrush = ModernUi.AccentBrush;
            messages.Children.Add(card);
            ScrollBottom();
        }

        void AppendQueryCard(QueryRequest q, IReadOnlyList<QueryRow> rows)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"{q.Label} — {(rows.Count == 0 ? "일정이 없습니다" : $"{rows.Count}건")}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                Margin = new Thickness(0, 0, 0, rows.Count == 0 ? 0 : 6),
            });

            foreach (var r in rows.Take(40))
            {
                var rowBtn = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 4, 6, 4),
                    Cursor = Cursors.Hand,
                    Content = new TextBlock
                    {
                        Text = $"{r.Date:MM-dd} ({AiText.Dow[(int)r.Date.DayOfWeek]})   {(r.IsItem ? (r.Done ? "✓ " : "☐ ") : r.Time + "  ")}{r.Title}",
                        FontSize = 12.5,
                        Foreground = r.IsItem && r.Done ? ModernUi.MutedBrush : ModernUi.InkBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    ToolTip = "클릭하면 달력에서 해당 날짜로 이동합니다",
                };
                rowBtn.Click += (_, _) => host.GoTo(r.Date);
                stack.Children.Add(rowBtn);
            }
            if (rows.Count > 0)
                stack.Children.Add(new TextBlock { Text = "항목을 클릭하면 달력이 그 날짜로 이동합니다.", FontSize = 11, Foreground = ModernUi.MutedBrush, Margin = new Thickness(0, 4, 0, 0) });

            messages.Children.Add(ModernUi.Card(stack, padding: new Thickness(12, 10, 12, 10), margin: new Thickness(0, 4, 24, 8)));
            ScrollBottom();
        }

        void AppendSearchCard(string titleText, IReadOnlyList<SearchHit> hits, string? note = null)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"{titleText} — {(hits.Count == 0 ? "결과가 없습니다" : $"{hits.Count}건")}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush,
                Margin = new Thickness(0, 0, 0, hits.Count == 0 ? 0 : 6),
            });
            if (!string.IsNullOrWhiteSpace(note))
                stack.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = ModernUi.MutedBrush, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });

            foreach (var h in hits.Take(35))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var dateText = new TextBlock
                {
                    Text = h.Date.ToString("yyyy-MM-dd ddd", CultureInfo.CurrentCulture),
                    Width = 108,
                    FontSize = 11.5,
                    Foreground = ModernUi.MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var pill = ModernUi.Pill(
                    h.Kind == SearchKind.Event ? "일정" : h.Done ? "완료" : "할 일",
                    h.Kind == SearchKind.Event ? ModernUi.AccentBrush : h.Done ? ModernUi.MutedBrush : ModernUi.SuccessBrush,
                    h.Kind == SearchKind.Event ? ModernUi.AccentSoftBrush : h.Done ? ModernUi.HoverBrush : ModernUi.SuccessSoftBrush);
                pill.Margin = new Thickness(0, 0, 8, 0);

                var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var goBtn = ModernUi.SecondaryBtn("이동", (_, _) => host.GoTo(h.Date));
                goBtn.Padding = new Thickness(7, 2, 7, 2);
                goBtn.FontSize = 11;
                actionPanel.Children.Add(goBtn);

                if (h.Kind == SearchKind.Item && !h.Deleted)
                {
                    var histBtn = ModernUi.SecondaryBtn("이력", (_, _) =>
                    {
                        var item = host.Items.Get(h.Id);
                        if (item is not null && HistoryWindow.Show(win, host.Items, host.History, item)) host.Changed();
                    });
                    histBtn.Padding = new Thickness(7, 2, 7, 2);
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
                    restoreBtn.Padding = new Thickness(7, 2, 7, 2);
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
                    Foreground = h.TextColor is { Length: > 0 } hex && UiColors.TryBrush(hex) is { } b ? b : ModernUi.InkBrush,
                };
                if (h.Done || h.Deleted) { label.TextDecorations = TextDecorations.Strikethrough; label.Opacity = 0.6; }

                var itemContent = new DockPanel();
                DockPanel.SetDock(dateText, Dock.Left);
                DockPanel.SetDock(pill, Dock.Left);
                itemContent.Children.Add(dateText);
                itemContent.Children.Add(pill);
                itemContent.Children.Add(label);

                var hitBtn = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 3, 6, 3),
                    Cursor = Cursors.Hand,
                    Content = itemContent,
                    ToolTip = "클릭하면 달력에서 해당 날짜로 이동합니다",
                };
                hitBtn.Click += (_, _) => host.GoTo(h.Date);

                DockPanel.SetDock(actionPanel, Dock.Right);
                row.Children.Add(actionPanel);
                row.Children.Add(hitBtn);
                stack.Children.Add(row);
            }
            if (hits.Count > 35)
                stack.Children.Add(new TextBlock { Text = $"…외 {hits.Count - 35}건 더 있음 (검색어 또는 상세 필터로 좁혀 보세요)", FontSize = 11, Foreground = ModernUi.MutedBrush, Margin = new Thickness(0, 4, 0, 0) });

            messages.Children.Add(ModernUi.Card(stack, padding: new Thickness(12, 10, 12, 10), margin: new Thickness(0, 4, 16, 8)));
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
            if (forcedPrompt is null) box.Text = "";
            AppendUserBubble(typed);

            var today = host.Today();
            var weekStart = host.WeekStart();

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
            var searchQ = BuildSearchQuery(searchTerm, filters, today, weekStart);
            if (filters.Meaning.IsChecked == true && host.Semantic is not null)
            {
                cts?.Cancel();
                cts = new CancellationTokenSource();
                var sem = await host.Semantic.SearchAsync(searchQ, today, weekStart, cts.Token, 50);
                if (sem.Hits.Count > 0 || looksLikeSearch)
                {
                    AppendSearchCard($"'{searchTerm}' 의미 검색", sem.Hits, sem.Note);
                    return;
                }
            }
            else
            {
                var hits = host.Search.Run(searchQ, 100);
                if (hits.Count > 0 || looksLikeSearch)
                {
                    AppendSearchCard($"'{searchTerm}' 검색 결과", hits);
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
            livePreview.Text = "AI 가 생각하고 있습니다… (Esc 로 취소)";
            try
            {
                var aiOutcome = await host.Service.ParseAsync(typed, today, weekStart, token);
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
                AppendAssistantText("AI 요청을 취소했습니다.");
            }
            catch (Exception ex)
            {
                AppendAssistantText("AI 응답 중 오류가 발생했습니다: " + ex.Message, ModernUi.WarnSoftBrush, ModernUi.WarnBrush);
            }
            finally
            {
                busy = false;
                sendBtn.IsEnabled = true;
                UpdateLivePreview();
            }
        }

        void UpdateLivePreview()
        {
            if (busy) return;
            string typed = box.Text.Trim();
            if (typed.Length == 0)
            {
                livePreview.Foreground = ModernUi.MutedBrush;
                livePreview.Text = "예) \"내일 오후 2시 팀 회의 1시간\" · \"이번 주 일정 보여줘\" · \"유엔파인\" · \"다음 주 비는 시간 알려줘\"";
                return;
            }
            if (KoreanQueryParser.Parse(typed, host.Today(), host.WeekStart()) is { } q)
            {
                livePreview.Foreground = ModernUi.AccentBrush;
                livePreview.Text = $"🔍 일정 조회 감지: {q.Label} (Enter로 바로 조회)";
                return;
            }
            if (!LooksLikeSearchPrompt(typed) && host.Service.ParseRules(typed, host.Today(), host.WeekStart()).Proposal is { } p)
            {
                var (t, w, _) = AiText.Describe(p);
                livePreview.Foreground = ModernUi.SuccessBrush;
                livePreview.Text = $"⚡ 일정 제안 감지: {t} · {w} (Enter로 카드 만들기)";
                return;
            }
            livePreview.Foreground = ModernUi.MutedBrush;
            livePreview.Text = host.Service.AiAvailable
                ? "💬 Enter를 누르면 캘린더 검색 또는 AI 자연어 처리를 실행합니다."
                : "🔎 Enter를 누르면 캘린더에서 검색합니다. (AI 비서는 설정 → AI 에서 켤 수 있습니다)";
        }

        // Add quick prompt chips
        foreach (var prompt in new[] { "이번 주 일정 보여줘", "오늘 할 일", "빈 시간 찾아줘", "내일 오후 2시 팀 회의 1시간" })
        {
            var chip = new Button
            {
                Content = prompt,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                Background = ModernUi.AccentSoftBrush,
                Foreground = ModernUi.AccentBrush,
                BorderBrush = ModernUi.AccentBorderBrush,
                BorderThickness = new Thickness(1),
            };
            chip.Click += (_, _) => SendPrompt(prompt);
            chipRow.Children.Add(chip);
        }

        sendBtn.Click += (_, _) => SendPrompt();
        send = () => SendPrompt();
        sendPrompt = p => SendPrompt(p);

        box.TextChanged += (_, _) => UpdateLivePreview();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SendPrompt();
        };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (busy) cts?.Cancel(); else win.Close();
        };
        win.Closed += (_, _) => cts?.Cancel();

        AddWelcome(messages, host.Service.AiAvailable);

        var body = new Grid();
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

        var header = ModernUi.Header(ModernUi.Icons.Sparkles, "캘린더 채팅", "자연어로 일정을 찾고, 등록하고, 물어보세요");
        win.Content = ModernUi.PageShell(header, footer, body);
        UpdateUndo();
        win.Loaded += (_, _) => { box.Focus(); _ = host.Service.WarmUpAsync(CancellationToken.None); };
        return win;
    }

    private static void AddWelcome(StackPanel messages, bool aiAvailable)
    {
        string aiNote = aiAvailable
            ? "AI 비서가 연결되어 있어 자유로운 자연어 질문과 복합 일정 해석도 가능합니다."
            : "현재 규칙·검색 모드로 동작 중입니다. 설정 → AI 에서 모델을 연결하면 대화형 질의도 사용할 수 있습니다.";
        var tb = new TextBlock
        {
            Text = "안녕하세요! 무엇을 도와드릴까요?\n" +
                   "• 일정 등록: \"다음 주 화요일 오후 3시 치과 1시간\"\n" +
                   "• 기간·빈 시간 조회: \"이번 주 일정 보여줘\", \"오늘 빈 시간\"\n" +
                   "• 검색: \"주간보고\", \"유엔파인 찾아줘\" (상세 필터로 기간·색상·완료 여부 필터링)\n" +
                   aiNote,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ModernUi.InkBrush,
            FontSize = 12.5,
            LineHeight = 19,
        };
        messages.Children.Add(new Border
        {
            Background = ModernUi.CardBrush,
            BorderBrush = ModernUi.LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 12, 2),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 2, 36, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = tb,
        });
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
