using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskCal.Ai;
using DeskCal.Ai.Intent;
using DeskCal.Ai.Llm;
using DeskCal.Core;
using DeskCal.Core.Exchange;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using DatePicker = System.Windows.Controls.DatePicker;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>What the settings page for AI needs from the app.</summary>
internal sealed record AiHost(
    Func<AiSettings> Get, Action<AiSettings> Set, Func<AiSettings, CancellationToken, Task<ConnectionReport>> Test,
    Func<AccuracySummary>? Accuracy = null, Action? ResetAccuracy = null, AiPlusHost? Plus = null);

/// <summary>The meaning-search index and the morning briefing, as the settings page sees them.</summary>
internal sealed record AiPlusHost(
    Func<BriefingSettings> GetBriefing, Action<BriefingSettings> SetBriefing,
    Func<string> IndexStatus, Func<Task<string>> BuildIndex, Action ClearIndex);

/// <summary>What the quick-input window needs from the app.</summary>
internal sealed record QuickInputHost(
    QuickInputService Service, AiBatchService Batch, QueryService Query, AccuracyStore Accuracy,
    Func<DateOnly> Today, Func<DayOfWeek> WeekStart, Action Changed, Action<DateOnly> GoTo, Action<DateOnly> OpenEditor);

/// <summary>What the "extract from text" window needs from the app.</summary>
internal sealed record ExtractHost(
    ExtractionService Service, AiBatchService Batch, Func<DateOnly> Today, Func<DayOfWeek> WeekStart, Action Changed, Action<DateOnly> GoTo);

internal static class AiText
{
    public static readonly string[] Dow = ["일", "월", "화", "수", "목", "금", "토"];
    public static readonly SolidColorBrush Warn = new(Color.FromRgb(0xC2, 0x5E, 0x00));
    public static readonly SolidColorBrush Accent = new(Color.FromRgb(0x2F, 0x8C, 0xFF));

    /// <summary>"11월 17일 (화) 14:00 ~ 15:00" from the arguments of a proposal.</summary>
    public static (string Title, string When, string More) Describe(Proposal p)
    {
        var a = JsonNode.Parse(p.ArgsJson)!;
        if (p.Tool == "add_item")
        {
            CountdownCalc.TryParseDate(a["day"]?.GetValue<string>(), out var day);
            return (a["content"]?.GetValue<string>() ?? "", $"{day:yyyy년 M월 d일} ({Dow[(int)day.DayOfWeek]})  ·  할 일", "");
        }
        string title = a["title"]?.GetValue<string>() ?? "";
        EventTime.TryParse(a["start"]?.GetValue<string>(), out var d, out var t);
        string when = $"{d:yyyy년 M월 d일} ({Dow[(int)d.DayOfWeek]})" + (t is { } tt ? $"  {tt:HH\\:mm}" : "  종일");
        if (EventTime.TryParse(a["end"]?.GetValue<string>(), out var ed, out var et) && et is { } e2)
            when += $" ~ {(ed != d ? $"{ed:M월 d일} " : "")}{e2:HH\\:mm}";
        var more = new List<string>();
        if (RecurrenceRule.TryParse(a["rrule"]?.GetValue<string>(), null, out var rule) && rule.IsRepeating) more.Add("반복: " + RepeatText.Describe(rule));
        if (a["reminder_minutes"] is JsonArray ra && ra.Count > 0) more.Add("알림: " + string.Join(", ", ra.Select(m => ReminderText.DescribeOffset(m!.GetValue<int>()))));
        return (title, when, string.Join("   ·   ", more));
    }

    public static DateOnly? DateOf(Proposal p)
    {
        var a = JsonNode.Parse(p.ArgsJson)!;
        string? text = p.Tool == "add_item" ? a["day"]?.GetValue<string>() : a["start"]?.GetValue<string>();
        return EventTime.TryParse(text, out var d, out _) ? d : null;
    }
}

