using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using DeskCal.Ai;
using DeskCal.Ai.Llm;
using DeskCal.Core;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Shapes = System.Windows.Shapes;

namespace DeskCal.App;

/// <summary>What the settings window needs from the app. Every change is applied and saved at once (no OK button).</summary>
internal sealed record SettingsHost(
    Func<BoardSettings> Get,
    Action<Func<BoardSettings, BoardSettings>> Update,
    Action ResetDefaults,
    Func<AppPreferences> GetPrefs,
    Action<AppPreferences> SetPrefs,
    Func<bool> GetAutoStart,
    Action<bool> SetAutoStart,
    Func<bool> GetTimetableWidget,
    Action<bool> SetTimetableWidget,
    Action OpenNotes,
    Action OpenCountdowns,
    Action OpenTimetable,
    Action EditHolidays,
    Action ReloadHolidays,
    Action OpenDataFolder,
    Action ShowDiagnostics,
    AiHost? Ai = null);

/// <summary>
/// "캘린더 설정": the full set of options, in seven tabs. The gear's quick menu holds the handful used every day;
/// everything else is here.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private static readonly Color Accent = Color.FromRgb(0x2F, 0x8C, 0xFF);
    private static readonly SolidColorBrush AccentBrush = Frozen(Accent);
    private static readonly SolidColorBrush InkBrush = Frozen(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly SolidColorBrush MutedBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush LineBrush = Frozen(Color.FromRgb(0xE5, 0xE8, 0xEE));
    private static readonly SolidColorBrush GlyphBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

    private readonly SettingsHost _h;
    private readonly List<Action> _sync = [];
    private bool _syncing;
    private readonly List<Border> _tabButtons = [];
    private readonly TextBlock _pageTitle = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = InkBrush, Margin = new Thickness(0, 0, 0, 14) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private readonly (string Title, string Glyph, Func<UIElement> Build)[] _tabs;

    public static void ShowOrActivate(SettingsHost host, int tab = 0)
    {
        if (_open is { } w) { w.ShowTab(tab); w.Show(); w.Activate(); return; }
        _open = new SettingsWindow(host, tab);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    /// <summary>Dev aid: build the window without showing it (for offscreen snapshots).</summary>
    internal static SettingsWindow CreateForSnapshot(SettingsHost host, int tab) => new(host, tab);

    internal FrameworkElement Surface => (FrameworkElement)Content;

    private SettingsWindow(SettingsHost host, int tab)
    {
        _h = host;
        Title = "캘린더 설정"; Width = 800; Height = 680; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; Topmost = true;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13; Foreground = InkBrush;
        Background = Frozen(Color.FromRgb(0xEE, 0xF1, 0xF6));

        _tabs =
        [
            ("캘린더", "", CalendarTab),
            ("셀", "", CellTab),
            ("글꼴 및 색상", "", FontTab),
            ("메모", "", MemoTab),
            ("고급", "", AdvancedTab),
            ("AI", "\uE82F", AiTab),
            ("제품 정보", "", AboutTab),
        ];

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // left: tab list and "restore defaults"
        var side = new DockPanel { Margin = new Thickness(14, 16, 6, 14) };
        var reset = new TextBlock
        {
            Text = "기본 설정으로 사용", Foreground = AccentBrush, Cursor = Cursors.Hand,
            TextDecorations = null, Margin = new Thickness(6, 8, 0, 0),
        };
        reset.MouseLeftButtonUp += (_, _) => ResetAll();
        DockPanel.SetDock(reset, Dock.Bottom);
        side.Children.Add(reset);
        var tabList = new StackPanel();
        for (int i = 0; i < _tabs.Length; i++)
        {
            int index = i;
            var b = new Border
            {
                Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand, Child = TabLabel(_tabs[i].Glyph, _tabs[i].Title),
            };
            b.MouseLeftButtonUp += (_, _) => ShowTab(index);
            _tabButtons.Add(b);
            tabList.Children.Add(b);
        }
        side.Children.Add(tabList);
        root.Children.Add(side);

        // right: title + scrolling page
        var main = new DockPanel { Margin = new Thickness(12, 14, 20, 0) };
        DockPanel.SetDock(_pageTitle, Dock.Top);
        main.Children.Add(_pageTitle);
        main.Children.Add(_scroll);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        Content = root;
        ShowTab(tab);
    }

    // ---- tabs -------------------------------------------------------------------

    private void ShowTab(int index)
    {
        index = Math.Clamp(index, 0, _tabs.Length - 1);
        for (int i = 0; i < _tabButtons.Count; i++)
        {
            bool on = i == index;
            _tabButtons[i].Background = on ? Brushes.White : Frozen(Color.FromArgb(0x99, 0xD9, 0xDE, 0xE7));
            var panel = (StackPanel)_tabButtons[i].Child;
            foreach (var tb in panel.Children.OfType<TextBlock>()) tb.Foreground = on ? AccentBrush : MutedBrush;
        }
        _pageTitle.Text = _tabs[index].Title;
        _sync.Clear();
        _scroll.Content = _tabs[index].Build();
        _scroll.ScrollToTop();
        SyncAll();
    }

    private int CurrentTab() => Math.Max(0, _tabButtons.FindIndex(b => ReferenceEquals(b.Background, Brushes.White)));

    private static UIElement TabLabel(string glyph, string title)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 17, Width = 26, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private void ResetAll()
    {
        if (MessageBox.Show(this, "표시와 관련된 모든 설정을 처음 값으로 되돌릴까요?\n(일정·항목·창 위치는 그대로입니다.)", "기본 설정으로 사용",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _h.ResetDefaults();
        ShowTab(CurrentTab());
    }

    private void SyncAll()
    {
        _syncing = true;
        try { foreach (var a in _sync.ToArray()) a(); }
        finally { _syncing = false; }
    }

    private void Apply(Func<BoardSettings, BoardSettings> change)
    {
        if (_syncing) return;
        _h.Update(change);
        SyncAll();
    }

    private BoardSettings S => _h.Get();

    private UIElement CalendarTab()
    {
        var page = new StackPanel();

        var modes = new (string Label, DisplayMode Value, Func<UIElement> Glyph)[]
        {
            ("고정 월", DisplayMode.FixedMonth, () => Cells(7, 5, 6, 5, 2, i => i < 2 || i >= 32 ? 0 : 1)),
            ("변동 월", DisplayMode.FloatingMonth, () => WithArrows(Cells(7, 4, 6, 5, 2, _ => 1), vertical: true)),
            ("전체 월", DisplayMode.FullMonth, () => Cells(7, 5, 6, 5, 2, i => i < 2 || i >= 32 ? 0.35 : 1)),
            ("고정 단일 행", DisplayMode.SingleLine, () => Cells(12, 1, 3, 28, 2, _ => 1)),
            ("변동 단일 행", DisplayMode.SingleLineFloating, () => WithArrows(Cells(8, 1, 4, 28, 2, _ => 1, highlight: 2), vertical: false)),
        };
        var maxRows = RowCombo("최대 행", "변동 월에서 한 화면에 보이는 주 수", Range(2, 8), () => S.MaxRows, v => Apply(s => s with { MaxRows = v }));
        var past = RowCombo("지난 주 표시", "변동 월에서 이번 주 앞에 보이는 지난 주 수", Range(0, 7), () => S.PastWeeks, v => Apply(s => s with { PastWeeks = v }));
        var days = RowCombo("한 줄 표시 일수", "변동 단일 행에서 보이는 날수", Range(7, 31), () => S.LineDays, v => Apply(s => s with { LineDays = v }));
        Gate(maxRows, () => S.Mode == DisplayMode.FloatingMonth);
        Gate(past, () => S.Mode == DisplayMode.FloatingMonth);
        Gate(days, () => S.Mode == DisplayMode.SingleLineFloating);
        page.Children.Add(Card("표시 방식",
            Pad(ChoiceCards(modes, () => S.Mode, v => Apply(s => s with { Mode = v }))), maxRows, past, days));

        page.Children.Add(Card("표시 효과",
            RowToggle("주 번호", null, () => S.ShowWeekNumbers, v => Apply(s => s with { ShowWeekNumbers = v })),
            RowToggle("그림자", null, () => S.Shadow, v => Apply(s => s with { Shadow = v }))));

        page.Children.Add(Card("날짜에 함께 표시",
            RowToggle("음력", null, () => S.ShowLunar, v => Apply(s => s with { ShowLunar = v })),
            RowToggle("24절기", null, () => S.ShowSolarTerms, v => Apply(s => s with { ShowSolarTerms = v })),
            RowToggle("공휴일", null, () => S.ShowHolidays, v => Apply(s => s with { ShowHolidays = v })),
            RowToggle("기념일·명절", "어버이날, 정월대보름 등 쉬는 날이 아닌 날", () => S.ShowFestivals, v => Apply(s => s with { ShowFestivals = v }))));
        return page;
    }

    private UIElement CellTab()
    {
        var page = new StackPanel();

        var bullets = new (string Label, BulletStyle Value, Func<UIElement> Glyph)[]
        {
            ("없음", BulletStyle.None, () => ListGlyph(null)),
            ("번호", BulletStyle.Number, () => ListGlyph(["1", "2", "3"])),
            ("점", BulletStyle.Dot, () => ListGlyph(["•", "•", "•"])),
        };
        page.Children.Add(Card("항목 표시",
            Pad(ChoiceCards(bullets, () => S.Bullet, v => Apply(s => s with { Bullet = v }))),
            RowCombo("셀 간격", null, Range(0, 12), () => S.CellGap, v => Apply(s => s with { CellGap = v })),
            RowCombo("줄 간격", null, Range(0, 8), () => S.LineGap, v => Apply(s => s with { LineGap = v }))));

        page.Children.Add(Card("항목 동작",
            RowToggle("완료한 항목을 맨 아래로", "체크한 항목이 그날의 마지막 줄로 내려갑니다", () => S.MoveDoneToBottom, v => Apply(s => s with { MoveDoneToBottom = v })),
            RowToggle("완료한 항목 표시", null, () => S.ShowCompleted, v => Apply(s => s with { ShowCompleted = v })),
            RowToggle("알림 표시 (⏰)", "알림이 걸린 항목 옆에 시계 표시", () => S.ShowReminderMark, v => Apply(s => s with { ShowReminderMark = v })),
            RowToggle("완료 효과음", null, () => S.CompleteSound, v => Apply(s => s with { CompleteSound = v })),
            RowToggle("알림음", null, () => S.ReminderSound, v => Apply(s => s with { ReminderSound = v }))));
        return page;
    }

    private UIElement FontTab()
    {
        var page = new StackPanel();

        var families = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        string Current() => S.FontFamily.Split(',')[0].Trim();
        if (!families.Contains(Current())) families.Insert(0, Current());
        var fontRow = RowCombo("글꼴", null, families.Select(f => (f, f)).ToList(), Current,
            name => Apply(s => s with { FontFamily = name == "Malgun Gothic" ? name : name + ", Malgun Gothic" }), width: 230);

        page.Children.Add(Card("글꼴 및 글자 크기",
            fontRow,
            RowCombo("글자 크기", null, Range(9, 30), () => (int)Math.Round(S.FontSize), v => Apply(s => s with { FontSize = v })),
            RowColor("글자 색", () => S.TextColor, v => Apply(s => s with { TextColor = v }))));

        page.Children.Add(Card("색상",
            RowColor("캘린더 배경", () => S.BackgroundColor, v => Apply(s => s with { BackgroundColor = v })),
            RowColor("휴일 배경색", () => S.HolidayBgColor, v => Apply(s => s with { HolidayBgColor = v }), allowNone: true),
            RowColor("휴일 글자색", () => S.HolidayColor, v => Apply(s => s with { HolidayColor = v })),
            RowColor("쉬는 날 글자색", () => S.OffDayColor, v => Apply(s => s with { OffDayColor = v })),
            RowColor("24절기 글자색", () => S.TermColor, v => Apply(s => s with { TermColor = v }))));

        var opacity = Enumerable.Range(0, 21).Select(i => ($"{i * 5}%", i * 5)).ToList();
        page.Children.Add(Card("투명도",
            RowCombo("배경 투명도", "0%는 완전 투명, 100%는 불투명", opacity, () => (int)Math.Round(S.BackgroundOpacity * 20) * 5, v => Apply(s => s with { BackgroundOpacity = v / 100.0 }))));

        page.Children.Add(Card("배경 사진", SkinRow(),
            RowCombo("맞춤 방식", null,
            [
                ("채우기 (넘치는 부분 잘림)", SkinFit.Fill), ("맞추기 (전체가 보이게)", SkinFit.Fit), ("늘이기 (비율 무시)", SkinFit.Stretch),
                ("바둑판", SkinFit.Tile), ("가운데 (원래 크기)", SkinFit.Center),
            ], () => S.SkinFit, v => Apply(s => s with { SkinFit = v }), width: 230),
            RowCombo("사진 바꾸는 주기", null,
            [
                ("바꾸지 않음", 0), ("5분", 5), ("15분", 15), ("30분", 30), ("1시간", 60), ("3시간", 180), ("12시간", 720), ("하루", 1440),
            ], () => S.SkinRotateMinutes, v => Apply(s => s with { SkinRotateMinutes = v }))));
        return page;
    }

    private UIElement MemoTab()
    {
        var page = new StackPanel();
        page.Children.Add(Card("노트", RowLink("노트 관리", "긴 글을 적어 두는 노트를 엽니다", "열기", _h.OpenNotes)));
        page.Children.Add(Card("디데이·시간표",
            RowLink("디데이 관리", "바탕화면 카드와 달력 아래 줄에 표시할 디데이", "열기", _h.OpenCountdowns),
            RowLink("시간표", "주간 시간표 편집", "열기", _h.OpenTimetable),
            RowToggle("바탕화면에 시간표 표시", null, _h.GetTimetableWidget, _h.SetTimetableWidget)));
        return page;
    }

    private UIElement AdvancedTab()
    {
        var page = new StackPanel();

        page.Children.Add(Card("고급 설정",
            RowCombo("시작 요일", null,
            [
                ("일요일", DayOfWeek.Sunday), ("월요일", DayOfWeek.Monday), ("화요일", DayOfWeek.Tuesday), ("수요일", DayOfWeek.Wednesday),
                ("목요일", DayOfWeek.Thursday), ("금요일", DayOfWeek.Friday), ("토요일", DayOfWeek.Saturday),
            ], () => S.FirstDayOfWeek, v => Apply(s => s with { FirstDayOfWeek = v })),
            RowCombo("첫 번째 주 기준", "주 번호를 세는 방법",
            [
                ("ISO 8601 (첫 목요일이 든 주)", WeekNumberRule.Iso), ("1월 1일이 든 주", WeekNumberRule.FirstDayOfYear),
            ], () => S.WeekRule, v => Apply(s => s with { WeekRule = v }), width: 230)));

        var offDays = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (dow, name) in new[]
                 {
                     (DayOfWeek.Monday, "월요일"), (DayOfWeek.Tuesday, "화요일"), (DayOfWeek.Wednesday, "수요일"), (DayOfWeek.Thursday, "목요일"),
                     (DayOfWeek.Friday, "금요일"), (DayOfWeek.Saturday, "토요일"), (DayOfWeek.Sunday, "일요일"),
                 })
        {
            var d = dow;
            var cb = new CheckBox { Content = name, Margin = new Thickness(0, 4, 18, 4), VerticalContentAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) => Apply(s => s with { OffDaysMask = cb.IsChecked == true ? s.OffDaysMask | (1 << (int)d) : s.OffDaysMask & ~(1 << (int)d) });
            _sync.Add(() => cb.IsChecked = S.IsOffDay(d));
            offDays.Children.Add(cb);
        }
        var offRow = RowCustom("쉬는 날", null, null);
        offRow.Child = Stack(new TextBlock { Text = "쉬는 날", Margin = new Thickness(0, 4, 0, 2) }, offDays);
        page.Children.Add(Card("휴무일",
            RowToggle("쉬는 날 색상 표시", "쉬는 날의 요일·날짜를 다른 색으로 그립니다", () => S.ShowOffDays, v => Apply(s => s with { ShowOffDays = v })),
            offRow));

        page.Children.Add(Card("단축키",
            RowToggle("전역 단축키 사용", "다른 프로그램을 쓰는 중에도 단축키로 달력을 부릅니다",
                () => _h.GetPrefs().HotkeysEnabled, v => { if (!_syncing) _h.SetPrefs(_h.GetPrefs() with { HotkeysEnabled = v }); }),
            RowLink("단축키 설정", "달력 앞으로·숨기기·일정 추가·검색·잠금", "바꾸기", EditHotkeys)));

        page.Children.Add(Card("시작과 휴일 데이터",
            RowToggle("Windows 시작 시 자동 실행", null, _h.GetAutoStart, v => { if (!_syncing) _h.SetAutoStart(v); }),
            RowLink("휴일 데이터 편집", "공휴일을 직접 추가·수정하는 파일을 엽니다", "열기", _h.EditHolidays),
            RowLink("휴일 데이터 다시 읽기", null, "실행", _h.ReloadHolidays)));
        return page;
    }

    private UIElement AiTab()
    {
        var page = new StackPanel();
        if (_h.Ai is not { } ai)
        {
            page.Children.Add(Card("AI", RowText("AI", "이 화면에서는 AI 설정을 바꿀 수 없습니다.")));
            return page;
        }
        AiSettings Cur() => ai.Get();
        void Change(Func<AiSettings, AiSettings> f) { if (_syncing) return; ai.Set(f(Cur())); SyncAll(); }

        page.Children.Add(Card("AI 기능",
            RowToggle("AI 기능 사용", "꺼져 있으면 AI 는 아무것도 하지 않습니다. 켜면 '빠른 입력'에서 규칙으로 읽지 못한 문장을 AI 가 해석해 제안합니다.",
                () => Cur().Enabled, v => Change(s => s with { Enabled = v })),
            RowText("개인정보", "기본 설정에서는 이 PC 의 AI 서버(예: Ollama)에만 연결하므로 달력 내용이 PC 밖으로 나가지 않습니다. AI 는 제안만 하고, 일정은 항상 직접 확인한 뒤 추가됩니다.")));

        // ---- the connection
        var endpoint = new TextBox { Width = 260, MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
        var endpointNote = new TextBlock { Foreground = Frozen(Color.FromRgb(0xC0, 0x39, 0x2B)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, MaxWidth = 420, Margin = new Thickness(14, 0, 14, 8) };
        void ApplyEndpoint()
        {
            if (_syncing) return;
            string text = endpoint.Text.Trim();
            if (!EndpointPolicy.TryParse(text, Cur().AllowRemote, out _, out string? error)) { endpointNote.Text = error; endpointNote.Visibility = Visibility.Visible; return; }
            endpointNote.Visibility = Visibility.Collapsed;
            if (text != Cur().Endpoint) Change(s => s with { Endpoint = text });
        }
        endpoint.LostFocus += (_, _) => ApplyEndpoint();
        endpoint.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyEndpoint(); };
        _sync.Add(() => { if (!endpoint.IsKeyboardFocusWithin) endpoint.Text = Cur().Endpoint; });

        var model = new ComboBox { Width = 230, MinHeight = 28, IsEditable = true, VerticalContentAlignment = VerticalAlignment.Center };
        model.SelectionChanged += (_, _) => { if (!_syncing && model.SelectedItem is string m && m != Cur().Model) Change(s => s with { Model = m }); };
        model.LostFocus += (_, _) => { if (!_syncing && model.Text.Trim() != Cur().Model) Change(s => s with { Model = model.Text.Trim() }); };
        _sync.Add(() => { if (!model.IsKeyboardFocusWithin) model.Text = Cur().Model; });

        var result = new TextBlock { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Right, Text = "아직 시험하지 않았습니다." };
        async Task RunTest()
        {
            result.Text = "연결하는 중…"; result.Foreground = MutedBrush;
            ConnectionReport report;
            try { report = await ai.Test(Cur(), CancellationToken.None); }
            catch (Exception ex) { report = new ConnectionReport(false, ex.Message, [], false); }
            result.Text = report.Message + (report.DataLeavesPc ? "\n⚠ 이 주소는 다른 컴퓨터입니다. 일정 제목·메모가 그곳으로 전송됩니다." : "");
            result.Foreground = report.Ok ? Frozen(Color.FromRgb(0x1E, 0x8E, 0x3E)) : report.Models.Count > 0 ? InkBrush : Frozen(Color.FromRgb(0xC0, 0x39, 0x2B));
            if (report.Models.Count > 0)
            {
                string keep = Cur().Model;
                model.Items.Clear();
                foreach (var m in report.Models) model.Items.Add(m);
                model.Text = keep;
            }
        }

        page.Children.Add(Card("연결",
            RowCombo("AI 서버 종류", "Ollama, 또는 LM Studio·llama.cpp 서버처럼 OpenAI 방식으로 말하는 서버",
                [("Ollama", AiProvider.Ollama), ("OpenAI 호환 (LM Studio 등)", AiProvider.OpenAiCompatible)],
                () => Cur().Provider,
                v => Change(s => s with
                {
                    Provider = v, Model = "",
                    Endpoint = s.Endpoint == AiSettings.DefaultEndpoint(s.Provider) ? AiSettings.DefaultEndpoint(v) : s.Endpoint,
                }), width: 220),
            RowCustom("서버 주소", "이 컴퓨터(127.0.0.1, localhost)만 쓸 수 있습니다", endpoint),
            endpointNote,
            RowCustom("모델", "목록에서 고르거나 이름을 직접 쓰세요 (예: gemma4:e2b). 작은 모델일수록 빠르지만 틀리기 쉬우므로 결과는 항상 확인하세요", model),
            RowLink("연결 시험", "서버에 연결해 설치된 모델 목록을 가져옵니다", "시험하기", () => _ = RunTest()),
            RowCustom("시험 결과", null, result)));

        var keepRow = RowCombo("모델을 메모리에 유지", "마지막 사용 뒤 이만큼 지나면 AI 서버가 모델을 내립니다 (Ollama)",
            [("바로 내림", "0"), ("5분", "5m"), ("30분", "30m"), ("2시간", "2h"), ("계속", "-1")], () => Cur().KeepAlive, v => Change(s => s with { KeepAlive = v }));
        Gate(keepRow, () => Cur().Provider == AiProvider.Ollama);
        page.Children.Add(Card("고급",
            RowCombo("응답 기다리는 시간", "처음 모델을 불러올 때는 오래 걸릴 수 있습니다",
                [("30초", 30), ("1분", 60), ("2분", 120), ("5분", 300)], () => Cur().TimeoutSeconds, v => Change(s => s with { TimeoutSeconds = v })),
            keepRow,
            RowToggle("다른 컴퓨터의 주소 허용", "⚠ 켜면 일정 제목·메모가 그 서버로 전송됩니다. https:// 주소만 쓸 수 있습니다. 잘 모르면 꺼 두세요.",
                () => Cur().AllowRemote, v => Change(s => s with { AllowRemote = v }))));

        if (ai.Plus is { } plus)
        {
            // ---- meaning search: a second model that turns each to-do and event into numbers, so that "병원 갔던 날" finds "치과 정기검진"
            var embed = new ComboBox { Width = 230, MinHeight = 28, IsEditable = true, VerticalContentAlignment = VerticalAlignment.Center };
            embed.SelectionChanged += (_, _) => { if (!_syncing && embed.SelectedItem is string m && m != Cur().EmbedModel) Change(s => s with { EmbedModel = m }); };
            embed.LostFocus += (_, _) => { if (!_syncing && embed.Text.Trim() != Cur().EmbedModel) Change(s => s with { EmbedModel = embed.Text.Trim() }); };
            _sync.Add(() => { if (!embed.IsKeyboardFocusWithin) embed.Text = Cur().EmbedModel; });
            var indexState = new TextBlock { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Right };
            _sync.Add(() => indexState.Text = plus.IndexStatus());
            async Task Build()
            {
                indexState.Text = "색인을 만드는 중… (처음에는 모델을 불러오느라 시간이 걸립니다)";
                string result;
                try { result = await plus.BuildIndex(); } catch (Exception ex) { result = ex.Message; }
                indexState.Text = result;
            }
            page.Children.Add(Card("의미 검색",
                RowToggle("의미 검색 사용", "검색 창에서 '의미 검색'을 고를 수 있습니다. 켜면 할 일·일정의 글을 임베딩 모델에 보내 숫자로 바꾼 색인을 이 컴퓨터에 만듭니다 (AI 기능이 켜져 있어야 합니다).",
                    () => Cur().SemanticSearch, v => Change(s => s with { SemanticSearch = v })),
                RowCustom("임베딩 모델", "예: bge-m3 (한국어 지원). 위의 '연결 시험'으로 목록을 가져올 수 있습니다. 모델을 바꾸면 색인을 다시 만듭니다.", embed),
                RowCustom("색인 상태", null, indexState),
                RowLink("색인 만들기·갱신", "새로 쓰거나 고친 글만 다시 계산합니다", "실행", () => _ = Build()),
                RowLink("색인 지우기", "색인은 글에서 만든 파생 데이터입니다. 지워도 일정은 그대로이고 언제든 다시 만들 수 있습니다", "지우기", () => { plus.ClearIndex(); SyncAll(); })));

            // ---- the morning briefing (needs no AI; the model only rephrases it, if asked to)
            var b = plus.GetBriefing;
            void ChangeB(Func<BriefingSettings, BriefingSettings> f) { if (_syncing) return; plus.SetBriefing(f(b())); SyncAll(); }
            page.Children.Add(Card("아침 브리핑",
                RowToggle("브리핑을 자동으로 띄우기", "하루에 한 번, 정한 시각부터 18시 사이에 오늘의 일정·할 일·빈 시간을 작은 창으로 보여 줍니다. 트레이의 '오늘 브리핑…'으로 언제든 열 수 있습니다.",
                    () => b().Auto, v => ChangeB(s => s with { Auto = v })),
                RowCombo("띄우는 시각", "이 시각 이후 처음 앱이 켜져 있을 때", [("7시", 7), ("8시", 8), ("9시", 9), ("10시", 10), ("11시", 11), ("12시", 12)], () => b().Hour, v => ChangeB(s => s with { Hour = v })),
                RowToggle("평일(공휴일 제외)에만", null, () => b().WorkdaysOnly, v => ChangeB(s => s with { WorkdaysOnly = v })),
                RowToggle("AI 로 문장 다듬기", "자동으로 띄울 때 AI 에게 문장을 다듬게 합니다. 일정이 빠지거나 없는 시각이 들어간 답은 버리고 원본을 보여 줍니다. 실패하면 간격을 늘려 다시 시도합니다.",
                    () => b().UseLlm, v => ChangeB(s => s with { UseLlm = v }))));
        }

        if (ai.Accuracy is { } accuracy)
        {
            // the user's own "맞음/틀림" marks on the quick-input cards: four counters kept on this computer, never any text
            var rules = new TextBlock { Foreground = MutedBrush, TextAlignment = TextAlignment.Right };
            var llm = new TextBlock { Foreground = MutedBrush, TextAlignment = TextAlignment.Right };
            _sync.Add(() =>
            {
                var s = accuracy();
                rules.Text = AccuracySummary.Describe(s.RulesOk, s.RulesBad);
                llm.Text = AccuracySummary.Describe(s.LlmOk, s.LlmBad);
            });
            page.Children.Add(Card("정확도 기록 (내 컴퓨터에만 저장)",
                RowCustom("규칙으로 읽은 것", "빠른 입력 카드에서 누른 '맞음/틀림'의 횟수입니다. 문장 내용은 저장하지 않습니다.", rules),
                RowCustom("AI 가 읽은 것", null, llm),
                RowLink("기록 지우기", null, "지우기", () => { ai.ResetAccuracy?.Invoke(); SyncAll(); })));
        }
        return page;
    }

    private UIElement AboutTab()
    {
        var page = new StackPanel();
        page.Children.Add(Card("소프트웨어 정보",
            RowText("M-DeskCal", AppInfo.Version),
            RowText("시스템", $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}"),
            RowLink("데이터 폴더", "일정·설정이 저장된 폴더", "열기", _h.OpenDataFolder),
            RowLink("진단 정보", "문제가 생겼을 때 확인하는 정보", "보기", _h.ShowDiagnostics)));
        page.Children.Add(Card("저작권", RowText("© M-DeskCal", "이 프로그램은 직접 구현한 소프트웨어이며 다른 제품의 코드와 리소스를 쓰지 않았습니다.")));
        return page;
    }

    private void EditHotkeys()
    {
        var prefs = _h.GetPrefs();
        if (HotkeyDialog.Edit(prefs) is { } edited) _h.SetPrefs(edited with { HotkeysEnabled = prefs.HotkeysEnabled });
    }

    // ---- page building blocks ------------------------------------------------------

    private static List<(string, int)> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => (i.ToString(CultureInfo.InvariantCulture), i)).ToList();

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel();
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    /// <summary>A white panel with a blue-barred heading; rows are separated by hairlines.</summary>
    private static UIElement Card(string title, params UIElement[] rows)
    {
        var inner = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 14, 9) };
        head.Children.Add(new Border { Width = 3, Height = 14, Background = AccentBrush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        inner.Children.Add(head);
        foreach (var r in rows)
        {
            if (r is Border b) b.BorderThickness = new Thickness(0, 1, 0, 0);
            inner.Children.Add(r);
        }
        return new Border
        {
            Background = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 14), Child = inner,
        };
    }

    /// <summary>A bare row that holds free content (no hairline of its own colour set yet).</summary>
    private static Border RowCustom(string label, string? hint, UIElement? right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (hint is not null) text.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        grid.Children.Add(text);
        if (right is not null)
        {
            Grid.SetColumn((FrameworkElement)right, 1);
            ((FrameworkElement)right).VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(right);
        }
        return new Border { Padding = new Thickness(14, 9, 14, 9), BorderBrush = LineBrush, Child = grid };
    }

    private static UIElement Pad(UIElement inner)
    {
        var b = new Border { Padding = new Thickness(10, 8, 10, 12), BorderBrush = LineBrush, Child = inner };
        return b;
    }

    /// <summary>Dim a row and make it inert while <paramref name="when"/> is false.</summary>
    private void Gate(Border row, Func<bool> when) =>
        _sync.Add(() => { bool on = when(); row.IsEnabled = on; row.Opacity = on ? 1 : 0.45; });

    private Border RowCombo<T>(string label, string? hint, IReadOnlyList<(string Label, T Value)> items, Func<T> get, Action<T> set, double width = 120)
    {
        var cb = new ComboBox { Width = width, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var i in items) cb.Items.Add(i.Label);
        cb.SelectionChanged += (_, _) => { if (!_syncing && cb.SelectedIndex >= 0) set(items[cb.SelectedIndex].Value); };
        _sync.Add(() =>
        {
            var current = get();
            int index = -1;
            for (int i = 0; i < items.Count; i++) if (EqualityComparer<T>.Default.Equals(items[i].Value, current)) { index = i; break; }
            cb.SelectedIndex = index;
        });
        return RowCustom(label, hint, cb);
    }

    private Border RowToggle(string label, string? hint, Func<bool> get, Action<bool> set)
    {
        var state = new TextBlock { Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 26, TextAlignment = TextAlignment.Right };
        var toggle = new System.Windows.Controls.Primitives.ToggleButton { Style = ToggleStyle, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += (_, _) => { set(toggle.IsChecked == true); state.Text = toggle.IsChecked == true ? "켜짐" : "꺼짐"; SyncAll(); };
        _sync.Add(() => { toggle.IsChecked = get(); state.Text = toggle.IsChecked == true ? "켜짐" : "꺼짐"; });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(state); right.Children.Add(toggle);
        return RowCustom(label, hint, right);
    }

    private static Border RowText(string label, string value)
    {
        var v = new TextBlock { Text = value, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Right };
        return RowCustom(label, null, v);
    }

    private static Border RowLink(string label, string? hint, string action, Action click)
    {
        var link = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        link.Children.Add(new TextBlock { Text = action, Foreground = AccentBrush, VerticalAlignment = VerticalAlignment.Center });
        link.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 11, Foreground = MutedBrush, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var row = RowCustom(label, hint, link);
        row.Cursor = Cursors.Hand;
        row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    // ---- colour and picture rows ----------------------------------------------------

    private static readonly string[] SwatchColors =
    [
        "#FFFFFF", "#E5E7EB", "#9CA3AF", "#4B5563", "#1F2937", "#000000",
        "#FF6B6B", "#FFA94D", "#FFE066", "#69DB7C", "#8FD694", "#74C0FC",
        "#3B82F6", "#B197FC", "#F783AC", "#101820", "#1E3A5F", "#3A5F7A",
    ];

    private Border RowColor(string label, Func<string> get, Action<string> set, bool allowNone = false)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Frozen(Color.FromRgb(0xB8, 0xBE, 0xC8)), BorderThickness = new Thickness(1) };
        var hex = new TextBlock { Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = MutedBrush, MinWidth = 62 };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(swatch); line.Children.Add(hex);
        line.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });
        var button = new Border
        {
            Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
            Background = Frozen(Color.FromRgb(0xF6, 0xF8, 0xFB)), BorderBrush = Frozen(Color.FromRgb(0xC9, 0xCE, 0xD6)), BorderThickness = new Thickness(1), Child = line,
        };

        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var grid = new WrapPanel { Width = 6 * 28 };
        foreach (var c in SwatchColors)
        {
            var color = c;
            var sw = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(3), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                Background = Frozen(Parse(color)), BorderBrush = Frozen(Color.FromRgb(0xB8, 0xBE, 0xC8)), BorderThickness = new Thickness(1), ToolTip = color,
            };
            sw.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; set(color); };
            grid.Children.Add(sw);
        }
        var custom = new TextBlock { Text = "사용자 지정…", Foreground = AccentBrush, Cursor = Cursors.Hand, Margin = new Thickness(5, 6, 0, 2) };
        custom.MouseLeftButtonUp += (_, _) =>
        {
            popup.IsOpen = false;
            var p = Parse(get());
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(p.R, p.G, p.B) };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) set($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        };
        var footer = new DockPanel { Margin = new Thickness(0, 0, 4, 0) };
        if (allowNone)
        {
            var clear = new TextBlock { Text = "없음", Foreground = MutedBrush, Cursor = Cursors.Hand, Margin = new Thickness(0, 6, 0, 2) };
            clear.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; set(""); };
            DockPanel.SetDock(clear, Dock.Right);
            footer.Children.Add(clear);
        }
        footer.Children.Add(custom);
        popup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = Frozen(Color.FromRgb(0xC9, 0xCE, 0xD6)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Child = Stack(grid, footer),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3 },
        };
        button.MouseLeftButtonUp += (_, _) => popup.IsOpen = true;

        _sync.Add(() =>
        {
            var v = get();
            bool empty = string.IsNullOrWhiteSpace(v);
            swatch.Background = empty ? Brushes.Transparent : Frozen(Parse(v));
            hex.Text = empty ? "없음" : v.ToUpperInvariant();
        });
        var wrap = new StackPanel { Orientation = Orientation.Horizontal };
        wrap.Children.Add(button);
        wrap.Children.Add(popup);
        return RowCustom(label, null, wrap);
    }

    private static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Colors.White; }
    }

    private Border SkinRow()
    {
        var path = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center, Foreground = MutedBrush, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var pick = new Button { Content = "폴더 선택…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        pick.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "배경으로 쓸 사진이 들어 있는 폴더를 고르세요 (png, jpg, bmp, gif, tif).", UseDescriptionForTitle = true,
                SelectedPath = S.SkinFolder,
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            if (SkinPicker.List(dlg.SelectedPath).Count == 0) { MessageBox.Show(this, "이 폴더에는 사용할 수 있는 사진이 없습니다.", "배경 사진"); return; }
            Apply(s => s with { SkinFolder = dlg.SelectedPath });
        };
        var off = new Button { Content = "끄기", Padding = new Thickness(10, 3, 10, 3) };
        off.Click += (_, _) => Apply(s => s with { SkinFolder = "" });
        _sync.Add(() => { path.Text = string.IsNullOrEmpty(S.SkinFolder) ? "사진 없음" : S.SkinFolder; path.ToolTip = S.SkinFolder; off.IsEnabled = !string.IsNullOrEmpty(S.SkinFolder); });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(path); right.Children.Add(pick); right.Children.Add(off);
        return RowCustom("사진 폴더", "폴더 안의 사진을 달력 뒤에 깝니다", right);
    }

    // ---- picture cards (display mode, bullets) ----------------------------------------

    private UIElement ChoiceCards<T>(IReadOnlyList<(string Label, T Value, Func<UIElement> Glyph)> items, Func<T> get, Action<T> set)
    {
        double cardW = items.Count >= 5 ? 78 : 84;
        double cardH = items.Count >= 5 ? 58 : 62;
        double marginX = items.Count >= 5 ? 4 : 8;
        double viewboxMargin = items.Count >= 5 ? 8 : 10;

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var cards = new List<(Border Card, UIElement Check, T Value)>();
        foreach (var (label, value, glyph) in items)
        {
            var v = value;
            var box = new Border
            {
                Width = cardW, Height = cardH, CornerRadius = new CornerRadius(7), Background = Frozen(Color.FromRgb(0xE7, 0xEA, 0xF0)),
                Child = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(viewboxMargin), Child = glyph() },
            };
            var check = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = AccentBrush, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4),
                Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var frame = new Border
            {
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Padding = new Thickness(3),
                Child = new Grid { Children = { box, check } },
            };
            var card = new StackPanel { Margin = new Thickness(marginX, 2, marginX, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            card.Children.Add(frame);
            card.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            card.MouseLeftButtonUp += (_, _) => { if (!_syncing) set(v); };
            cards.Add((frame, check, value));
            row.Children.Add(card);
        }
        _sync.Add(() =>
        {
            var cur = get();
            foreach (var (frame, check, value) in cards)
            {
                bool on = EqualityComparer<T>.Default.Equals(value, cur);
                frame.BorderBrush = on ? AccentBrush : Brushes.Transparent;
                check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        });
        return row;
    }

    /// <summary>A little grid of day cells (the display-mode pictures).</summary>
    private static UIElement Cells(int cols, int rows, double w, double h, double gap, Func<int, double> opacity, int highlight = -1)
    {
        var c = new Canvas { Width = cols * (w + gap) - gap, Height = rows * (h + gap) - gap };
        for (int r = 0; r < rows; r++)
            for (int col = 0; col < cols; col++)
            {
                int idx = r * cols + col;
                var cell = new Shapes.Rectangle
                {
                    Width = w, Height = h, RadiusX = 1, RadiusY = 1, Opacity = opacity(idx),
                    Fill = idx == highlight ? AccentBrush : GlyphBrush,
                };
                Canvas.SetLeft(cell, col * (w + gap));
                Canvas.SetTop(cell, r * (h + gap));
                c.Children.Add(cell);
            }
        return c;
    }

    private static UIElement WithArrows(UIElement body, bool vertical)
    {
        Shapes.Polygon Tri(PointCollection pts) => new() { Points = pts, Fill = GlyphBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(vertical ? 0 : 3, vertical ? 3 : 0, vertical ? 0 : 3, vertical ? 3 : 0) };
        var up = Tri(vertical ? [new Point(0, 6), new Point(10, 6), new Point(5, 0)] : [new Point(6, 0), new Point(6, 10), new Point(0, 5)]);
        var down = Tri(vertical ? [new Point(0, 0), new Point(10, 0), new Point(5, 6)] : [new Point(0, 0), new Point(0, 10), new Point(6, 5)]);
        var p = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(up); p.Children.Add(body); p.Children.Add(down);
        return p;
    }

    private static UIElement ListGlyph(string[]? markers)
    {
        var p = new StackPanel { Width = 54 };
        for (int i = 0; i < 3; i++)
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            if (markers is not null)
            {
                var m = new TextBlock { Text = markers[i], FontSize = 9, FontWeight = FontWeights.Bold, Foreground = GlyphBrush, Width = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(m, Dock.Left);
                line.Children.Add(m);
            }
            line.Children.Add(new Shapes.Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Fill = GlyphBrush, Opacity = i == 2 ? 0.55 : 1, HorizontalAlignment = HorizontalAlignment.Left, Width = i == 2 ? 24 : 38 });
            p.Children.Add(line);
        }
        return p;
    }

    // ---- toggle switch ------------------------------------------------------------------

    private static readonly Style ToggleStyle = (Style)XamlReader.Parse(
        """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ToggleButton">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ToggleButton">
                <Grid Width="42" Height="22" Background="Transparent" Cursor="Hand">
                  <Border x:Name="Track" CornerRadius="11" Background="#B8BEC8"/>
                  <Ellipse x:Name="Knob" Width="16" Height="16" Fill="White" HorizontalAlignment="Left" Margin="3,0,0,0"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Track" Property="Background" Value="#2F8CFF"/>
                    <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
                    <Setter TargetName="Knob" Property="Margin" Value="0,0,3,0"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.5"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
