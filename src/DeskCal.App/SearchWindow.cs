using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskCal.Ai;
using DeskCal.Core;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>
/// Search over to-do lines and events: text plus period / kind / done / colour filters, soft-deleted items on request.
/// Non-modal; stays open while the user jumps to results on the calendar.
/// </summary>
internal sealed class SearchWindow : Window
{
    private static SearchWindow? _open;

    private readonly SearchService _search;
    private readonly ItemRepository _items;
    private readonly HistoryRepository _history;
    private readonly Action<DateOnly> _goTo;
    private readonly Action _changed;
    private readonly SemanticSearchService? _semantic;
    private readonly Func<Task>? _syncIndex;
    private readonly Func<DayOfWeek> _weekStart;
    private CancellationTokenSource? _meaningCts;

    private readonly TextBox _text = new() { Padding = new Thickness(5, 4, 5, 4), MinWidth = 200 };
    private readonly ComboBox _range = new(), _kind = new(), _done = new(), _color = new();
    private readonly DatePicker _from = new(), _to = new();
    private readonly CheckBox _meaning = new() { Content = "의미 검색", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = "글자가 달라도 뜻이 비슷한 항목을 찾습니다 (예: 병원 → 치과 검진). 설정 → AI 에서 켭니다." };
    private readonly CheckBox _deleted = new() { Content = "삭제된 할 일 포함", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly ListBox _results = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.Gray };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _ready;

    public static void ShowOrActivate(SearchService search, ItemRepository items, HistoryRepository history, Action<DateOnly> goTo, Action changed,
                                      SemanticSearchService? semantic = null, Func<Task>? syncIndex = null, Func<DayOfWeek>? weekStart = null)
    {
        if (_open is { } w) { w.Show(); w.WindowState = WindowState.Normal; w.Activate(); w._text.Focus(); w._text.SelectAll(); return; }
        _open = new SearchWindow(search, items, history, goTo, changed, semantic, syncIndex, weekStart);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
        _open._text.Focus();
    }

    private SearchWindow(SearchService search, ItemRepository items, HistoryRepository history, Action<DateOnly> goTo, Action changed,
                         SemanticSearchService? semantic = null, Func<Task>? syncIndex = null, Func<DayOfWeek>? weekStart = null)
    {
        _search = search; _items = items; _history = history; _goTo = goTo; _changed = changed;
        _semantic = semantic; _syncIndex = syncIndex; _weekStart = weekStart ?? (() => DayOfWeek.Monday);
        ModernUi.ConfigureWindow(this, "M-DeskCal 검색", 680, 620, topmost: false);

        _text.Padding = new Thickness(10, 7, 10, 7);
        _text.FontSize = 14;
        _status.Foreground = ModernUi.MutedBrush;
        _status.FontSize = 12;
        _status.Margin = new Thickness(0, 0, 0, 8);

        Fill(_range, "전체 기간", "최근 1개월", "최근 3개월", "최근 1년", "기간 지정…");
        Fill(_kind, "할 일 + 일정", "할 일만", "일정만");
        Fill(_done, "완료 여부 무관", "완료만", "미완료만");
        _color.Items.Add("모든 글자색"); _color.Items.Add("기본 색");
        foreach (var (name, hex) in MainWindow.Palette.Where(p => p.Hex is not null)) _color.Items.Add(new ComboBoxItem { Content = name, Tag = hex, Foreground = UiColors.TryBrush(hex), Background = Brushes.Black });
        _color.SelectedIndex = 0;
        _from.SelectedDate = DateTime.Today.AddMonths(-1); _to.SelectedDate = DateTime.Today;

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var go = ModernUi.PrimaryBtn("검색", (_, _) => Run(), isDefault: true);
        go.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(go, Dock.Right);
        top.Children.Add(go); top.Children.Add(_text);

        var filters = new WrapPanel();
        foreach (var c in new[] { _range, _kind, _done, _color })
        {
            c.Margin = new Thickness(0, 0, 8, 4); c.MinWidth = 104; c.Padding = new Thickness(8, 4, 8, 4);
            filters.Children.Add(c);
        }
        _from.Margin = new Thickness(0, 0, 4, 4); _to.Margin = new Thickness(0, 0, 8, 4);
        filters.Children.Add(_from); filters.Children.Add(_to);
        filters.Children.Add(_deleted);
        if (_semantic?.Available == true) filters.Children.Add(_meaning);

        var searchCard = ModernUi.Card(new StackPanel { Children = { top, filters } }, new Thickness(14, 12, 14, 10));

        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        leftBtns.Children.Add(ModernUi.PrimaryBtn("달력에서 보기", (_, _) => GoToSelected()));
        leftBtns.Children.Add(ModernUi.SecondaryBtn("이력 보기", (_, _) => ShowHistory()));
        leftBtns.Children.Add(ModernUi.SecondaryBtn("삭제된 항목 복원", (_, _) => Undelete()));
        var closeBtn = ModernUi.SecondaryBtn("닫기", (_, _) => Close());
        closeBtn.Margin = new Thickness(0);
        var footer = ModernUi.Footer(leftBtns, closeBtn);

        var header = ModernUi.Header(ModernUi.Icons.Search, "일정 및 할 일 검색", "키워드, 기간, 유형, 색상으로 원하는 항목을 빠르게 찾습니다");

        var contentDock = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        DockPanel.SetDock(searchCard, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        contentDock.Children.Add(searchCard);
        contentDock.Children.Add(_status);
        contentDock.Children.Add(_results);

        var rootDock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        rootDock.Children.Add(header);
        rootDock.Children.Add(footer);
        rootDock.Children.Add(contentDock);
        Content = rootDock;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Run(); };
        _text.TextChanged += (_, _) => { if (_ready) { _debounce.Stop(); _debounce.Start(); } };
        foreach (var c in new[] { _range, _kind, _done, _color }) c.SelectionChanged += (_, _) => { SyncDates(); if (_ready) Run(); };
        _from.SelectedDateChanged += (_, _) => { if (_ready) Run(); };
        _to.SelectedDateChanged += (_, _) => { if (_ready) Run(); };
        _deleted.Click += (_, _) => Run();
        _meaning.Click += (_, _) =>
        {
            _debounce.Interval = TimeSpan.FromMilliseconds(_meaning.IsChecked == true ? 600 : 250);       // each meaning search asks the embedding model
            if (_meaning.IsChecked == true && _syncIndex is not null) _ = _syncIndex();                  // bring the index up to date in the background
            Run();
        };
        _results.MouseDoubleClick += (_, _) => GoToSelected();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        SyncDates();
        _ready = true;
        Run();
    }

