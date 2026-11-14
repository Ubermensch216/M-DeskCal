using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskCal.Core;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>Shared pieces of the timetable views.</summary>
internal static class TimetableUi
{
    public static readonly string[] DayNames = ["일", "월", "화", "수", "목", "금", "토"];

    public static string SlotLabel(PeriodSlot s) => $"{s.Number}교시\n{s.Start:HH\\:mm}–{s.End:HH\\:mm}";

    public static SolidColorBrush? Tint(string? hex, byte alpha = 0x66)
    {
        if (UiColors.TryParse(hex) is not { } c) return null;
        c.A = alpha;
        return new SolidColorBrush(c);
    }
}

/// <summary>
/// Editor for the class timetable: the period layout (generated times) on top, the weekly grid below. A square holds a
/// subject on its first line and a room / note on the second. Right-click a square for a colour. Today's column and the
/// period in progress are highlighted.
/// </summary>
internal sealed class TimetableWindow : Window
{
    private static TimetableWindow? _open;

    private readonly TimetableRepository _repo;
    private readonly SettingsStore _settings;
    private readonly Action _changed;
    private TimetableConfig _config;

    private readonly TextBox _periods = Field(46), _first = Field(60), _class = Field(46), _break = Field(46), _lunchAfter = Field(46), _lunch = Field(46);
    private readonly CheckBox _weekend = new() { Content = "주말 표시", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, MinHeight = 18, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Grid _grid = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<(DayOfWeek Day, int Period, Border Cell, TextBox Box)> _boxes = [];

    private static TextBox Field(double width) => new() { Width = width, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(2, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };

    public static void ShowOrActivate(TimetableRepository repo, SettingsStore settings, Action changed)
    {
        if (_open is { } w) { w.Show(); w.Activate(); return; }
        _open = new TimetableWindow(repo, settings, changed);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    /// <summary>Dev aid: build the window without showing it and render it.</summary>
    internal static void Snapshot(TimetableRepository repo, SettingsStore settings, string path)
    {
        var w = new TimetableWindow(repo, settings, () => { });
        DevHarness.RenderToPng((FrameworkElement)w.Content, path, 800, 600, System.Windows.Media.Colors.White);
    }

    private TimetableWindow(TimetableRepository repo, SettingsStore settings, Action changed)
    {
        _repo = repo; _settings = settings; _changed = changed;
        _config = repo.LoadConfig(settings);
        Title = "M-DeskCal 시간표"; Width = 820; Height = 640; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13;

        var cfg = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void Add(string label, TextBox box) { cfg.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); cfg.Children.Add(box); }
        Add("교시 수", _periods); Add("첫 수업", _first); Add("수업(분)", _class); Add("쉬는 시간(분)", _break); Add("점심 직전 교시", _lunchAfter); Add("점심(분)", _lunch);
        cfg.Children.Add(_weekend);
        var apply = Dlg.Btn("적용", (_, _) => ApplyConfig());
        apply.Margin = new Thickness(10, 0, 0, 0);
        cfg.Children.Add(apply);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(Dlg.Btn("모두 지우기", (_, _) => ClearAll()));
        bottom.Children.Add(new TextBlock { Text = "칸: 첫 줄 = 과목, 둘째 줄 = 장소/메모 · 우클릭 = 색 · 칸에서 나오면 저장", Foreground = Brushes.Gray, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });

        var dock = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(cfg, Dock.Top); DockPanel.SetDock(_error, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(cfg); dock.Children.Add(_error); dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;

        _clock.Tick += (_, _) => Highlight();
        Loaded += (_, _) => _clock.Start();
        Closed += (_, _) => _clock.Stop();

        FillConfig();
        Rebuild();
    }

    private void FillConfig()
    {
        _periods.Text = _config.Periods.ToString(CultureInfo.InvariantCulture);
        _first.Text = _config.FirstStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        _class.Text = _config.ClassMinutes.ToString(CultureInfo.InvariantCulture);
        _break.Text = _config.BreakMinutes.ToString(CultureInfo.InvariantCulture);
        _lunchAfter.Text = _config.LunchAfter.ToString(CultureInfo.InvariantCulture);
        _lunch.Text = _config.LunchMinutes.ToString(CultureInfo.InvariantCulture);
        _weekend.IsChecked = _config.ShowWeekend;
    }

    private void ApplyConfig()
    {
        bool Num(TextBox b, int min, int max, out int v) => int.TryParse(b.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;
        if (!Num(_periods, 1, 12, out int periods)) { _error.Text = "교시 수는 1~12 사이 숫자입니다."; return; }
        if (!TimeOnly.TryParseExact(_first.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)) { _error.Text = "첫 수업 시각을 09:00 형식으로 입력하세요."; return; }
        if (!Num(_class, 10, 180, out int cls)) { _error.Text = "수업 시간은 10~180분입니다."; return; }
        if (!Num(_break, 0, 60, out int brk)) { _error.Text = "쉬는 시간은 0~60분입니다."; return; }
        if (!Num(_lunchAfter, 0, periods - 1, out int lunchAfter)) { _error.Text = $"점심 시간 전 교시는 0(없음)~{periods - 1} 입니다."; return; }
        if (!Num(_lunch, 0, 180, out int lunch)) { _error.Text = "점심 시간은 0~180분입니다."; return; }

        _error.Text = "";
        _config = new TimetableConfig { Periods = periods, FirstStart = first, ClassMinutes = cls, BreakMinutes = brk, LunchAfter = lunchAfter, LunchMinutes = lunch, ShowWeekend = _weekend.IsChecked == true }.Sanitized();
        _repo.SaveConfig(_settings, _config);
        FillConfig();
        Rebuild();
        _changed();
    }

    private void ClearAll()
    {
        if (MessageBox.Show(this, "시간표의 모든 칸을 지울까요?", "시간표", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _repo.Clear();
        Rebuild();
        _changed();
    }

    private void Rebuild()
    {
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear(); _boxes.Clear();
        var days = TimetableSchedule.Days(_config);
        var slots = TimetableSchedule.Slots(_config);
        var cells = _repo.GetCells().ToDictionary(c => (c.Day, c.Period));

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        foreach (var _ in days) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < days.Length; c++) Place(Label(TimetableUi.DayNames[(int)days[c]], true), 0, c + 1, tag: days[c]);

        int row = 1;
        foreach (var slot in slots)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto), MinHeight = 52 });
            Place(Label(TimetableUi.SlotLabel(slot), true), row, 0, tag: slot.Number);
            for (int c = 0; c < days.Length; c++)
            {
                var day = days[c];
                cells.TryGetValue((day, slot.Number), out var cell);
                var box = new TextBox
                {
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(4), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Text = cell is null ? "" : cell.Room.Length > 0 ? cell.Subject + "\n" + cell.Room : cell.Subject, VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = cell?.Color,
                };
                var border = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5), Child = box, Background = TimetableUi.Tint(cell?.Color) ?? Brushes.White };
                int period = slot.Number;
                box.LostFocus += (_, _) => Save(day, period, box, border);
                box.ContextMenu = ColorMenu(day, period, box, border);
                _boxes.Add((day, period, border, box));
                Place(border, row, c + 1);
            }
            row++;
            if (slot.Number == _config.LunchAfter && slot.Number < slots.Count)
            {
                _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                var lunch = new TextBlock { Text = "점심 시간", Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(lunch, row); Grid.SetColumn(lunch, 0); Grid.SetColumnSpan(lunch, days.Length + 1); _grid.Children.Add(lunch);
                row++;
            }
        }
        Highlight();
    }