/// <summary>"마지막 AI 추가 되돌리기": ask, then take back everything the last AI batch created.</summary>
internal static class AiUndo
{
    public static void Run(Window? owner, AiBatchService batch, Action changed)
    {
        var last = batch.Last();
        if (last is null) { MessageBox.Show(owner, "되돌릴 AI 추가가 없습니다.", "마지막 AI 추가 되돌리기"); return; }
        string list = string.Join("\n", last.Entries.Take(10).Select(e => "· " + e.Summary)) + (last.Entries.Count > 10 ? $"\n… 외 {last.Entries.Count - 10}건" : "");
        string when = DateTimeOffset.FromUnixTimeMilliseconds(last.At).ToLocalTime().ToString("M월 d일 HH:mm", CultureInfo.InvariantCulture);
        if (MessageBox.Show(owner, $"{when} 에 추가한 {last.Entries.Count}건을 되돌릴까요?\n\n{list}", "마지막 AI 추가 되돌리기", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var r = batch.UndoLast();
        changed();
        MessageBox.Show(owner, r.AlreadyGone == 0 ? $"{r.Reverted}건을 되돌렸습니다." : $"{r.Reverted}건을 되돌렸습니다. ({r.AlreadyGone}건은 이미 지워져 있었습니다.)", "마지막 AI 추가 되돌리기");
    }
}

/// <summary>
/// "Type one sentence, get an event." While typing, the rules read the sentence at once and show a card; Enter adds it. A question
/// ("이번 주 일정 보여줘") is answered with a list read from the calendar. Only when the rules cannot read the sentence (and the user
/// has switched AI on) does Enter ask the model, and what comes back is shown as the same card - marked as the AI's reading - for the
/// user to accept. Nothing is added without Enter on a card, and the last addition can be taken back.
/// </summary>
internal static class QuickInputWindow
{
    private static Window? _open;

    public static void ShowOrActivate(QuickInputHost host)
    {
        if (_open is not null) { _open.Activate(); return; }
        var win = Build(host, out _);
        _open = win;
        win.Closed += (_, _) => _open = null;
        win.Show();
        win.Activate();
    }

    /// <summary>The window, wired but not shown. <paramref name="input"/> is its text box (a dev snapshot types into it).</summary>
    internal static Window Build(QuickInputHost host, out TextBox input)
    {
        var win = new Window();
        ModernUi.ConfigureWindow(win, "빠른 입력", 650, 560);

        input = new TextBox { FontSize = 15.5, Padding = new Thickness(12, 10, 12, 10), MaxLength = KoreanEventParser.MaxChars };
        var box = input;                                                                  // locals below cannot capture an out parameter
        var hint = new TextBlock
        {
            Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "예) 다음 주 화요일 오후 2시 A사 미팅 1시간, 15분 전 알림 · 매주 월수금 아침 7시 운동 · 12월 3일 종일 치과 하루 전 알림 · 이번 주 일정 보여줘",
        };
        var inputCard = ModernUi.Card(new StackPanel
        {
            Children = { ModernUi.FieldLabel("무엇을 언제 하나요? (질문도 됩니다)"), box, hint }
        }, new Thickness(16, 14, 16, 14));

        // the card
        var cardTitle = new TextBlock { FontSize = 16.5, FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush, TextWrapping = TextWrapping.Wrap };
        var cardWhen = new TextBlock { Margin = new Thickness(0, 5, 0, 0), FontSize = 13, Foreground = ModernUi.InkBrush };
        var cardMore = new TextBlock { Margin = new Thickness(0, 3, 0, 0), FontSize = 12, Foreground = ModernUi.MutedBrush, TextWrapping = TextWrapping.Wrap };
        var cardClash = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = ModernUi.WarnBrush, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 12 };
        var cardNotes = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        var badge = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var badgePill = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 2), Child = badge, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };

        // "was this reading right?" - counted on this computer only (no text is kept)
        var feedbackLabel = new TextBlock { Text = "이 해석이 맞나요?", FontSize = 11.5, Foreground = ModernUi.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var right = ModernUi.ChipBtn("맞음", (_, _) => { });
        var wrong = ModernUi.ChipBtn("틀림", (_, _) => { });
        right.Margin = new Thickness(0, 0, 6, 0);
        var feedback = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        feedback.Children.Add(feedbackLabel); feedback.Children.Add(right); feedback.Children.Add(wrong);

        var cardBody = new StackPanel();
        foreach (var e in new UIElement[] { cardTitle, cardWhen, cardMore, cardClash, cardNotes, badgePill, feedback }) cardBody.Children.Add(e);
        var card = new Border
        {
            Child = cardBody, Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10), Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(1.5), BorderBrush = ModernUi.AccentBorderBrush, Background = ModernUi.AccentSoftBrush,
        };