    /// <summary>Dev aid: build the window without showing it, run a query and render it to a PNG.</summary>
    internal static void Snapshot(SearchService search, ItemRepository items, HistoryRepository history, string query, string path)
    {
        var w = new SearchWindow(search, items, history, _ => { }, () => { });
        w._text.Text = query;
        w.Run();
        DevHarness.RenderToPng((FrameworkElement)w.Content, path, 640, 520, System.Windows.Media.Colors.White);
    }

    private static void Fill(ComboBox box, params string[] entries)
    {
        foreach (var e in entries) box.Items.Add(e);
        box.SelectedIndex = 0;
    }

    private void SyncDates() => _from.Visibility = _to.Visibility = _range.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

    private SearchQuery BuildQuery()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        (DateOnly? from, DateOnly? to) = _range.SelectedIndex switch
        {
            1 => ((DateOnly?)today.AddMonths(-1), (DateOnly?)today),
            2 => (today.AddMonths(-3), today),
            3 => (today.AddYears(-1), today),
            4 => (_from.SelectedDate is { } a ? DateOnly.FromDateTime(a) : null, _to.SelectedDate is { } b ? DateOnly.FromDateTime(b) : null),
            _ => ((DateOnly?)null, (DateOnly?)null),
        };
        string? color = _color.SelectedIndex switch { 0 => null, 1 => "", _ => (_color.SelectedItem as ComboBoxItem)?.Tag as string };
        return new SearchQuery(
            _text.Text, from, to,
            _kind.SelectedIndex switch { 1 => SearchKind.Item, 2 => SearchKind.Event, _ => SearchKind.Any },
            _done.SelectedIndex switch { 1 => DoneFilter.Done, 2 => DoneFilter.NotDone, _ => DoneFilter.Any },
            color, _deleted.IsChecked == true);
    }

    private const int Limit = 500;

    private async void RunMeaning()
    {
        _meaningCts?.Cancel();
        _meaningCts = new CancellationTokenSource();
        var token = _meaningCts.Token;
        var query = BuildQuery();
        _status.Text = "의미로 찾는 중…";
        try
        {
            var o = await _semantic!.SearchAsync(query, DateOnly.FromDateTime(DateTime.Today), _weekStart(), token, 100);
            if (token.IsCancellationRequested) return;
            _results.Items.Clear();
            foreach (var h in o.Hits) _results.Items.Add(Row(h));
            string head = o.Hits.Count == 0 ? "결과가 없습니다." : $"{o.Hits.Count}건 · 더블클릭하면 달력에서 해당 날짜로 이동합니다.";
            _status.Text = head + (o.Period is { } p ? $"  (기간 '{p.Label}' 적용)" : "") + (o.UsedMeaning ? "  · 의미 검색" : "") + (o.Note is { } n ? "\n" + n : "");
        }
        catch (OperationCanceledException) { }
    }

    private void Run()
    {
        if (_meaning.IsChecked == true && _semantic is not null) { RunMeaning(); return; }
        var hits = _search.Run(BuildQuery(), Limit);
        _results.Items.Clear();
        foreach (var h in hits) _results.Items.Add(Row(h));
        _status.Text = hits.Count == 0 ? "결과가 없습니다."
            : hits.Count >= Limit ? $"{Limit}건 이상 — 처음 {Limit}건만 표시합니다. 조건을 좁혀 보세요."
            : $"{hits.Count}건 · 더블클릭하면 달력에서 해당 날짜로 이동합니다.";
    }

    private static UIElement Row(SearchHit h)
    {
        var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Tag = h };
        var date = new TextBlock
        {
            Text = h.Date.ToString("yyyy-MM-dd ddd", CultureInfo.CurrentCulture), Width = 118, Foreground = ModernUi.MutedBrush,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        var kindPill = ModernUi.Pill(
            h.Kind == SearchKind.Event ? "일정" : h.Done ? "완료" : "할 일",
            h.Kind == SearchKind.Event ? ModernUi.AccentBrush : h.Done ? ModernUi.MutedBrush : ModernUi.SuccessBrush,
            h.Kind == SearchKind.Event ? ModernUi.AccentSoftBrush : h.Done ? ModernUi.HoverBrush : ModernUi.SuccessSoftBrush);
        kindPill.Margin = new Thickness(0, 0, 10, 0);

        var text = new TextBlock
        {
            Text = h.Text.ReplaceLineEndings(" "), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Foreground = ModernUi.InkBrush,
        };
        if (h.Done || h.Deleted) text.TextDecorations = TextDecorations.Strikethrough;
        if (h.Done || h.Deleted) text.Opacity = 0.6;
        if (h.TextColor is { Length: > 0 } hex && UiColors.TryBrush(hex) is { } b) text.Foreground = b;

        var tail = new TextBlock { Foreground = h.Deleted ? ModernUi.DangerBrush : ModernUi.MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        tail.Text = h.Deleted ? "삭제됨" : h.Repeating ? "반복" : "";
        DockPanel.SetDock(date, Dock.Left); DockPanel.SetDock(kindPill, Dock.Left); DockPanel.SetDock(tail, Dock.Right);
        line.Children.Add(date); line.Children.Add(kindPill); line.Children.Add(tail); line.Children.Add(text);
        return line;
    }

    private SearchHit? Selected => (_results.SelectedItem as DockPanel)?.Tag as SearchHit;

    private void GoToSelected()
    {
        if (Selected is { } h) _goTo(h.Date);
        else MessageBox.Show(this, "목록에서 결과를 고르세요.", "검색");
    }

    private void ShowHistory()
    {
        if (Selected is not { Kind: SearchKind.Item } h) { MessageBox.Show(this, "이력은 할 일 항목에서만 볼 수 있습니다. 목록에서 할 일을 고르세요.", "이력"); return; }
        var item = _items.Get(h.Id);
        if (item is null) return;
        if (HistoryWindow.Show(this, _items, _history, item)) { _changed(); Run(); }
    }

    private void Undelete()
    {
        if (Selected is not { Kind: SearchKind.Item, Deleted: true } h)
        {
            MessageBox.Show(this, "'삭제된 할 일 포함'을 켜고, 삭제됨으로 표시된 할 일을 고르세요.", "복원");
            return;
        }
        if (_items.Undelete(h.Id)) { _changed(); Run(); }
    }
}
