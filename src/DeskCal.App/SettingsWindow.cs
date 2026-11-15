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
    Action OpenCountdowns,
    Action OpenTimetable,
    Action EditHolidays,
    Action ReloadHolidays,
    Action OpenDataFolder,
    Action ShowDiagnostics,
    AiHost? Ai = null,
    GitSyncHost? Git = null);

/// <summary>
/// "캘린더 설정": the full set of options, in seven tabs. The gear's quick menu holds the handful used every day;
/// everything else is here.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public const int SyncTabIndex = 6, AboutTabIndex = 7, TabCount = 8;

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
    private readonly List<(Shapes.Path Icon, TextBlock Label)> _tabParts = [];
    private readonly TextBlock _pageTitle = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = InkBrush, Margin = new Thickness(0, 0, 0, 14) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private readonly (string Group, string Title, string IconData, Func<UIElement> Build)[] _tabs;

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
            ("캘린더 설정", "캘린더", ModernUi.Icons.CalendarPlus, CalendarTab),
            ("캘린더 설정", "셀", ModernUi.Icons.Layout, CellTab),
            ("캘린더 설정", "글꼴 및 색상", ModernUi.Icons.Droplet, FontTab),
            ("캘린더 설정", "고급", ModernUi.Icons.GroupSettings, AdvancedTab),
            ("기능 · 서비스", "부가 기능", ModernUi.Icons.Layers, AddonsTab),
            ("기능 · 서비스", "AI", ModernUi.Icons.Sparkles, AiTab),
            ("기능 · 서비스", "동기화", ModernUi.Icons.RepeatPlan, SyncTab),
            ("시스템", "제품 정보", ModernUi.Icons.Info, AboutTab),
        ];

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // left: grouped tab list and "restore defaults"
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
        string? lastGroup = null;
        for (int i = 0; i < _tabs.Length; i++)
        {
            if (_tabs[i].Group != lastGroup)
            {
                if (lastGroup is not null)
                    tabList.Children.Add(new Border { Height = 1, Background = Frozen(Color.FromRgb(0xD8, 0xDF, 0xEA)), Margin = new Thickness(6, 8, 6, 8) });
                tabList.Children.Add(new TextBlock
                {
                    Text = _tabs[i].Group,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = MutedBrush,
                    Margin = new Thickness(8, lastGroup is null ? 0 : 2, 0, 6),
                });
                lastGroup = _tabs[i].Group;
            }
            int index = i;
            var label = TabLabel(_tabs[i].IconData, _tabs[i].Title, out var iconPath, out var labelText);
            var b = new Border
            {
                Padding = new Thickness(11, 8, 10, 8), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand, Child = label,
            };
            b.MouseLeftButtonUp += (_, _) => ShowTab(index);
            _tabButtons.Add(b);
            _tabParts.Add((iconPath, labelText));
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
            _tabButtons[i].Background = on ? Brushes.White : Frozen(Color.FromArgb(0x70, 0xDC, 0xE2, 0xEC));
            _tabParts[i].Icon.Stroke = on ? AccentBrush : MutedBrush;
            _tabParts[i].Label.Foreground = on ? AccentBrush : InkBrush;
            _tabParts[i].Label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _pageTitle.Text = _tabs[index].Title;
        _sync.Clear();
        _scroll.Content = _tabs[index].Build();
        _scroll.ScrollToTop();
        SyncAll();
    }

    private int CurrentTab() => Math.Max(0, _tabButtons.FindIndex(b => ReferenceEquals(b.Background, Brushes.White)));

    private static UIElement TabLabel(string iconData, string title, out Shapes.Path iconPath, out TextBlock labelText)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var vb = ModernUi.FlatIcon(iconData, MutedBrush, 16, 1.9);
        vb.Margin = new Thickness(0, 0, 9, 0);
        vb.VerticalAlignment = VerticalAlignment.Center;
        iconPath = (Shapes.Path)vb.Child;
        labelText = new TextBlock { Text = title, FontSize = 13.5, Foreground = InkBrush, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(vb);
        p.Children.Add(labelText);
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

        page.Children.Add(IconCard(ModernUi.Icons.GroupDisplay, "미리보기", FontPreview()));

        var families = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        string Current() => S.FontFamily.Split(',')[0].Trim();
        if (!families.Contains(Current())) families.Insert(0, Current());
        var fontRow = RowCombo("글꼴", "달력 전체에 적용할 기본 글꼴", families.Select(f => (f, f)).ToList(), Current,
            name => Apply(s => s with { FontFamily = name == "Malgun Gothic" ? name : name + ", Malgun Gothic" }), width: 230);

        page.Children.Add(IconCard(ModernUi.Icons.Type, "글꼴 및 글자 크기",
            fontRow,
            RowCombo("글자 크기", "9~30pt 사이에서 조절합니다", Range(9, 30), () => (int)Math.Round(S.FontSize), v => Apply(s => s with { FontSize = v })),
            RowColor("글자 색", () => S.TextColor, v => Apply(s => s with { TextColor = v }))));

        // shown as transparency: 0% = fully opaque, 100% = fully transparent (BackgroundOpacity is the opposite)
        var opacity = Enumerable.Range(0, 21).Select(i => (i == 0 ? "0% (완전 불투명)" : i == 20 ? "100% (완전 투명)" : $"{i * 5}%", i * 5)).ToList();
        page.Children.Add(IconCard(ModernUi.Icons.Droplet, "색상 및 투명도",
            RowColor("캘린더 배경", () => S.BackgroundColor, v => Apply(s => s with { BackgroundColor = v })),
            RowCombo("배경 투명도", "0%는 완전 불투명, 100%는 완전 투명", opacity, () => (int)Math.Round((1 - S.BackgroundOpacity) * 20) * 5, v => Apply(s => s with { BackgroundOpacity = 1 - v / 100.0 })),
            RowColor("휴일 배경색", () => S.HolidayBgColor, v => Apply(s => s with { HolidayBgColor = v }), allowNone: true),
            RowColor("휴일 글자색", () => S.HolidayColor, v => Apply(s => s with { HolidayColor = v })),
            RowColor("쉬는 날 글자색", () => S.OffDayColor, v => Apply(s => s with { OffDayColor = v })),
            RowColor("24절기 글자색", () => S.TermColor, v => Apply(s => s with { TermColor = v }))));

        page.Children.Add(IconCard(ModernUi.Icons.Image, "배경 사진", SkinRow(),
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

    /// <summary>Live 3-cell calendar preview reflecting font, size, text/holiday/term colors, and opacity.</summary>
    private Border FontPreview()
    {
        Border Cell(string dayNum, string? sub, Brush? subBrush, string? todo, out TextBlock numTb, out TextBlock? subTb, out TextBlock? todoTb)
        {
            var sp = new StackPanel();
            numTb = new TextBlock { Text = dayNum, FontWeight = FontWeights.SemiBold };
            sp.Children.Add(numTb);
            if (sub is not null)
            {
                subTb = new TextBlock { Text = sub, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 2) };
                if (subBrush is not null) subTb.Foreground = subBrush;
                sp.Children.Add(subTb);
            }
            else subTb = null;
            if (todo is not null)
            {
                todoTb = new TextBlock { Text = todo, TextTrimming = TextTrimming.CharacterEllipsis };
                sp.Children.Add(todoTb);
            }
            else todoTb = null;
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 7, 9, 8),
                Margin = new Thickness(0, 0, 6, 0),
                Child = sp,
            };
        }

        var c1 = Cell("15 금", "입동", null, "• 팀 회의 10:00", out var n1, out var s1, out var t1);
        var c2 = Cell("16 토", "주말", null, "• 운동 30분", out var n2, out var s2, out var t2);
        var c3 = Cell("17 일", "공휴일", null, "• 가족 모임", out var n3, out var s3, out var t3);
        c3.Margin = new Thickness(0);

        var cols = new Grid();
        for (int i = 0; i < 3; i++) cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(c1, 0); Grid.SetColumn(c2, 1); Grid.SetColumn(c3, 2);
        cols.Children.Add(c1); cols.Children.Add(c2); cols.Children.Add(c3);

        var boardBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = cols,
        };

        var wallpaper = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x4C, 0x6F), Color.FromRgb(0x47, 0x7A, 0x8F), 25),
            Child = boardBox,
        };

        _sync.Add(() =>
        {
            var s = S;
            var bg = UiColors.Parse(s.BackgroundColor, Color.FromRgb(0x10, 0x18, 0x20));
            byte alpha = (byte)Math.Clamp((int)Math.Round(s.BackgroundOpacity * 255), 0, 255);
            boardBox.Background = Frozen(Color.FromArgb(alpha, bg.R, bg.G, bg.B));

            var holBg = string.IsNullOrEmpty(s.HolidayBgColor)
                ? Brushes.Transparent
                : Frozen(UiColors.Parse(s.HolidayBgColor, Colors.Transparent));
            c1.Background = Brushes.Transparent;
            c2.Background = holBg;
            c3.Background = holBg;

            var ff = new FontFamily(s.FontFamily);
            double sz = Math.Clamp(s.FontSize, 10, 18);
            var textBr = Frozen(UiColors.Parse(s.TextColor, Colors.White));
            var holBr = Frozen(UiColors.Parse(s.HolidayColor, Color.FromRgb(0xFF, 0x6B, 0x6B)));
            var offBr = Frozen(UiColors.Parse(s.OffDayColor, Color.FromRgb(0xFF, 0x7A, 0x7A)));
            var termBr = Frozen(UiColors.Parse(s.TermColor, Color.FromRgb(0x8F, 0xD6, 0x94)));

            foreach (var tb in new[] { n1, n2, n3, t1!, t2!, t3! }) { tb.FontFamily = ff; tb.FontSize = sz; }
            n1.Foreground = textBr;
            n2.Foreground = s.ShowOffDays ? offBr : textBr;
            n3.Foreground = holBr;
            if (s1 is not null) { s1.FontFamily = ff; s1.Foreground = termBr; }
            if (s2 is not null) { s2.FontFamily = ff; s2.Foreground = s.ShowOffDays ? offBr : textBr; }
            if (s3 is not null) { s3.FontFamily = ff; s3.Foreground = holBr; }
            if (t1 is not null) t1.Foreground = textBr;
            if (t2 is not null) t2.Foreground = textBr;
            if (t3 is not null) t3.Foreground = textBr;
        });

        return new Border { Padding = new Thickness(14, 10, 14, 12), BorderBrush = LineBrush, Child = wallpaper };
    }

    private UIElement AddonsTab()
    {
        var page = new StackPanel();
        page.Children.Add(IconCard(ModernUi.Icons.Flag, "디데이",
            RowLink("디데이 관리", "바탕화면 카드와 달력 아래 줄에 표시할 기념일·시험·생일을 관리합니다", "열기", _h.OpenCountdowns)));
        page.Children.Add(IconCard(ModernUi.Icons.Layout, "시간표",
            RowLink("시간표 편집", "요일·교시별 주간 시간표를 편집합니다", "열기", _h.OpenTimetable),
            RowToggle("바탕화면에 시간표 표시", "오늘 요일과 현재 진행 중인 교시를 바탕화면 위젯으로 표시합니다", _h.GetTimetableWidget, _h.SetTimetableWidget)));
        return page;
    }

    private UIElement AdvancedTab()
    {
        var page = new StackPanel();

        page.Children.Add(IconCard(ModernUi.Icons.CalendarPlus, "달력 기준",
            RowCombo("시작 요일", "한 주의 첫 번째 칸에 올 요일",
            [
                ("일요일", DayOfWeek.Sunday), ("월요일", DayOfWeek.Monday), ("화요일", DayOfWeek.Tuesday), ("수요일", DayOfWeek.Wednesday),
                ("목요일", DayOfWeek.Thursday), ("금요일", DayOfWeek.Friday), ("토요일", DayOfWeek.Saturday),
            ], () => S.FirstDayOfWeek, v => Apply(s => s with { FirstDayOfWeek = v })),
            RowCombo("첫 번째 주 기준", "주 번호를 세는 방법",
            [
                ("ISO 8601 (첫 목요일이 든 주)", WeekNumberRule.Iso), ("1월 1일이 든 주", WeekNumberRule.FirstDayOfYear),
            ], () => S.WeekRule, v => Apply(s => s with { WeekRule = v }), width: 230)));

        // Interactive 7-day pill selector for off-days
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (dow, shortName) in new[]
                 {
                     (DayOfWeek.Monday, "월"), (DayOfWeek.Tuesday, "화"), (DayOfWeek.Wednesday, "수"), (DayOfWeek.Thursday, "목"),
                     (DayOfWeek.Friday, "금"), (DayOfWeek.Saturday, "토"), (DayOfWeek.Sunday, "일"),
                 })
        {
            var d = dow;
            var label = new TextBlock { Text = shortName, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var pill = new Border
            {
                Width = 34, Height = 30, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Child = label,
            };
            pill.MouseLeftButtonUp += (_, _) =>
            {
                bool on = !S.IsOffDay(d);
                Apply(s => s with { OffDaysMask = on ? s.OffDaysMask | (1 << (int)d) : s.OffDaysMask & ~(1 << (int)d) });
            };
            _sync.Add(() =>
            {
                bool on = S.IsOffDay(d);
                pill.Background = on ? ModernUi.DangerSoftBrush : ModernUi.CardAltBrush;
                pill.BorderBrush = on ? ModernUi.DangerBorderBrush : LineBrush;
                label.Foreground = on ? ModernUi.DangerBrush : MutedBrush;
                label.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            });
            pillRow.Children.Add(pill);
        }

        page.Children.Add(IconCard(ModernUi.Icons.Flag, "휴무일",
            RowToggle("쉬는 날 색상 표시", "선택한 요일의 날짜를 쉬는 날 색으로 강조합니다", () => S.ShowOffDays, v => Apply(s => s with { ShowOffDays = v })),
            RowCustom("쉬는 날 지정", "클릭하여 주말·휴무 요일을 켜고 끕니다", pillRow)));

        // deskPet_Bugi style click-to-capture hotkey row for 캘린더 채팅
        var chatHkHint = new TextBlock { FontSize = 11.5, Foreground = AccentBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 8), Visibility = Visibility.Collapsed };
        var keycaps = new WrapPanel { Margin = new Thickness(14, 0, 14, 10) };
        var chatHkBox = new HotkeyCaptureBox(_h.GetPrefs().QuickInput);
        Button chatHkReset = null!;
        void RefreshKeycaps()
        {
            var p = _h.GetPrefs();
            chatHkBox.IsEnabled = chatHkReset.IsEnabled = p.HotkeysEnabled;
            if (!chatHkBox.IsKeyboardFocusWithin) chatHkBox.SetValue(p.QuickInput);
            keycaps.Children.Clear();
            foreach (var (name, key) in new[]
                     {
                         ("캘린더 채팅", p.QuickInput.Format()), ("앞으로", p.BringToFront.Format()),
                         ("숨김/표시", p.ToggleVisible.Format()), ("새 일정", p.NewEvent.Format()), ("잠금", p.ToggleContent.Format()),
                     })
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                chip.Children.Add(new TextBlock { Text = name, FontSize = 11.5, Foreground = MutedBrush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                chip.Children.Add(new TextBlock { Text = key, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ModernUi.InkBrush, VerticalAlignment = VerticalAlignment.Center });
                keycaps.Children.Add(new Border
                {
                    Background = ModernUi.CardAltBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 6, 0),
                    Child = chip,
                });
            }
        }
        chatHkBox.ValidateConflict = hk =>
        {
            var cur = _h.GetPrefs();
            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                if (action == HotkeyAction.QuickInput) continue;
                if (cur.Get(action) == hk) return $"'{HotkeyManager.ActionName(action)}' 단축키와 겹칩니다.";
            }
            return null;
        };
        chatHkReset = ModernUi.ChipBtn("기본값", (_, _) =>
        {
            var def = new AppPreferences().QuickInput;
            chatHkBox.SetValue(def);
            chatHkHint.Visibility = Visibility.Collapsed;
            _h.SetPrefs(_h.GetPrefs() with { QuickInput = def });
            RefreshKeycaps();
        });
        chatHkReset.Margin = new Thickness(6, 0, 0, 0);
        chatHkBox.Hint += msg =>
        {
            chatHkHint.Text = msg ?? "";
            chatHkHint.Foreground = msg is not null && (msg.Contains("겹칩") || msg.Contains("눌러야") || msg.Contains("없습니다")) ? ModernUi.DangerBrush : AccentBrush;
            chatHkHint.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        };
        chatHkBox.HotkeyChanged += hk =>
        {
            if (!_syncing) _h.SetPrefs(_h.GetPrefs() with { QuickInput = hk });
            RefreshKeycaps();
        };
        var chatHkCtrl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        chatHkCtrl.Children.Add(chatHkBox);
        chatHkCtrl.Children.Add(chatHkReset);
        _sync.Add(RefreshKeycaps);

        page.Children.Add(IconCard(ModernUi.Icons.Keyboard, "단축키",
            RowToggle("전역 단축키 사용", "다른 프로그램을 쓰는 중에도 단축키로 달력을 부릅니다",
                () => _h.GetPrefs().HotkeysEnabled, v => { if (!_syncing) { _h.SetPrefs(_h.GetPrefs() with { HotkeysEnabled = v }); RefreshKeycaps(); } }),
            RowCustom("캘린더 채팅 바로 가기", "클릭 후 보조키 2개 이상 + 글쇠 1개 입력 (Esc 취소 · Backspace 지우기)", chatHkCtrl),
            chatHkHint,
            RowLink("기타 단축키 설정", "앞으로·숨김·새 일정·잠금 단축키를 바꿉니다", "바꾸기", () => { EditHotkeys(); RefreshKeycaps(); }),
            new Border { BorderBrush = LineBrush, Child = keycaps }));

        page.Children.Add(IconCard(ModernUi.Icons.GroupSettings, "시작 및 휴일 데이터",
            RowToggle("Windows 시작 시 자동 실행", "컴퓨터를 켤 때 달력을 자동으로 시작합니다", _h.GetAutoStart, v => { if (!_syncing) _h.SetAutoStart(v); }),
            RowLink("휴일 데이터 편집", "임시공휴일 등을 직접 추가·수정하는 파일을 엽니다 (holidays.json)", "열기", _h.EditHolidays),
            RowLink("휴일 데이터 다시 읽기", "수정한 휴일 파일을 달력에 즉시 반영합니다", "실행", _h.ReloadHolidays)));
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

        page.Children.Add(IconCard(ModernUi.Icons.Sparkles, "AI 동작 방식 · 내 PC 안심 모드",
            AiFlowDiagram(),
            RowToggle("AI 기능 사용", "꺼져 있으면 AI가 동작하지 않으며, 켜면 '빠른 입력'에서 복잡한 문장을 해석해 제안합니다",
                () => Cur().Enabled, v => Change(s => s with { Enabled = v }))));

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

        // Other model pickers (the embedding model) register here so the one connection test fills them all.
        Action<IReadOnlyList<string>>? onModelsFound = null;
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
                onModelsFound?.Invoke(report.Models);
            }
        }

        page.Children.Add(IconCard(ModernUi.Icons.Cpu, "AI 서버 연결",
            RowCombo("AI 서버 종류", "Ollama 또는 OpenAI 호환 서버 (LM Studio · llama.cpp 등)",
                [("Ollama", AiProvider.Ollama), ("OpenAI 호환 (LM Studio 등)", AiProvider.OpenAiCompatible)],
                () => Cur().Provider,
                v => Change(s => s with
                {
                    Provider = v, Model = "",
                    Endpoint = s.Endpoint == AiSettings.DefaultEndpoint(s.Provider) ? AiSettings.DefaultEndpoint(v) : s.Endpoint,
                }), width: 220),
            RowCustom("서버 주소", "기본값은 내 PC (127.0.0.1 · localhost) 전용", endpoint),
            endpointNote,
            RowCustom("모델", "목록 선택 또는 직접 입력 (예: gemma4:e2b)", model),
            RowLink("연결 시험", "서버 연결 및 설치된 모델 목록 불러오기", "시험하기", () => _ = RunTest()),
            RowCustom("시험 결과", null, result)));

        if (ai.Plus is { } plus)
        {
            // ---- meaning search: a second model that turns each to-do and event into numbers, so that "병원 갔던 날" finds "치과 정기검진"
            var embed = new ComboBox { Width = 230, MinHeight = 28, IsEditable = true, VerticalContentAlignment = VerticalAlignment.Center };
            embed.SelectionChanged += (_, _) => { if (!_syncing && embed.SelectedItem is string m && m != Cur().EmbedModel) Change(s => s with { EmbedModel = m }); };
            embed.LostFocus += (_, _) => { if (!_syncing && embed.Text.Trim() != Cur().EmbedModel) Change(s => s with { EmbedModel = embed.Text.Trim() }); };
            _sync.Add(() => { if (!embed.IsKeyboardFocusWithin) embed.Text = Cur().EmbedModel; });
            onModelsFound = models =>
            {
                static bool LooksLikeEmbedding(string m) =>
                    new[] { "embed", "bge", "e5", "minilm", "nomic", "gte", "arctic", "mxbai", "snowflake" }
                        .Any(k => m.Contains(k, StringComparison.OrdinalIgnoreCase));
                string keep = Cur().EmbedModel;
                embed.Items.Clear();
                foreach (string name in models.Where(LooksLikeEmbedding).Concat(models.Where(x => !LooksLikeEmbedding(x)))) embed.Items.Add(name);
                embed.Text = keep;
            };
            var indexState = new TextBlock { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Right };
            _sync.Add(() => indexState.Text = plus.IndexStatus());
            async Task Build()
            {
                indexState.Text = "색인을 만드는 중… (처음에는 모델을 불러오느라 시간이 걸립니다)";
                string buildResult;
                try { buildResult = await plus.BuildIndex(); } catch (Exception ex) { buildResult = ex.Message; }
                indexState.Text = buildResult;
            }
            page.Children.Add(IconCard(ModernUi.Icons.Search, "의미 검색",
                RowToggle("의미 검색 사용", "단어가 달라도 뜻이 비슷한 일정·할 일을 함께 찾습니다 (AI 기능 켜짐 필요)",
                    () => Cur().SemanticSearch, v => Change(s => s with { SemanticSearch = v })),
                RowCustom("임베딩 모델", "예: bge-m3 (한국어 지원) · 모델 변경 시 색인 재생성", embed),
                RowCustom("색인 상태", null, indexState),
                RowLink("색인 만들기·갱신", "추가·수정한 항목만 빠르게 반영", "실행", () => _ = Build()),
                RowLink("색인 지우기", "파생 데이터만 삭제 (원본 일정은 유지)", "지우기", () => { plus.ClearIndex(); SyncAll(); })));

            // ---- the morning briefing (needs no AI; the model only rephrases it, if asked to)
            var b = plus.GetBriefing;
            void ChangeB(Func<BriefingSettings, BriefingSettings> f) { if (_syncing) return; plus.SetBriefing(f(b())); SyncAll(); }
            page.Children.Add(IconCard(ModernUi.Icons.Briefing, "아침 브리핑",
                RowToggle("브리핑을 자동으로 띄우기", "정한 시각~18시 사이 하루 한 번 오늘 요약 팝업 표시",
                    () => b().Auto, v => ChangeB(s => s with { Auto = v })),
                RowCombo("띄우는 시각", "이 시각 이후 처음 앱이 켜져 있을 때", [("7시", 7), ("8시", 8), ("9시", 9), ("10시", 10), ("11시", 11), ("12시", 12)], () => b().Hour, v => ChangeB(s => s with { Hour = v })),
                RowToggle("평일(공휴일 제외)에만", null, () => b().WorkdaysOnly, v => ChangeB(s => s with { WorkdaysOnly = v })),
                RowToggle("AI 로 문장 다듬기", "자동 표시 시 AI로 자연스럽게 요약 (오류 시 원본 유지)",
                    () => b().UseLlm, v => ChangeB(s => s with { UseLlm = v }))));
        }

        var keepRow = RowCombo("모델을 메모리에 유지", "미사용 시 AI 서버가 모델을 내리는 시간 (Ollama)",
            [("바로 내림", "0"), ("5분", "5m"), ("30분", "30m"), ("2시간", "2h"), ("계속", "-1")], () => Cur().KeepAlive, v => Change(s => s with { KeepAlive = v }));
        Gate(keepRow, () => Cur().Provider == AiProvider.Ollama);

        var advRows = new List<UIElement>
        {
            RowCombo("응답 기다리는 시간", "첫 모델 로딩 시 시간이 더 걸릴 수 있습니다",
                [("30초", 30), ("1분", 60), ("2분", 120), ("5분", 300)], () => Cur().TimeoutSeconds, v => Change(s => s with { TimeoutSeconds = v })),
            keepRow,
            RowToggle("다른 컴퓨터의 주소 허용", "⚠ 켜면 일정·메모가 외부 https:// 서버로 전송됩니다",
                () => Cur().AllowRemote, v => Change(s => s with { AllowRemote = v })),
        };
        if (ai.Accuracy is { } accuracy)
        {
            var rules = new TextBlock { Foreground = MutedBrush, TextAlignment = TextAlignment.Right };
            var llm = new TextBlock { Foreground = MutedBrush, TextAlignment = TextAlignment.Right };
            _sync.Add(() =>
            {
                var s = accuracy();
                rules.Text = AccuracySummary.Describe(s.RulesOk, s.RulesBad);
                llm.Text = AccuracySummary.Describe(s.LlmOk, s.LlmBad);
            });
            advRows.Add(RowCustom("규칙 해석 정확도", "빠른 입력에서 누른 '맞음/틀림' 통계 (이 PC에만 저장)", rules));
            advRows.Add(RowCustom("AI 해석 정확도", null, llm));
            advRows.Add(RowLink("정확도 기록 지우기", null, "지우기", () => { ai.ResetAccuracy?.Invoke(); SyncAll(); }));
        }
        page.Children.Add(IconCard(ModernUi.Icons.GroupSettings, "고급 설정 및 정확도", advRows.ToArray()));
        return page;
    }

    private UIElement SyncTab()
    {
        var page = new StackPanel();
        if (_h.Git is not { } git)
        {
            page.Children.Add(Card("Git 저장소 동기화", RowText("동기화", "이 화면에서는 동기화 설정을 바꿀 수 없습니다.")));
            return page;
        }
        GitSyncConfig Cur() => git.Get();
        void Change(Func<GitSyncConfig, GitSyncConfig> f) { if (_syncing) return; git.Set(f(Cur())); SyncAll(); }

        TextBox Field(Func<GitSyncConfig, string> get, Func<GitSyncConfig, string, GitSyncConfig> set, double width)
        {
            var box = new TextBox { Width = width, MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
            void Commit() { if (!_syncing && box.Text.Trim() != get(Cur())) Change(c => set(c, box.Text.Trim())); }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            _sync.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = get(Cur()); });
            return box;
        }
        var urlNote = new TextBlock { Foreground = Frozen(Color.FromRgb(0xC0, 0x39, 0x2B)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(14, 0, 14, 8) };
        var url = Field(c => c.Url, (c, v) =>
        {
            bool ok = v.Length == 0 || DeskCal.Sync.GitCalendarSync.IsValidUrl(v);
            urlNote.Text = ok ? "" : "주소 형식이 올바르지 않습니다. https://github.com/아이디/저장소.git 처럼 입력하세요.";
            urlNote.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            return ok ? c with { Url = v } : c;
        }, 300);
        var folderNote = new TextBlock { Foreground = Frozen(Color.FromRgb(0xC0, 0x39, 0x2B)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(14, 0, 14, 8) };
        var folder = Field(c => c.Folder, (c, v) =>
        {
            bool ok = DeskCal.Sync.GitCalendarSync.IsValidFolder(v);
            folderNote.Text = ok ? "" : "폴더 이름에는 / \\ : * ? \" < > | 를 쓸 수 없고 . 으로 시작할 수 없습니다.";
            folderNote.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            return ok ? c with { Folder = v } : c;
        }, 200);

        var status = new TextBlock { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Right };
        _sync.Add(() => status.Text = git.Status());
        bool busy = false;
        async Task Run()
        {
            if (busy) return;
            if (git.Get().Url.Length == 0) { status.Text = "저장소 주소를 먼저 입력하세요."; return; }
            busy = true;
            status.Text = "동기화하는 중… (처음에는 저장소를 받아오느라 시간이 걸립니다)";
            try { await git.RunNow(); } finally { busy = false; }
            status.Text = git.Status();
        }

        page.Children.Add(Card("처음 사용하는 방법",
            SyncFlowDiagram(),
            GuideStep("1", ModernUi.Icons.Terminal, "준비물 (최초 1회)",
                "Git for Windows 설치 → 비공개(Private) 저장소 생성 → 명령 창에서 git clone <주소> 로 1회 로그인"),
            GuideStep("2", ModernUi.Icons.Link, "저장소 연결",
                "아래 '저장소 주소'에 주소를 붙여 넣고 Enter (지정 폴더만 사용하며 저장소의 다른 파일은 건드리지 않음)"),
            GuideStep("3", ModernUi.Icons.RepeatPlan, "동기화 및 다른 PC 연결",
                "'지금 동기화' 클릭 시 양쪽 일정을 합쳐 올림 · 다른 PC에서도 같은 주소 입력 후 동기화하면 자동 병합"),
            GuideStep("4", ModernUi.Icons.Shield, "안전 백업 및 문제 해결",
                "변경 전 항상 backup 폴더에 자동 백업 · 오류 시 아래 '마지막 결과' 안내 확인 (주소 입력 전엔 전송 없음)",
                muted: true)));

        page.Children.Add(Card("저장소 연결",
            RowCustom("저장소 주소", "비어 있으면 동기화하지 않습니다. 예: https://github.com/아이디/저장소.git", url),
            urlNote,
            RowCustom("폴더", "저장소 안에서 캘린더가 쓰는 폴더. 다른 폴더는 건드리지 않습니다", folder),
            folderNote,
            RowCombo("자동 동기화", "프로그램이 켜진 동안 이 간격으로 받고 올립니다 (켤 때도 한 번)",
                [("끔", 0), ("5분", 5), ("15분", 15), ("30분", 30), ("1시간", 60)], () => Cur().IntervalMinutes, v => Change(c => c with { IntervalMinutes = v })),
            RowLink("지금 동기화", "받아서 합치고 올립니다. 이 컴퓨터의 내용을 바꾸기 전에는 데이터 폴더의 backup 에 복사본을 남깁니다", "실행", () => _ = Run()),
            RowCustom("마지막 결과", null, status),
            RowLink("작업 폴더", "이 프로그램이 받아 둔 저장소 사본", "열기", git.OpenFolder)));
        page.Children.Add(Card("주의",
            RowText("개인정보", "일정 내용이 그대로 저장소에 올라갑니다. 저장소는 반드시 비공개로 두세요. 콘텐츠 잠금 중에는 자동 동기화를 하지 않습니다."),
            RowText("충돌", "같은 항목을 두 컴퓨터에서 동시에 고치면 나중에 고친 쪽이 남고, 덮어쓴 글은 기록(이력)에 남습니다.")));
        return page;
    }

    private static Border AiFlowDiagram() => FlowDiagram(
        "기본 설정에서는 내 PC 안에서만 동작하며, AI는 제안만 하고 직접 확인해야 달력에 추가됩니다.",
        (ModernUi.Icons.Bolt, "내 입력 · 달력", "빠른 입력 · 검색", false),
        ModernUi.Icons.ArrowRight,
        (ModernUi.Icons.Cpu, "로컬 AI (내 PC)", "Ollama · LM Studio", true),
        (ModernUi.Icons.UserCheck, "확인 후 반영", "외부 전송 없음", false));

    private static Border SyncFlowDiagram() => FlowDiagram(
        "할 일 · 일정 · 디데이 · 메모 · 시간표 · 휴일 설정을 내 비공개 저장소로 안전하게 합칩니다.",
        (ModernUi.Icons.Monitor, "내 PC", "M-DeskCal", false),
        ModernUi.Icons.ArrowsLeftRight,
        (ModernUi.Icons.Cloud, "비공개 Git 저장소", "전용 폴더만 사용", true),
        (ModernUi.Icons.Monitor, "다른 PC", "M-DeskCal", false));

    /// <summary>Shared visual 3-node diagram helper used in AI and Sync guide cards.</summary>
    private static Border FlowDiagram(
        string caption,
        (string icon, string label, string sub, bool accent) left,
        string arrowIcon,
        (string icon, string label, string sub, bool accent) mid,
        (string icon, string label, string sub, bool accent) right)
    {
        Border Node((string icon, string label, string sub, bool accent) d)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
                Background = d.accent ? ModernUi.AccentSoftBrush : ModernUi.CardBrush,
                BorderBrush = d.accent ? ModernUi.AccentBorderBrush : LineBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
                Child = ModernUi.FlatIcon(d.icon, d.accent ? AccentBrush : MutedBrush, 15, 1.9),
            });
            stack.Children.Add(new TextBlock
            {
                Text = d.label, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = ModernUi.InkBrush, HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = d.sub, FontSize = 10.5, Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
            });
            return new Border
            {
                Background = d.accent ? ModernUi.AccentSoftBrush : ModernUi.CardBrush,
                BorderBrush = d.accent ? ModernUi.AccentBorderBrush : LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = stack,
            };
        }

        UIElement Arrow()
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            sp.Children.Add(ModernUi.FlatIcon(arrowIcon, AccentBrush, 16, 1.9));
            return sp;
        }

        var flow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var n0 = Node(left); var a0 = Arrow(); var n1 = Node(mid); var a1 = Arrow(); var n2 = Node(right);
        Grid.SetColumn(n0, 0); Grid.SetColumn(a0, 1); Grid.SetColumn(n1, 2); Grid.SetColumn(a1, 3); Grid.SetColumn(n2, 4);
        flow.Children.Add(n0); flow.Children.Add(a0); flow.Children.Add(n1); flow.Children.Add(a1); flow.Children.Add(n2);

        var wrap = new StackPanel();
        wrap.Children.Add(flow);
        wrap.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 11.5, Foreground = MutedBrush, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = ModernUi.CardAltBrush,
            BorderBrush = LineBrush,
            Padding = new Thickness(14, 12, 14, 10),
            Child = wrap,
        };
    }

    /// <summary>Compact numbered step row with a flat icon badge and concise summary.</summary>
    private static Border GuideStep(string step, string iconData, string heading, string text, bool muted = false)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
            Background = muted ? ModernUi.CardAltBrush : ModernUi.AccentSoftBrush,
            BorderBrush = muted ? LineBrush : ModernUi.AccentBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 1, 11, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = ModernUi.FlatIcon(iconData, muted ? MutedBrush : AccentBrush, 14, 1.9),
        };
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var p = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        titleRow.Children.Add(new Border
        {
            Background = muted ? ModernUi.CardAltBrush : AccentBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = step, FontSize = 10.5, FontWeight = FontWeights.Bold,
                Foreground = muted ? MutedBrush : Brushes.White,
            },
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = heading, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = ModernUi.InkBrush, VerticalAlignment = VerticalAlignment.Center,
        });
        p.Children.Add(titleRow);
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap, LineHeight = 17,
        });
        Grid.SetColumn(p, 1);
        row.Children.Add(p);

        return new Border { Padding = new Thickness(14, 9, 14, 9), BorderBrush = LineBrush, Child = row };
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

    private static SolidColorBrush Frozen(Color c) => UiColors.Frozen(c);

    private static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel();
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    /// <summary>A white panel with a blue-barred heading; rows are separated by hairlines.</summary>
    private static UIElement Card(string title, params UIElement[] rows) => CardCore(null, title, rows);

    /// <summary>A white panel with a flat vector icon badge next to the heading.</summary>
    private static UIElement IconCard(string iconData, string title, params UIElement[] rows) => CardCore(iconData, title, rows);

    private static UIElement CardCore(string? iconData, string title, UIElement[] rows)
    {
        var inner = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 14, 9) };
        if (iconData is not null)
        {
            var icon = ModernUi.FlatIcon(iconData, AccentBrush, 14, 2.0);
            icon.Margin = new Thickness(0, 0, 7, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(icon);
        }
        else
        {
            head.Children.Add(new Border { Width = 3, Height = 14, Background = AccentBrush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0) });
        }
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
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
                Background = Frozen(UiColors.Parse(color, Colors.White)), BorderBrush = Frozen(Color.FromRgb(0xB8, 0xBE, 0xC8)), BorderThickness = new Thickness(1), ToolTip = color,
            };
            sw.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; set(color); };
            grid.Children.Add(sw);
        }
        var custom = new TextBlock { Text = "사용자 지정…", Foreground = AccentBrush, Cursor = Cursors.Hand, Margin = new Thickness(5, 6, 0, 2) };
        custom.MouseLeftButtonUp += (_, _) =>
        {
            popup.IsOpen = false;
            var p = UiColors.Parse(get(), Colors.White);
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
            swatch.Background = empty ? Brushes.Transparent : Frozen(UiColors.Parse(v, Colors.White));
            hex.Text = empty ? "없음" : v.ToUpperInvariant();
        });
        var wrap = new StackPanel { Orientation = Orientation.Horizontal };
        wrap.Children.Add(button);
        wrap.Children.Add(popup);
        return RowCustom(label, null, wrap);
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