        // the answer to a question
        var answerLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        var answer = new ListBox { Height = 190, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        var status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ModernUi.MutedBrush };

        Proposal? current = null;
        DateOnly? currentDate = null;
        ParseSource currentSource = ParseSource.Rules;
        bool feedbackGiven = false;
        CancellationTokenSource? cts = null;
        bool busy = false;

        var add = ModernUi.PrimaryBtn("추가 (Enter)", (_, _) => Add(), isDefault: true);
        var edit = ModernUi.SecondaryBtn("일정 편집기로 열기", (_, _) => OpenEditor());
        Button undo = null!;
        undo = ModernUi.SecondaryBtn("마지막 추가 되돌리기", (_, _) => { AiUndo.Run(win, host.Batch, host.Changed); UpdateUndo(); });
        var close = ModernUi.SecondaryBtn("닫기 (Esc)", (_, _) => win.Close());
        close.Margin = new Thickness(0);
        add.IsEnabled = false;
        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        leftBtns.Children.Add(add); leftBtns.Children.Add(edit); leftBtns.Children.Add(undo);
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
        rightBtns.Children.Add(close);
        var footer = ModernUi.Footer(leftBtns, rightBtns);

        void UpdateUndo() => undo.IsEnabled = host.Batch.Last() is not null;

        void FillCard(Proposal p)
        {
            var (title, when, more) = AiText.Describe(p);
            currentDate = AiText.DateOf(p);
            cardTitle.Text = title;
            cardWhen.Text = when;
            cardMore.Text = more;
            cardMore.Visibility = more.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            cardClash.Text = p.ConflictList.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", p.ConflictList);
            cardClash.Visibility = p.ConflictList.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var notes = p.Warnings.Where(w => !w.StartsWith("AI 가 해석", StringComparison.Ordinal)).ToList();
            cardNotes.Text = string.Join("\n", notes.Select(n => "· " + n));
            cardNotes.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            bool viaAi = p.Source == ParseSource.Llm;
            badge.Text = viaAi ? "AI 가 해석했습니다 — 날짜와 시각이 맞는지 꼭 확인하세요" : "규칙으로 읽었습니다";
            badge.Foreground = viaAi ? ModernUi.WarnBrush : ModernUi.AccentBrush;
            badgePill.Background = viaAi ? ModernUi.WarnSoftBrush : ModernUi.CardBrush;
            currentSource = p.Source;
            feedbackGiven = false;
            feedbackLabel.Text = "이 해석이 맞나요?";
            right.Visibility = wrong.Visibility = Visibility.Visible;
        }

        void ShowAnswer(QueryRequest q)
        {
            var rows = host.Query.Run(q);
            current = null; currentDate = null;
            add.IsEnabled = false;
            card.Visibility = Visibility.Collapsed;
            answerLabel.Text = $"{q.Label} — {(rows.Count == 0 ? "일정이 없습니다" : $"{rows.Count}건")}";
            answer.Items.Clear();
            foreach (var r in rows)
            {
                var line = $"{r.Date.Month}월 {r.Date.Day}일 ({AiText.Dow[(int)r.Date.DayOfWeek]})   {(r.IsItem ? (r.Done ? "✓ " : "☐ ") : r.Time + "  ")}{r.Title}";
                answer.Items.Add(new ListBoxItem { Content = line, Tag = r.Date, Foreground = r.IsItem && r.Done ? ModernUi.MutedBrush : ModernUi.InkBrush });
            }
            answerLabel.Visibility = Visibility.Visible;
            answer.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            status.Text = rows.Count == 0 ? "" : "줄을 더블클릭하면 달력이 그 날짜로 이동합니다. (읽기만 하며 아무것도 바꾸지 않습니다)";
        }