    private void Place(UIElement e, int row, int col, object? tag = null)
    {
        if (e is FrameworkElement fe) fe.Tag = tag;
        Grid.SetRow(e, row); Grid.SetColumn(e, col);
        _grid.Children.Add(e);
    }

    private static Border Label(string text, bool header) => new()
    {
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0xE1, 0xF2)), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5), Padding = new Thickness(4, 6, 4, 6),
        Child = new TextBlock { Text = text, TextAlignment = TextAlignment.Center, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12 },
    };

    private void Save(DayOfWeek day, int period, TextBox box, Border border)
    {
        var lines = box.Text.Replace("\r\n", "\n").Split('\n', 2);
        string subject = lines[0].Trim(), room = lines.Length > 1 ? lines[1].Replace("\n", " ").Trim() : "";
        string? color = box.Tag as string;
        _repo.SetCell(day, period, subject, room, subject.Length == 0 && room.Length == 0 ? null : color);
        if (subject.Length == 0 && room.Length == 0) { box.Tag = null; border.Background = Brushes.White; }
        _changed();
    }

    private ContextMenu ColorMenu(DayOfWeek day, int period, TextBox box, Border border)
    {
        var menu = new ContextMenu();
        foreach (var (name, hex) in MainWindow.Palette)
        {
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) =>
            {
                box.Tag = hex;
                border.Background = TimetableUi.Tint(hex) ?? Brushes.White;
                Save(day, period, box, border);
            };
            menu.Items.Add(mi);
        }
        return menu;
    }

    /// <summary>Mark today's column and the period in progress (or the next one).</summary>
    private void Highlight()
    {
        var now = DateTime.Now;
        int? period = TimetableSchedule.NextOrCurrent(_config, TimeOnly.FromDateTime(now));
        foreach (var child in _grid.Children.OfType<Border>())
        {
            if (child.Tag is DayOfWeek d && child.Child is TextBlock dt) dt.Foreground = d == now.DayOfWeek ? Brushes.Firebrick : Brushes.Black;
            if (child.Tag is int p && child.Child is TextBlock pt) pt.Foreground = p == period ? Brushes.Firebrick : Brushes.Black;
        }
    }
}

