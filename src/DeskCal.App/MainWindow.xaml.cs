using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ContextMenu = System.Windows.Controls.ContextMenu;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using WpfBrush = System.Windows.Media.Brush;

namespace DeskCal.App;

/// <summary>The calendar board window. Geometry comes from <see cref="BoardLayout"/>; this class only draws it.</summary>
public partial class MainWindow : Window
{
    private bool _editMode;                                         // "edit position/size" mode
    private DesktopHost.RectInt _rect = new(200, 150, 640, 480);    // physical pixels
    private uint _taskbarCreated;
    private bool _attached;

    private BoardSettings _settings = new();
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _renderedToday;

    private ItemRepository? _items;
    private readonly Dictionary<DateOnly, Border> _cells = [];
    private DateOnly? _editingDay;

    private EventRepository? _events;
    private EventSeriesService? _series;
    private ReminderRepository? _reminders;
    private HashSet<string> _itemsWithReminder = [];

    /// <summary>Raised when reminders were added, changed or removed, so the scheduler can re-arm its timer.</summary>
    internal event Action? RemindersChanged;

    private static readonly (string Name, string? Hex)[] Palette =
    [
        ("기본", null), ("빨강", "#FF6B6B"), ("주황", "#FFA94D"), ("노랑", "#FFE066"),
        ("초록", "#69DB7C"), ("파랑", "#74C0FC"), ("보라", "#B197FC"),
    ];

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
        };
        Render();
    }

    // ---- public surface ----------------------------------------------------

    internal BoardSettings Settings => _settings;

    internal void SetItems(ItemRepository items) { _items = items; Render(); }

    internal void SetEvents(EventRepository events, EventSeriesService series, ReminderRepository reminders)
    {
        _events = events; _series = series; _reminders = reminders;
        Render();
    }

    private KoreanDays? _calendar;
    internal void SetCalendar(KoreanDays calendar) { _calendar = calendar; Render(); }

    /// <summary>Apply new settings. Switching display mode re-centres on today.</summary>
    internal void ApplySettings(BoardSettings settings)
    {
        var s = settings.Sanitized();
        if (s.Mode != _settings.Mode) _anchor = Today();
        _settings = s;
        Render();
    }

    internal void GoToday() { _anchor = Today(); Render(); }
    internal void GoTo(DateOnly date) { _anchor = date; Render(); }

    internal void SetRect(DesktopHost.RectInt rect) => _rect = rect;
    public void SetPosition(int x, int y) => _rect = new(x, y, _rect.Width, _rect.Height);

    /// <summary>Raised after the user finishes edit mode, with the final physical-pixel rect.</summary>
    internal event Action<DesktopHost.RectInt>? RectCommitted;

    /// <summary>Dev aid: render the board (over a flat stand-in wallpaper) to a PNG without touching the screen.</summary>
    internal void SaveSnapshot(string path)
    {
        var root = (FrameworkElement)Content;
        double w = 640, h = 480;
        root.Measure(new System.Windows.Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x5F, 0x7A)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public string Diagnostics() =>
        $"attached={_attached} edit={_editMode}\nrect={_rect}\nanchor={_anchor} mode={_settings.Mode}\n{DesktopHost.LastDiagnostics}";

    // ---- desktop pinning ---------------------------------------------------

    public void AttachToDesktop()
    {
        _attached = DesktopHost.Pin(this, _rect);
        Log($"pin -> {_attached} {DesktopHost.LastDiagnostics}");
        _timer ??= StartTimer();
    }

    private System.Windows.Threading.DispatcherTimer? _timer;

    /// <summary>True while the board is shown on the desktop (it can be hidden from the tray menu or by a shortcut).</summary>
    internal bool BoardVisible => IsVisible;

    /// <summary>Show the board and pin it above the desktop. Safe to call when it is already visible.</summary>
    internal void ShowBoard()
    {
        if (!IsVisible) Show();
        if (!_editMode) AttachToDesktop();
    }

    internal void HideBoard()
    {
        if (_editMode) SetEditMode(false);
        if (_editingDay is not null) CommitEditor();
        Hide();
    }

    internal void ToggleBoard() { if (IsVisible) HideBoard(); else ShowBoard(); }

    /// <summary>
    /// Raise the board above the other windows so it can be read or used, and give it the keyboard focus.
    /// The 1 s timer (and deactivation) put it back under the other windows once the user clicks elsewhere.
    /// </summary>
    internal void BringToFront()
    {
        // Do not re-pin when already shown: moving the window behind the others would deactivate it, and the
        // queued deactivation would then send it straight back.
        if (!IsVisible) ShowBoard();
        DesktopHost.Float(this);
        Activate();
        Focus();
    }

    // Windows reorders top-level windows when other apps activate, so keep us just above the desktop.
    // The same tick notices midnight so "today" moves on without user action.
    private System.Windows.Threading.DispatcherTimer StartTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (!_editMode && IsVisible && !IsActive) DesktopHost.Repin(this);
            if (Today() != _renderedToday && _editingDay is null) Render();
        };
        t.Start();
        return t;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_editingDay is not null) CommitEditor();   // never lose typed text when focus moves away
        else if (!_editMode && _attached && IsVisible) DesktopHost.Repin(this);
    }

    public void SetEditMode(bool on)
    {
        if (on == _editMode) return;
        if (on && !IsVisible) ShowBoard();
        if (on && _editingDay is not null) CommitEditor();
        _editMode = on;
        if (on)
        {
            DesktopHost.Float(this);
            Frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57));
            EditHint.Visibility = Grip.Visibility = Visibility.Visible;
            ShowActivated = true;
            Activate();
        }
        else
        {
            _rect = DesktopHost.GetScreenRect(this);
            Frame.BorderBrush = Brushes.Transparent;
            EditHint.Visibility = Grip.Visibility = Visibility.Collapsed;
            AttachToDesktop();
            RectCommitted?.Invoke(_rect);
        }
    }

    // ---- input -------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editMode) DragMove();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_editingDay is null) Navigate(e.Delta > 0 ? -1 : +1);
    }

    private void Navigate(int direction)
    {
        _anchor = BoardLayout.Step(_anchor, direction, _settings);
        Render();
    }

    private void Prev_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; Navigate(-1); }
    private void Next_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; Navigate(+1); }
    private void Today_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; GoToday(); }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(320, Width + e.HorizontalChange);
        Height = Math.Max(240, Height + e.VerticalChange);
    }

    // ---- day editor --------------------------------------------------------

    /// <summary>Open the one-line-per-item editor over the given day's cell.</summary>
    internal void OpenEditor(DateOnly date)
    {
        if (_items is null || _editMode) return;
        if (_editingDay is not null) CommitEditor();
        _editingDay = date;

        var lines = _items.GetByDay(DayKey(date)).Select(i => i.Content);
        EditorTitle.Text = date.ToString("D", CultureInfo.CurrentCulture);
        EditorBox.Text = string.Join("\r\n", lines);
        EditorBox.FontFamily = new FontFamily(_settings.FontFamily);
        EditorBox.FontSize = Math.Max(12, _settings.FontSize - 1);

        Root.UpdateLayout();
        double rw = Root.ActualWidth, rh = Root.ActualHeight;
        var r = _cells.TryGetValue(date, out var cell) && cell.IsLoaded
            ? cell.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight))
            : new Rect(rw / 2 - 130, rh / 2 - 95, 260, 190);
        double w = Math.Min(Math.Max(r.Width, 270), Math.Max(150, rw - 8));
        double h = Math.Min(Math.Max(r.Height + 50, 200), Math.Max(120, rh - 8));
        EditorPanel.Width = w;
        EditorPanel.Height = h;
        EditorPanel.Margin = new Thickness(
            Math.Clamp(r.Left, 4, Math.Max(4, rw - w - 4)),
            Math.Clamp(r.Top, 4, Math.Max(4, rh - h - 4)), 0, 0);
        EditorLayer.Visibility = Visibility.Visible;

        Activate();
        EditorBox.Focus();
        EditorBox.CaretIndex = EditorBox.Text.Length;
        Keyboard.Focus(EditorBox);
    }

    /// <summary>Dev aid (snapshot self-test): the editor's text, as if typed by the user.</summary>
    internal string EditorText { get => EditorBox.Text; set => EditorBox.Text = value; }

    internal void CommitEditor()
    {
        if (_editingDay is not { } day) return;
        var lines = EditorBox.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        _editingDay = null;
        EditorLayer.Visibility = Visibility.Collapsed;
        _items?.ReplaceDay(DayKey(day), lines);
        Render();
        SendBack();
    }

    private void CancelEditor()
    {
        if (_editingDay is null) return;
        _editingDay = null;
        EditorLayer.Visibility = Visibility.Collapsed;
        SendBack();
    }

    // After editing, drop back beneath the other windows instead of covering them.
    private void SendBack()
    {
        if (!_editMode && _attached) DesktopHost.Repin(this);
    }

    private void Scrim_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; CommitEditor(); }

    private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelEditor(); }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; CommitEditor(); }
    }

    // ---- rendering ---------------------------------------------------------

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
    private static string DayKey(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Render()
    {
        var s = _settings;
        var culture = CultureInfo.CurrentCulture;
        _renderedToday = Today();
        var grid = BoardLayout.Build(_anchor, _renderedToday, s);
        var byDay = LoadItems(grid);
        var eventsByDay = LoadEvents(grid);
        _itemsWithReminder = LoadItemReminderIds();
        _cells.Clear();

        // frame: background, opacity, shadow
        var bg = ParseColor(s.BackgroundColor, Color.FromRgb(0x10, 0x18, 0x20));
        bg.A = (byte)Math.Round(s.BackgroundOpacity * 255);
        Frame.Background = new SolidColorBrush(bg);
        Frame.Margin = new Thickness(s.Shadow ? 12 : 0);
        Frame.Effect = s.Shadow ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black } : null;

        var text = Brush(s.TextColor, Colors.White);
        var off = Brush(s.OffDayColor, Color.FromRgb(0xFF, 0x7A, 0x7A));
        var font = new FontFamily(s.FontFamily);

        foreach (var tb in new[] { MonthTitle, PrevText, TodayText, NextText }) { tb.Foreground = text; tb.FontFamily = font; }
        MonthTitle.FontSize = s.FontSize + 7;
        MonthTitle.Text = FormatTitle(grid, culture);

        // grid
        GridHost.Children.Clear();
        GridHost.RowDefinitions.Clear();
        GridHost.ColumnDefinitions.Clear();
        int colOffset = grid.HasWeekNumbers ? 1 : 0;
        if (grid.HasWeekNumbers) GridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < grid.Columns; c++) GridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        GridHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Single-line modes: rows hug their content instead of stretching over the whole window.
        var rowHeight = grid.Rows.Count == 1 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        foreach (var _ in grid.Rows) GridHost.RowDefinitions.Add(new RowDefinition { Height = rowHeight });

        double headerSize = Math.Max(9, s.FontSize - 2);
        for (int c = 0; c < grid.Columns; c++)
        {
            var dow = grid.HeaderDays[c];
            bool isOff = s.ShowOffDays && s.IsOffDay(dow);
            Place(new TextBlock
            {
                Text = culture.DateTimeFormat.GetAbbreviatedDayName(dow),
                Foreground = isOff ? off : text, Opacity = 0.8, FontFamily = font, FontSize = headerSize,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
            }, 0, c + colOffset);
        }

        bool dense = grid.Columns > 7;                // single-line modes: narrow cells
        double dayFont = dense ? Math.Max(9, s.FontSize - 3) : s.FontSize;
        for (int r = 0; r < grid.Rows.Count; r++)
        {
            var row = grid.Rows[r];
            if (grid.HasWeekNumbers)
                Place(new TextBlock
                {
                    Text = row.WeekNumber?.ToString(CultureInfo.InvariantCulture), Foreground = text, Opacity = 0.5,
                    FontFamily = font, FontSize = headerSize, Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                }, r + 1, 0);

            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                byDay.TryGetValue(cell.Date, out var items);
                eventsByDay.TryGetValue(cell.Date, out var events);
                Place(MakeCell(cell, items ?? [], events ?? [], s, text, off, font, dayFont, dense), r + 1, c + colOffset);
            }
        }
    }

    private Dictionary<DateOnly, List<Item>> LoadItems(BoardGrid grid)
    {
        var result = new Dictionary<DateOnly, List<Item>>();
        if (_items is null || grid.Rows.Count == 0) return result;
        var first = grid.Rows[0].Cells[0].Date;
        var last = grid.Rows[^1].Cells[^1].Date;
        foreach (var item in _items.GetRange(DayKey(first), DayKey(last)))
            if (DateOnly.TryParseExact(item.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                (result.TryGetValue(d, out var list) ? list : result[d] = []).Add(item);
        return result;
    }

    private Dictionary<DateOnly, List<EventOccurrence>> LoadEvents(BoardGrid grid)
    {
        var result = new Dictionary<DateOnly, List<EventOccurrence>>();
        if (_events is null || grid.Rows.Count == 0) return result;
        var first = grid.Rows[0].Cells[0].Date;
        var last = grid.Rows[^1].Cells[^1].Date;
        Func<DateOnly, bool>? isDayOff = _calendar is null ? null : _calendar.Holidays.IsDayOff;
        foreach (var occ in EventOccurrences.Expand(_events.GetAllActive(), _events.GetExceptions(), first, last, isDayOff))
            for (var d = occ.Date > first ? occ.Date : first; d <= occ.EndDate && d <= last; d = d.AddDays(1))
                (result.TryGetValue(d, out var list) ? list : result[d] = []).Add(occ);
        return result;
    }

    private HashSet<string> LoadItemReminderIds() =>
        _reminders is null ? [] : _reminders.ListAll().Where(r => r.OwnerType == ReminderOwner.Item).Select(r => r.OwnerId).ToHashSet();

    private UIElement MakeCell(BoardCell cell, List<Item> items, List<EventOccurrence> events, BoardSettings s, WpfBrush text, WpfBrush off,
                               FontFamily font, double size, bool dense)
    {
        var border = new Border
        {
            Margin = new Thickness(s.CellGap / 2.0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,          // transparent (not null) so empty cells are clickable
            ClipToBounds = true,
        };
        if (cell.IsBlank) return border;

        var accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        if (cell.IsToday) border.Background = new SolidColorBrush(dense ? Color.FromArgb(0xB0, accent.R, accent.G, accent.B)
                                                                         : Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        if (dense || s.Mode is DisplayMode.SingleLine or DisplayMode.SingleLineFloating) border.MinHeight = size * 2.4;

        // "M/d" on the 1st only where the view can span months; a fixed month needs no label.
        bool floatingLike = s.Mode is DisplayMode.FloatingMonth or DisplayMode.SingleLineFloating;
        string label = cell.IsMonthStart && floatingLike
            ? $"{cell.Date.Month}/{cell.Date.Day}"
            : cell.Date.Day.ToString(CultureInfo.InvariantCulture);

        var info = _calendar?.Get(cell.Date);
        var holidayBrush = Brush(s.HolidayColor, Color.FromRgb(0xFF, 0x6B, 0x6B));
        bool isHoliday = s.ShowHolidays && info?.IsDayOff == true;
        WpfBrush numberBrush = isHoliday ? holidayBrush : s.ShowOffDays && cell.IsOffDay ? off : text;

        var number = new TextBlock
        {
            Text = label, FontFamily = font, FontSize = size,
            Foreground = numberBrush,
            FontWeight = cell.IsToday ? FontWeights.Bold : FontWeights.Normal,
        };
        UIElement head = number;
        if (cell.IsToday && !dense)
        {
            number.Margin = new Thickness(5, 0, 5, 0);
            head = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B)),
                CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(3, 3, 0, 1), Child = number,
            };
        }
        else number.Margin = new Thickness(6, 4, 0, 0);

        var stack = new StackPanel { Opacity = cell.InAnchorMonth ? 1.0 : 0.42 };
        stack.Children.Add(!dense && info is not null ? WithAnnotations(head, info, s, text, holidayBrush, font, size) : head);

        var shown = s.ShowCompleted ? items : items.Where(i => !i.Done).ToList();
        if (dense)
        {
            if (shown.Count > 0 || events.Count > 0)
                stack.Children.Add(new TextBlock { Text = "•", Foreground = text, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
        }
        else
        {
            double itemSize = Math.Max(9, size - 4);
            foreach (var occ in events)
                stack.Children.Add(MakeEventLine(occ, cell.Date, text, font, itemSize));
            int n = 0;
            foreach (var item in shown)
                stack.Children.Add(MakeLine(item, ++n, s, text, font, itemSize, cell.Date));
        }
        border.Child = stack;

        var tip = new List<string>();
        if (info?.Lunar is { } lunar) tip.Add($"음력 {lunar.ToShortString()}");
        if (info?.Term is { } term) tip.Add($"{term.Name} ({term.Kst:M/d HH:mm})");
        if (info is not null) tip.AddRange(info.Holidays.Select(h => h.IsDayOff ? $"{h.Name} (휴일)" : h.Name));
        tip.AddRange(events.Select(o => "◷ " + (o.StartTime is { } st ? $"{st:HH:mm} " : "") + o.Event.Title));
        tip.AddRange(items.Select(i => (i.Done ? "✓ " : "• ") + i.Content));
        if (tip.Count > 0) border.ToolTip = string.Join("\n", tip);
        border.ContextMenu = BuildCellMenu(cell.Date);
        border.MouseLeftButtonUp += (_, e) => { if (!_editMode && !e.Handled) { e.Handled = true; OpenEditor(cell.Date); } };
        _cells[cell.Date] = border;
        return border;
    }

    private static readonly SolidColorBrush TermBrush = Brush("#8FD694", Colors.LightGreen);
    private static readonly SolidColorBrush FestivalBrush = Brush("#FFD27F", Colors.Orange);

    /// <summary>The head row of a cell: day number on the left, lunar date on the right, and one name in between
    /// (holiday, else solar term, else memorial day).</summary>
    private static UIElement WithAnnotations(UIElement head, DayInfo info, BoardSettings s, WpfBrush text,
                                             WpfBrush holidayBrush, FontFamily font, double size)
    {
        double small = Math.Max(8.5, size - 6);
        var dock = new DockPanel();

        if (s.ShowLunar && info.Lunar is { } lunar)
        {
            var l = new TextBlock
            {
                Text = lunar.ToShortString(), FontFamily = font, FontSize = small, Foreground = text, Opacity = 0.55,
                Margin = new Thickness(0, 5, 5, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(l, Dock.Right);
            dock.Children.Add(l);
        }

        DockPanel.SetDock(head, Dock.Left);
        dock.Children.Add(head);

        (string Text, WpfBrush Brush)? name =
            s.ShowHolidays && info.HolidayName is { } hn ? (hn, holidayBrush)
            : s.ShowSolarTerms && info.Term is { } t ? (t.Name, TermBrush)
            : s.ShowFestivals && info.FestivalName is { } fn ? (fn, FestivalBrush)
            : null;
        dock.LastChildFill = false;
        if (name is not { } n) return dock;

        // The name gets its own line so long names ("부처님오신날", "설날 대체공휴일") are not cut off.
        var rows = new StackPanel();
        rows.Children.Add(dock);
        rows.Children.Add(new TextBlock
        {
            Text = n.Text, FontFamily = font, FontSize = small, Foreground = n.Brush,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 2, 1),
        });
        return rows;
    }

    private static readonly SolidColorBrush DefaultEventBrush = Brush("#9CC9FF", Colors.LightSkyBlue);

    /// <summary>An event inside a day cell: coloured left bar, time (first day only) and title.</summary>
    private UIElement MakeEventLine(EventOccurrence occ, DateOnly day, WpfBrush text, FontFamily font, double size)
    {
        var color = occ.Event.Color is { } hex ? Brush(hex, Colors.LightSkyBlue) : DefaultEventBrush;
        string prefix = day > occ.Date ? "↳ " : occ.StartTime is { } t ? $"{t:HH:mm} " : "";
        var row = new Border
        {
            Margin = new Thickness(4, 1, 2, 0), Padding = new Thickness(4, 0, 2, 0), CornerRadius = new CornerRadius(2),
            BorderBrush = color, BorderThickness = new Thickness(2.5, 0, 0, 0), Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = prefix + occ.Event.Title, Foreground = text, FontFamily = font, FontSize = size,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            },
        };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!_editMode) EditEvent(occ); };
        row.ContextMenu = BuildEventMenu(occ);
        return row;
    }

    internal void NewEvent(DateOnly date) => RunEventEditor(null, date);
    private void EditEvent(EventOccurrence occ) => RunEventEditor(occ.Event.Id, occ.Date);

    private void RunEventEditor(string? eventId, DateOnly date)
    {
        if (_series is null || _editMode) return;
        if (_editingDay is not null) CommitEditor();
        var win = new EventEditorWindow(_series, eventId, date);
        win.ShowDialog();
        if (win.Changed) { Render(); RemindersChanged?.Invoke(); }
        SendBack();
    }

    private void DeleteEvent(EventOccurrence occ)
    {
        if (_series is null) return;
        bool repeating = RecurrenceRule.From(occ.Event).IsRepeating;
        if (repeating)
        {
            switch (ScopeDialog.Ask(this, "삭제"))
            {
                case SeriesScope.This: _series.DeleteThis(occ.Event.Id, occ.Date); break;
                case SeriesScope.Future: _series.DeleteFuture(occ.Event.Id, occ.Date); break;
                case SeriesScope.All: _series.DeleteAll(occ.Event.Id); break;
                default: SendBack(); return;
            }
        }
        else
        {
            if (System.Windows.MessageBox.Show($"'{occ.Event.Title}' 일정을 삭제할까요?", "일정 삭제",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { SendBack(); return; }
            _series.DeleteAll(occ.Event.Id);
        }
        Render();
        RemindersChanged?.Invoke();
        SendBack();
    }

    private ContextMenu BuildEventMenu(EventOccurrence occ)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("일정 편집…", () => EditEvent(occ)));
        menu.Items.Add(MenuEntry("일정 삭제…", () => DeleteEvent(occ)));
        return menu;
    }

    /// <summary>Set (or clear, with null) the reminder of a day-cell item: it fires at 09:00, that many minutes earlier.</summary>
    private void SetItemReminder(Item item, int? offsetMinutes)
    {
        if (_reminders is null) return;
        _reminders.ReplaceForOwner(ReminderOwner.Item, item.Id,
            offsetMinutes is { } m ? [(m, "09:00", (string?)null)] : []);
        Render();
        RemindersChanged?.Invoke();
    }

    private UIElement MakeLine(Item item, int number, BoardSettings s, WpfBrush defaultText, FontFamily font, double size, DateOnly date)
    {
        var row = new Grid
        {
            Margin = new Thickness(4, 1, 2, 0),
            Background = item.BgColor is { } bgHex ? new SolidColorBrush(WithAlpha(ParseColor(bgHex, Colors.Transparent), 0x66)) : Brushes.Transparent,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fg = item.TextColor is { } tc ? Brush(tc, Colors.White) : defaultText;
        string marker = s.Bullet switch
        {
            BulletStyle.Number => $"{number}.",
            BulletStyle.Dot => item.Done ? "✓" : "•",
            _ => item.Done ? "✓" : "",
        };
        if (marker.Length > 0)
        {
            var m = new TextBlock
            {
                Text = marker, Foreground = fg, FontFamily = font, FontSize = size, Margin = new Thickness(0, 0, 3, 0),
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = item.Done ? "완료 해제" : "완료로 표시",
                Opacity = item.Done ? 0.6 : 1.0,
            };
            m.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!_editMode) ToggleDone(item); };
            row.Children.Add(m);
        }

        var t = new TextBlock
        {
            Text = item.Content, Foreground = fg, FontFamily = font, FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
            Opacity = item.Done ? 0.5 : 1.0,
        };
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        if (_itemsWithReminder.Contains(item.Id))
        {
            var bell = new TextBlock { Text = "⏰", FontSize = size - 1, Margin = new Thickness(3, 0, 0, 0), Opacity = 0.85, ToolTip = "알림 설정됨" };
            Grid.SetColumn(bell, 2);
            row.Children.Add(bell);
        }

        row.ContextMenu = BuildItemMenu(item, date);
        return row;
    }

    private void ToggleDone(Item item)
    {
        if (_items is null) return;
        bool nowDone = !item.Done;
        if (_items.SetDone(item.Id, nowDone) && nowDone && _settings.CompleteSound) Sounds.PlayComplete();
        Render();
    }

    // ---- context menus -----------------------------------------------------

    private ContextMenu BuildCellMenu(DateOnly date)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("일정 추가…", () => NewEvent(date)));
        menu.Items.Add(MenuEntry("항목 추가/편집…", () => OpenEditor(date)));
        return menu;
    }

    private ContextMenu BuildItemMenu(Item item, DateOnly date)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry(item.Done ? "완료 해제" : "완료로 표시", () => ToggleDone(item)));
        menu.Items.Add(MenuEntry("편집…", () => OpenEditor(date)));
        menu.Items.Add(new Separator());
        var reminder = new MenuItem { Header = "알림" };
        foreach (var (label, offset) in new (string, int?)[] { ("알림 없음", null), ("당일 09:00", 0), ("하루 전 09:00", 1440), ("3일 전 09:00", 4320) })
            reminder.Items.Add(MenuEntry(label, () => SetItemReminder(item, offset)));
        menu.Items.Add(reminder);
        menu.Items.Add(ColorMenu("글자색", hex => _items?.SetTextColor(item.Id, hex)));
        menu.Items.Add(ColorMenu("배경색", hex => _items?.SetColor(item.Id, hex)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("삭제", () => { _items?.SoftDelete(item.Id); Render(); }));
        return menu;
    }

    private MenuItem ColorMenu(string header, Action<string?> apply)
    {
        var parent = new MenuItem { Header = header };
        foreach (var (name, hex) in Palette)
        {
            var mi = MenuEntry(name, () => { apply(hex); Render(); });
            if (hex is not null) mi.Foreground = Brush(hex, Colors.White);
            mi.Background = Brushes.Black;
            parent.Items.Add(mi);
        }
        return parent;
    }

    private static MenuItem MenuEntry(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ---- helpers -----------------------------------------------------------

    private void Place(UIElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        GridHost.Children.Add(e);
    }

    private static string FormatTitle(BoardGrid g, CultureInfo culture)
    {
        bool wholeMonth = g.TitleStart.Day == 1 && g.TitleEnd.Month == g.TitleStart.Month && g.TitleEnd.Year == g.TitleStart.Year
                          && g.TitleEnd.AddDays(1).Day == 1;
        return wholeMonth
            ? g.TitleStart.ToString("Y", culture)
            : $"{g.TitleStart.ToString("d", culture)} – {g.TitleEnd.ToString("d", culture)}";
    }

    private static Color WithAlpha(Color c, byte a) { c.A = a; return c; }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    private static SolidColorBrush Brush(string hex, Color fallback)
    {
        var b = new SolidColorBrush(ParseColor(hex, fallback));
        b.Freeze();
        return b;
    }

    // Re-attach after Explorer restarts (TaskbarCreated is broadcast on restart).
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && !_editMode && IsVisible)
            Dispatcher.BeginInvoke(AttachToDesktop);
        return 0;
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(App.DataDir, "m0.log"), $"[{DateTime.Now:s}] {text}\n"); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