        void Render(QuickInputOutcome o, string typed)
        {
            answerLabel.Visibility = answer.Visibility = Visibility.Collapsed;
            current = o.Proposal;
            currentDate = o.Result?.Event.Date;
            add.IsEnabled = current is not null;
            edit.IsEnabled = typed.Length > 0;
            if (current is not null) { FillCard(current); card.Visibility = Visibility.Visible; status.Text = ""; return; }
            card.Visibility = Visibility.Collapsed;
            if (typed.Length == 0) { status.Text = ""; return; }
            string tail = host.Service.AiAvailable ? (o.UsedLlm ? "" : "  Enter 를 누르면 AI 가 해석해 봅니다.")
                                                   : "  (설정 > AI 에서 AI 를 켜면 이런 문장도 해석을 시도합니다.)";
            status.Text = (o.Message ?? "") + tail;
        }

        void Preview()
        {
            string typed = box.Text.Trim();
            if (typed.Length > 0 && KoreanQueryParser.Parse(typed, host.Today(), host.WeekStart()) is { } q) { ShowAnswer(q); edit.IsEnabled = false; return; }
            Render(host.Service.ParseRules(typed, host.Today(), host.WeekStart()), typed);
        }

        async void Ask()
        {
            string typed = box.Text.Trim();
            if (typed.Length == 0 || busy) return;
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            busy = true;
            status.Text = host.Service.AiAvailable ? "AI 가 해석하는 중… (Esc 로 취소) 처음에는 모델을 불러오느라 시간이 걸릴 수 있습니다." : "해석 중…";
            try
            {
                var outcome = await host.Service.ParseAsync(typed, host.Today(), host.WeekStart(), token);
                if (!token.IsCancellationRequested && box.Text.Trim() == typed) Render(outcome, typed);
            }
            catch (OperationCanceledException) { status.Text = "취소했습니다."; }
            finally { busy = false; }
        }

        void Add()
        {
            if (current is not { } p) return;
            var result = host.Batch.Apply([p]);
            if (result.Failed.Count > 0) { status.Text = "추가하지 못했습니다: " + result.Failed[0].Error; return; }
            host.Changed();
            if (currentDate is { } d) host.GoTo(d);
            win.Close();
        }

        void OpenEditor()
        {
            var day = currentDate ?? host.Today();
            win.Close();
            host.OpenEditor(day);
        }

        void Mark(bool correct)
        {
            if (feedbackGiven || current is null) return;
            feedbackGiven = true;
            host.Accuracy.Record(currentSource, correct);
            feedbackLabel.Text = correct ? "기록했습니다 (맞음)" : "기록했습니다 (틀림)";
            right.Visibility = wrong.Visibility = Visibility.Collapsed;
        }
        right.Click += (_, _) => Mark(true);
        wrong.Click += (_, _) => Mark(false);
        answer.MouseDoubleClick += (_, _) => { if (answer.SelectedItem is ListBoxItem { Tag: DateOnly d }) host.GoTo(d); };

        box.TextChanged += (_, _) => { if (!busy) Preview(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (busy) return;
            if (current is not null) Add(); else if (answer.Visibility != Visibility.Visible) Ask();
        };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (busy) cts?.Cancel(); else win.Close();
        };
        win.Closed += (_, _) => cts?.Cancel();

        var header = ModernUi.Header(ModernUi.Icons.Bolt, "빠른 입력", "한 문장으로 일정을 추가하거나 달력 일정을 바로 조회합니다");
        var body = new StackPanel();
        foreach (var e in new UIElement[] { inputCard, card, answerLabel, answer, status })
            body.Children.Add(e);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 14, 18, 10), Content = body };

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        win.Content = dock;
        UpdateUndo();
        win.Loaded += (_, _) => { box.Focus(); _ = host.Service.WarmUpAsync(CancellationToken.None); };       // start loading the model while the user types
        return win;
    }
}

/// <summary>
/// "텍스트에서 일정 추출": paste a notice, an e-mail or a conversation and tick what to add. Every candidate shows the sentence it
/// came from, can be renamed, and is marked when it is already on the calendar or clashes with something. The ticked ones are added
/// as one batch (backed up first) that can be taken back in one click. Text from outside is only ever read by the rules.
/// </summary>
internal static class ExtractWindow
{
    private static Window? _open;

    public static void ShowOrActivate(ExtractHost host)
    {
        if (_open is not null) { _open.Activate(); return; }
        var win = Build(host, out _, out _);
        _open = win;
        win.Closed += (_, _) => _open = null;
        win.Show();
        win.Activate();
    }

    private sealed class RowView
    {
        public required ExtractedRow Row;
        public required CheckBox Check;
        public required TextBlock Detail, Notes, Clash;
        public bool Added;
    }