/// <summary>
/// The timetable on the desktop (read only): pinned above the wallpaper like the calendar and the D-day cards, with today's
/// column and the current period highlighted. Edit it in the timetable window; move it in edit mode.
/// </summary>
internal sealed class TimetableWidgetWindow : Window
{
    private readonly Border _frame;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _x = 80, _y = 400, _tick;
    private bool _editMode;
    private TimetableConfig _config = new();
    private IReadOnlyList<TimetableCell> _cells = [];
    private BoardSettings _settings = new();

    public event Action<int, int>? Moved;

    public TimetableWidgetWindow()
    {
        Title = "M-DeskCal 시간표 위젯";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; UseLayoutRounding = true;
        _frame = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(14) };
        Content = _frame;
        _timer.Tick += (_, _) =>
        {
            if (!_editMode && IsVisible && !IsActive) DesktopHost.Repin(this);
            if (++_tick % 30 == 0 && IsVisible) Rebuild(_config, _cells, _settings);     // moves the highlight along
        };
    }

    public void SetPosition(int x, int y) { _x = x; _y = y; }

    public void Update(TimetableConfig config, IReadOnlyList<TimetableCell> cells, BoardSettings s, bool allowed)
    {
        if (!allowed) { if (IsVisible) Hide(); return; }
        Rebuild(config, cells, s);
        if (!IsVisible) { Show(); UpdateLayout(); PinNow(); _timer.Start(); }
        else if (!_editMode) Dispatcher.BeginInvoke(PinNow);
    }

    internal void Rebuild(TimetableConfig config, IReadOnlyList<TimetableCell> cells, BoardSettings s)
    {
        (_config, _cells, _settings) = (config, cells, s);
        var bg = UiColors.Parse(s.BackgroundColor, System.Windows.Media.Color.FromRgb(0x10, 0x18, 0x20));
        bg.A = (byte)Math.Round(Math.Max(0.35, s.BackgroundOpacity) * 255);
        var text = new SolidColorBrush(UiColors.Parse(s.TextColor, Colors.White));
        var accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x57));
        var font = new FontFamily(s.FontFamily);

        var days = TimetableSchedule.Days(config);
        var slots = TimetableSchedule.Slots(config);
        var lookup = cells.ToDictionary(c => (c.Day, c.Period));
        var now = DateTime.Now;
        int? current = TimetableSchedule.NextOrCurrent(config, TimeOnly.FromDateTime(now));

        var grid = new Grid { Margin = new Thickness(10, 8, 10, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var _ in days) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock Cell(string t, double size, Brush fg, bool bold = false, double opacity = 1) =>
            new() { Text = t, Foreground = fg, FontFamily = font, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Opacity = opacity, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 2, 3, 2) };
        void Put(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); grid.Children.Add(e); }

        for (int c = 0; c < days.Length; c++) Put(Cell(TimetableUi.DayNames[(int)days[c]], s.FontSize - 1, days[c] == now.DayOfWeek ? accent : text, true), 0, c + 1);
        int row = 1;
        foreach (var slot in slots)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(Cell($"{slot.Number}\n{slot.Start:HH\\:mm}", Math.Max(9, s.FontSize - 4), slot.Number == current ? accent : text, slot.Number == current, 0.8), row, 0);
            for (int c = 0; c < days.Length; c++)
            {
                lookup.TryGetValue((days[c], slot.Number), out var cell);
                var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                if (cell is not null)
                {
                    inner.Children.Add(Cell(cell.Subject, Math.Max(10, s.FontSize - 2), text, true));
                    if (cell.Room.Length > 0) inner.Children.Add(Cell(cell.Room, Math.Max(9, s.FontSize - 5), text, false, 0.7));
                }
                bool hot = days[c] == now.DayOfWeek && slot.Number == current;
                var b = new Border
                {
                    Margin = new Thickness(1), CornerRadius = new CornerRadius(6), Child = inner, MinHeight = 34,
                    Background = TimetableUi.Tint(cell?.Color, 0x88) ?? (cell is null ? Brushes.Transparent : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))),
                    BorderBrush = hot ? accent : Brushes.Transparent, BorderThickness = new Thickness(hot ? 2 : 0),
                };
                Put(b, row, c + 1);
            }
            row++;
        }
        _frame.Background = new SolidColorBrush(bg);
        _frame.Child = grid;
    }

    private void PinNow()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        DesktopHost.Pin(this, new DesktopHost.RectInt(_x, _y, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
    }

    public void SetEditMode(bool on)
    {
        if (on == _editMode) return;
        _editMode = on;
        if (on) { if (!IsVisible) return; DesktopHost.Float(this); _frame.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x57)); }
        else
        {
            _frame.BorderBrush = Brushes.Transparent;
            if (!IsVisible) return;
            var r = DesktopHost.GetScreenRect(this);
            (_x, _y) = (r.X, r.Y);
            PinNow();
            Moved?.Invoke(_x, _y);
        }
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editMode) DragMove();
    }

    internal void SaveSnapshot(string path) => DevHarness.RenderToPng(_frame, path);}