    /// <summary>The window, wired but not shown. A dev snapshot sets <paramref name="input"/> and calls <paramref name="analyze"/>.</summary>
    internal static Window Build(ExtractHost host, out TextBox input, out Action analyze)
    {
        var win = new Window();
        ModernUi.ConfigureWindow(win, "텍스트에서 일정 추출", 780, 720);

        input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 126,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 8, 10, 8),
            MaxLength = TextExtractor.MaxChars, FontSize = 13,
        };
        var box = input;
        var baseDay = new DatePicker { SelectedDate = host.Today().ToDateTime(TimeOnly.MinValue), Width = 136, VerticalAlignment = VerticalAlignment.Center };
        var summary = new TextBlock { Margin = new Thickness(0, 4, 0, 8), FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush };
        var list = new StackPanel();
        var skippedBox = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var skippedHead = new Expander { Header = "제외된 문장", Visibility = Visibility.Collapsed, Content = skippedBox, Margin = new Thickness(0, 8, 0, 0), Foreground = ModernUi.MutedBrush };
        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ModernUi.MutedBrush };
        var views = new List<RowView>();
        Button addBtn = null!;
        addBtn = ModernUi.PrimaryBtn("선택한 건 추가", (_, _) => AddSelected());
        addBtn.IsEnabled = false;

        bool fromImage = false, setByProgram = false;        // the text came out of a picture (so its numbers may have been misread)

        string BaseNote() => baseDay.SelectedDate is { } d ? DateOnly.FromDateTime(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

        void UpdateCount()
        {
            int n = views.Count(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Row.Proposal is not null);
            addBtn.Content = n == 0 ? "선택한 건 추가" : $"선택한 {n}건 추가";
            addBtn.IsEnabled = n > 0;
        }

        void RefreshRow(RowView v)
        {
            var p = v.Row.Proposal;
            if (p is null) { v.Detail.Text = "제목이 비어 있습니다."; v.Check.IsChecked = false; v.Check.IsEnabled = false; }
            else
            {
                var (_, when, more) = AiText.Describe(p);
                v.Detail.Text = when + (more.Length > 0 ? "   ·   " + more : "");
                v.Check.IsEnabled = !v.Added;
            }
            var notes = v.Row.Candidate.Parse.Event.Notes;
            v.Notes.Text = string.Join("\n", notes.Select(n => "· " + n));
            v.Notes.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var clash = v.Row.Conflicts.Select(c => "⚠ " + c).ToList();
            if (v.Row.Duplicate) clash.Insert(0, "이미 달력에 있는 것 같습니다 (같은 제목·같은 시각).");
            v.Clash.Text = string.Join("\n", clash);
            v.Clash.Visibility = clash.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateCount();
        }

        void Analyze()
        {
            views.Clear(); list.Children.Clear(); skippedBox.Children.Clear();
            string text = box.Text;
            if (string.IsNullOrWhiteSpace(text)) { summary.Text = ""; skippedHead.Visibility = Visibility.Collapsed; UpdateCount(); return; }
            var day = baseDay.SelectedDate is { } d ? DateOnly.FromDateTime(d) : host.Today();
            var rows = host.Service.Analyze(text, day, host.WeekStart());

            foreach (var row in rows.Where(r => r.Candidate.Kind != CandidateKind.Skipped))
            {
                var check = new CheckBox { IsChecked = row.SelectedByDefault, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                bool todo = row.Candidate.Kind == CandidateKind.Todo;
                var kindPill = ModernUi.Pill(
                    todo ? "할 일" : "일정",
                    todo ? ModernUi.SuccessBrush : ModernUi.AccentBrush,
                    todo ? ModernUi.SuccessSoftBrush : ModernUi.AccentSoftBrush);
                kindPill.VerticalAlignment = VerticalAlignment.Top;
                kindPill.Margin = new Thickness(0, 3, 10, 0);

                var title = new TextBox { Text = row.Candidate.Title, Padding = new Thickness(8, 5, 8, 5), FontWeight = FontWeights.SemiBold };
                var detail = new TextBlock { Margin = new Thickness(0, 5, 0, 0), Foreground = ModernUi.InkBrush, FontSize = 12.5 };
                var notes = new TextBlock { Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                var clash = new TextBlock { Foreground = ModernUi.WarnBrush, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                var source = new TextBlock { Text = "원문: " + row.Candidate.Source, Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                var ok = new TextBlock
                {
                    Text = fromImage ? "이미지에서 읽은 글자 기준 — 날짜와 시각을 이미지와 꼭 대조하세요 (OCR 은 숫자를 틀리게 읽을 수 있습니다)" : "원문 확인 — 날짜가 이 문장에 적혀 있습니다",
                    Foreground = fromImage ? ModernUi.WarnBrush : ModernUi.SuccessBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                };

                var view = new RowView { Row = row, Check = check, Detail = detail, Notes = notes, Clash = clash };
                views.Add(view);
                title.LostFocus += (_, _) =>
                {
                    string t = title.Text.Trim();
                    if (view.Added || t == view.Row.Candidate.Title) return;
                    view.Row = host.Service.WithTitle(view.Row, t);
                    check.IsChecked = view.Row.SelectedByDefault;
                    RefreshRow(view);
                };
                check.Click += (_, _) => UpdateCount();

                var body = new StackPanel();
                foreach (var e in new UIElement[] { title, detail, clash, notes, source, ok }) body.Children.Add(e);
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(kindPill, 1); Grid.SetColumn(body, 2);
                line.Children.Add(check); line.Children.Add(kindPill); line.Children.Add(body);
                list.Children.Add(ModernUi.Card(line, new Thickness(12, 10, 12, 10), new Thickness(0, 0, 0, 8)));
                RefreshRow(view);
            }

            var skipped = rows.Where(r => r.Candidate.Kind == CandidateKind.Skipped).ToList();
            foreach (var s in skipped)
                skippedBox.Children.Add(new TextBlock { Text = $"· {s.Candidate.Source}   →   {s.Candidate.Reason}", Foreground = ModernUi.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
            skippedHead.Header = $"제외된 문장 ({skipped.Count}) — 날짜가 없거나 읽을 수 없는 문장";
            skippedHead.Visibility = skipped.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            int found = views.Count;
            summary.Text = found == 0 ? "날짜가 있는 문장을 찾지 못했습니다." : $"{found}건을 찾았습니다. 추가할 것만 체크하고 제목은 고칠 수 있습니다.";
            status.Text = found == 0 ? "" : $"상대 표현(내일·다음 주 …)은 기준일 {BaseNote()} 을 기준으로 읽었습니다. 메시지를 받은 날이 다르면 기준일을 바꾸세요.";
            UpdateCount();
        }
        analyze = Analyze;

        void AddSelected()
        {
            var picked = views.Where(v => v.Check.IsChecked == true && v.Check.IsEnabled && v.Row.Proposal is not null).ToList();
            if (picked.Count == 0) return;
            var result = host.Batch.Apply(picked.Select(v => v.Row.Proposal!).ToList());
            host.Changed();
            var failedSummaries = result.Failed.Select(f => f.Summary).ToHashSet();
            foreach (var v in picked.Where(v => !failedSummaries.Contains(v.Row.Proposal!.Summary)))
            {
                v.Added = true; v.Check.IsChecked = false; v.Check.IsEnabled = false;
                v.Detail.Text += "   ✓ 추가했습니다";
            }
            string text = $"{result.Applied.Count}건을 추가했습니다." + (result.BackupPath is not null ? $" (추가 전 데이터베이스를 백업했습니다: {System.IO.Path.GetFileName(result.BackupPath)})" : "");
            if (result.Failed.Count > 0) text += $"\n추가하지 못한 {result.Failed.Count}건: " + string.Join(" / ", result.Failed.Select(f => f.Error));
            status.Text = text + "\n잘못 추가했다면 '마지막 추가 되돌리기'로 한꺼번에 취소할 수 있습니다.";
            if (picked.Select(v => AiText.DateOf(v.Row.Proposal!)).Where(d => d is not null).Min() is { } first) host.GoTo(first);
            UpdateCount();
        }

        var analyzeBtn = ModernUi.PrimaryBtn("분석", (_, _) => Analyze());
        // ---- a picture instead of text: read with the OCR that comes with Windows (offline; the picture is not kept)
        async Task LoadImage(Func<Task<OcrReading>> read)
        {
            status.Text = "이미지에서 글자를 읽는 중…";
            try
            {
                var r = await read();
                if (string.IsNullOrWhiteSpace(r.Text)) { status.Text = "이미지에서 글자를 찾지 못했습니다. 글자가 크고 또렷한 이미지를 쓰세요."; return; }
                setByProgram = true;
                box.Text = string.IsNullOrWhiteSpace(box.Text) ? r.Text : box.Text.TrimEnd() + "\n" + r.Text;
                setByProgram = false;
                fromImage = true;
                Analyze();
                status.Text = $"이미지에서 {r.Lines}줄을 읽었습니다 ({r.Elapsed.TotalSeconds:0.#}초, 이 컴퓨터에서만 처리하며 이미지는 저장하지 않습니다). 읽은 글자는 위 칸에서 고칠 수 있습니다. "
                            + "OCR 은 글자·숫자를 틀리게 읽을 수 있으니 날짜와 시각을 이미지와 꼭 대조하세요.";
            }
            catch (OcrException ex) { status.Text = ex.Message; }
            catch (OperationCanceledException) { }
        }

        var paste = ModernUi.SecondaryBtn("클립보드 (글·이미지)", (_, _) =>
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bmp) { _ = LoadImage(() => OcrReader.ReadBitmapAsync(bmp)); return; }
                if (Clipboard.ContainsText()) { fromImage = false; box.Text = Clipboard.GetText(); Analyze(); }
                else status.Text = "클립보드에 글이나 이미지가 없습니다 (이미지는 Win+Shift+S 로 캡처할 수 있습니다).";
            }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "클립보드를 읽을 수 없습니다. 직접 붙여넣으세요 (Ctrl+V)."; }
        });
        var imageFile = ModernUi.SecondaryBtn("이미지 파일", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "글자를 읽을 이미지", Filter = "그림 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|모든 파일|*.*" };
            if (dlg.ShowDialog(win) == true) _ = LoadImage(() => OcrReader.ReadFileAsync(dlg.FileName));
        });
        box.TextChanged += (_, _) => { if (!setByProgram) fromImage = false; };
        box.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+V with a picture (and no text) on the clipboard reads the picture
            if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
            try
            {
                if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && Clipboard.GetImage() is { } bmp) { e.Handled = true; _ = LoadImage(() => OcrReader.ReadBitmapAsync(bmp)); }
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        win.AllowDrop = true;
        win.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        win.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = LoadImage(() => OcrReader.ReadFileAsync(files[0]));
            e.Handled = true;
        };
        var undo = ModernUi.SecondaryBtn("마지막 추가 되돌리기", (_, _) => AiUndo.Run(win, host.Batch, host.Changed));
        var close = ModernUi.SecondaryBtn("닫기", (_, _) => win.Close());
        close.Margin = new Thickness(0);
        baseDay.SelectedDateChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) Analyze(); };

        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        toolbar.Children.Add(new TextBlock { Text = "기준일", VerticalAlignment = VerticalAlignment.Center, Foreground = ModernUi.MutedBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) });
        toolbar.Children.Add(baseDay);
        toolbar.Children.Add(new TextBlock { Width = 12 });
        toolbar.Children.Add(analyzeBtn); toolbar.Children.Add(paste); toolbar.Children.Add(imageFile);

        var inputCard = ModernUi.Card(new StackPanel
        {
            Children = { ModernUi.FieldLabel("원문 텍스트 또는 캡처 이미지 (드래그·Ctrl+V 지원)"), box, toolbar }
        }, new Thickness(14, 12, 14, 12));

        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        leftBtns.Children.Add(addBtn); leftBtns.Children.Add(undo);
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
        rightBtns.Children.Add(close);
        var footer = ModernUi.Footer(leftBtns, rightBtns);

        var header = ModernUi.Header(ModernUi.Icons.FileText, "텍스트에서 일정 추출", "공지·메일·대화를 붙여넣거나 이미지를 불러오면 날짜가 있는 일정을 제안합니다");
        var body = new StackPanel();
        foreach (var e in new UIElement[] { inputCard, summary, list, skippedHead, status })
            body.Children.Add(e);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 14, 18, 10), Content = body };

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        win.Content = dock;
        win.Loaded += (_, _) => box.Focus();
        return win;
    }
}
