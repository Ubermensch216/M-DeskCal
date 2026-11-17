using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
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

    internal static readonly (string Name, string? Hex)[] Palette =
    [
        ("기본", null), ("빨강", "#FF6B6B"), ("주황", "#FFA94D"), ("노랑", "#FFE066"),
        ("초록", "#69DB7C"), ("파랑", "#74C0FC"), ("보라", "#B197FC"),
    ];

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

    public MainWindow()
    {
        InitializeComponent();
        System.Windows.DataObject.AddPastingHandler(EditorBox, EditorBox_Pasting);
        System.Windows.DataObject.AddPastingHandler(ChatPromptBox, ChatPromptBox_Pasting);
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

    private CountdownRepository? _countdowns;
    private HistoryRepository? _history;
    internal void SetCountdowns(CountdownRepository countdowns) { _countdowns = countdowns; Render(); }
    internal void SetHistory(HistoryRepository history) => _history = history;

    /// <summary>Redraw after items were restored/undeleted or countdowns changed elsewhere (search window, countdown manager).</summary>
    internal void Refresh() => Render();

    // ---- content lock --------------------------------------------------------------

    private bool _contentHidden;

    /// <summary>True while the content lock hides everything the user wrote (day numbers and holidays stay).</summary>
    internal bool ContentHidden => _contentHidden;

    /// <summary>Set by the app: asks for the password if there is one, unhides on success, and says whether the content is now visible.</summary>
    internal Func<bool>? EnsureUnlocked { get; set; }

    internal void SetContentHidden(bool hidden)
    {
        if (_contentHidden == hidden) return;
        if (hidden) CloseChatPromptBox();
        if (hidden && _editingDay is not null) CommitEditor();
        _contentHidden = hidden;
        Render();
    }

    /// <summary>False when the content is locked and the user did not unlock it: the caller must not show or change content.</summary>
    private bool Guard() => !_contentHidden || (EnsureUnlocked?.Invoke() ?? false);

    // ---- background photo ("skin") ----------------------------------------------------

    private List<string> _skinFiles = [];
    private string _skinListedFolder = "";
    private DateTime _skinListedAt;
    private string? _skinShown;
    private SkinFit _skinShownFit;
    private double _skinShownOpacity = -1;

    /// <summary>Show the current background photo (or none). Re-lists the folder every ten minutes; skips work when nothing changed.</summary>
    private void ApplySkin()
    {
        var s = _settings;
        if (string.IsNullOrEmpty(s.SkinFolder)) { ClearSkin(); return; }

        if (s.SkinFolder != _skinListedFolder || DateTime.UtcNow - _skinListedAt > TimeSpan.FromMinutes(10))
        {
            _skinFiles = SkinPicker.List(s.SkinFolder);
            _skinListedFolder = s.SkinFolder; _skinListedAt = DateTime.UtcNow;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int index = SkinPicker.Pick(_skinFiles.Count, DateTimeOffset.Now, s.SkinRotateMinutes);
            if (index < 0) { ClearSkin(); return; }
            string path = _skinFiles[index];
            if (path == _skinShown && s.SkinFit == _skinShownFit && SkinLayer.Background is not null)
            {
                if (Math.Abs(_skinShownOpacity - s.BackgroundOpacity) > 0.001) { SkinLayer.Opacity = s.BackgroundOpacity; _skinShownOpacity = s.BackgroundOpacity; }
                return;
            }
            var bmp = LoadPicture(path);
            if (bmp is null) { _skinFiles.RemoveAt(index); continue; }     // unreadable picture: drop it and try the next

            var brush = new ImageBrush(bmp);
            switch (s.SkinFit)
            {
                case SkinFit.Fill: brush.Stretch = Stretch.UniformToFill; break;
                case SkinFit.Fit: brush.Stretch = Stretch.Uniform; break;
                case SkinFit.Stretch: brush.Stretch = Stretch.Fill; break;
                case SkinFit.Center: brush.Stretch = Stretch.None; break;
                case SkinFit.Tile:
                    brush.TileMode = TileMode.Tile; brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new Rect(0, 0, bmp.Width, bmp.Height); brush.Stretch = Stretch.Fill;
                    break;
            }
            brush.Freeze();
            SkinLayer.Background = brush;
            SkinLayer.Opacity = s.BackgroundOpacity;
            SkinLayer.Visibility = Visibility.Visible;
            (_skinShown, _skinShownFit, _skinShownOpacity) = (path, s.SkinFit, s.BackgroundOpacity);
            return;
        }
        ClearSkin();
    }

    private void ClearSkin()
    {
        if (SkinLayer.Background is null && SkinLayer.Visibility == Visibility.Collapsed) return;
        SkinLayer.Background = null; SkinLayer.Visibility = Visibility.Collapsed; _skinShown = null;
    }

    /// <summary>Decodes a picture at no more than 1920 px wide (a camera photo would otherwise take ~100 MB). Null when it cannot be read.</summary>
    internal static System.Windows.Media.Imaging.BitmapSource? LoadPicture(string path)
    {
        try
        {
            var uri = new Uri(path);
            int width = System.Windows.Media.Imaging.BitmapDecoder.Create(uri, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation | System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0].PixelWidth;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            if (width > 1920) bmp.DecodePixelWidth = 1920;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Apply new settings. Switching display mode re-centres on today.</summary>
    internal void ApplySettings(BoardSettings settings)
    {
        var s = settings.Sanitized();
        if (s.Mode != _settings.Mode) _anchor = Today();
        _settings = s;
        Render();
    }

    /// <summary>The date the board is currently centred on (its month is what "print" prints).</summary>
    internal DateOnly Anchor => _anchor;

    private DateOnly? _highlightedDay;
    private System.Windows.Threading.DispatcherTimer? _highlightTimer;

    internal void GoToday() { _highlightedDay = null; _anchor = Today(); Render(); }
    internal void GoTo(DateOnly date) { _highlightedDay = null; _anchor = date; Render(); }

    /// <summary>Navigate to <paramref name="date"/> and briefly outline that day cell so same-month jumps are visible.</summary>
    internal void GoToAndHighlight(DateOnly date)
    {
        _highlightedDay = date;
        _anchor = date;
        Render();
        _highlightTimer?.Stop();
        _highlightTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _highlightedDay = null;
            if (IsVisible) Render();
        };
        _highlightTimer.Start();
    }

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
        $"M-DeskCal {AppInfo.Version}\nattached={_attached} edit={_editMode}\nrect={_rect}\nanchor={_anchor} mode={_settings.Mode}\n{DesktopHost.LastDiagnostics}";

    // ---- desktop pinning ---------------------------------------------------

    public void AttachToDesktop()
    {
        _attached = DesktopHost.Pin(this, _rect);
        Log($"pin -> {_attached} {DesktopHost.LastDiagnostics}");
        _timer ??= StartTimer();
    }

    private System.Windows.Threading.DispatcherTimer? _timer;
    private WeakReference<Window>? _peekBehind;

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
        CloseChatPromptBox();
        if (_editingDay is not null) CommitEditor();
        _peekBehind = null;
        Hide();
    }

    internal void ToggleBoard() { if (IsVisible) HideBoard(); else ShowBoard(); }

    /// <summary>
    /// Raise the board above the other windows so it can be read or used. When <paramref name="behind"/> is given and visible,
    /// the board stays directly below that window without stealing focus; otherwise it floats topmost and takes focus.
    /// The 1 s timer (and deactivation) put it back under the other windows once the user clicks elsewhere.
    /// </summary>
    internal void BringToFront(Window? behind = null)
    {
        // Do not re-pin when already shown: moving the window behind the others would deactivate it, and the
        // queued deactivation would then send it straight back.
        if (!IsVisible) ShowBoard();
        if (behind is not null && behind.IsVisible)
        {
            _peekBehind = new WeakReference<Window>(behind);
            DesktopHost.FloatBehind(this, behind);
            return;
        }
        _peekBehind = null;
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
            bool keepingPeek = _peekBehind is { } w && w.TryGetTarget(out var front) && front.IsVisible && front.IsActive;
            if (!_editMode && IsVisible && !IsActive && !keepingPeek && _busy == 0)
            {
                _peekBehind = null;
                DesktopHost.Repin(this);
            }
            if (Today() != _renderedToday && _editingDay is null) Render();
            else ApplySkin();     // cheap unless the rotation moved on to the next picture
        };
        t.Start();
        return t;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_busy > 0) return;                         // an option popup or dialog of the editor has the focus
        CloseChatPromptBox();
        if (_editingDay is not null) CommitEditor();   // never lose typed text when focus moves away
        else if (!_editMode && _attached && IsVisible) { _peekBehind = null; DesktopHost.Repin(this); }
    }

    public void SetEditMode(bool on)
    {
        if (on == _editMode) return;
        if (on && !IsVisible) ShowBoard();
        if (on) CloseChatPromptBox();
        if (on && _editingDay is not null) CommitEditor();
        _editMode = on;
        if (on)
        {
            DesktopHost.Float(this);
            EditHint.Visibility = EditOverlay.Visibility = Visibility.Visible;
            ShowActivated = true;
            Activate();
        }
        else
        {
            _rect = DesktopHost.GetScreenRect(this);
            EditHint.Visibility = EditOverlay.Visibility = Visibility.Collapsed;
            AttachToDesktop();
            RectCommitted?.Invoke(_rect);
        }
    }

    // ---- input -------------------------------------------------------------

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (IsChatPromptActive() && !IsWithinElement(e.OriginalSource as DependencyObject, ChatPromptBar))
            CloseChatPromptBox();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editMode && !e.Handled) DragMove();
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

    /// <summary>Raised when the gear is clicked; the app answers by opening its quick menu under <see cref="GearAnchor"/>.</summary>
    internal event Action? GearClicked;
    internal FrameworkElement GearAnchor => GearBtn;
    private void Gear_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_editingDay is not null) CommitEditor();
        GearClicked?.Invoke();
    }

    internal sealed record SyncStatusBadgeData(int Pulled, int Pushed, int Warnings, int Errors, string Tooltip);

    /// <summary>Raised when the header sync icon is clicked.</summary>
    internal event Action? SyncClicked;
    /// <summary>Raised when the header sync result badge is clicked.</summary>
    internal event Action? SyncResultClicked;
    internal FrameworkElement SyncAnchor => SyncBtn;

    private bool _syncRunning;
    private SyncStatusBadgeData? _syncStatus;

    private void Sync_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_editMode) return;
        if (_editingDay is not null) CommitEditor();
        SyncClicked?.Invoke();
    }

    private void SyncResult_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_editMode) return;
        if (_editingDay is not null) CommitEditor();
        SyncResultClicked?.Invoke();
    }

    internal void SetSyncRunning(bool running)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetSyncRunning(running)); return; }
        if (_syncRunning == running) return;
        _syncRunning = running;
        if (running)
        {
            SyncBtn.ToolTip = "동기화하는 중…";
            SyncIcon.Stroke = UiColors.Frozen(Color.FromRgb(0x7E, 0xB6, 0xFF));
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(950))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            SyncIconRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }
        else
        {
            SyncIconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            SyncIconRotate.Angle = 0;
            SyncIcon.Stroke = Brush(_settings.TextColor, Colors.White);
            SyncBtn.ToolTip = "동기화 실행 (우클릭: 동기화 설정)";
        }
        if (_syncStatus is not null) SyncResultBadge.Opacity = running ? 0.55 : 1.0;
    }

    internal void SetSyncResult(SyncStatusBadgeData? status)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetSyncResult(status)); return; }
        _syncStatus = status;
        RenderSyncResult();
    }

    private void RenderSyncResult()
    {
        SyncResultContent.Children.Clear();
        if (_syncStatus is null || _contentHidden)
        {
            SyncResultBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var textBrush = Brush(_settings.TextColor, Colors.White);
        var green = UiColors.Frozen(Color.FromRgb(0x6E, 0xE7, 0xB7));
        var blue = UiColors.Frozen(Color.FromRgb(0x7E, 0xB6, 0xFF));
        var amber = UiColors.Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
        var red = UiColors.Frozen(Color.FromRgb(0xFC, 0xA5, 0xA5));

        static System.Windows.Shapes.Path MiniIcon(string data, WpfBrush stroke, double size = 10, double thickness = 2.2) => new()
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse(data),
        };

        void AddPair(string iconData, WpfBrush iconStroke, int count, double leftMargin = 0, double iconSize = 9.5)
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(leftMargin, 0, 0, 0) };
            pair.Children.Add(MiniIcon(iconData, iconStroke, iconSize, 2.2));
            pair.Children.Add(new TextBlock
            {
                Text = count.ToString(CultureInfo.InvariantCulture),
                Foreground = textBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2.5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            SyncResultContent.Children.Add(pair);
        }

        const string checkPath = "M20 6L9 17l-5-5";
        const string downPath = "M12 5v14M6 13l6 6 6-6";
        const string upPath = "M12 19V5M6 11l6-6 6 6";
        const string warnPath = "M12 3L2 20h20L12 3zM12 10v4M12 17h.01";
        const string errorPath = "M18 6L6 18M6 6l12 12";

        var s = _syncStatus;
        if (s.Errors > 0)
        {
            AddPair(errorPath, red, s.Errors, iconSize: 10);
            if (s.Pulled > 0) AddPair(downPath, blue, s.Pulled, leftMargin: 6);
            if (s.Pushed > 0) AddPair(upPath, green, s.Pushed, leftMargin: 6);
            if (s.Warnings > 0) AddPair(warnPath, amber, s.Warnings, leftMargin: 6);
        }
        else if (s.Pulled == 0 && s.Pushed == 0 && s.Warnings == 0)
        {
            AddPair(checkPath, green, 0, iconSize: 10.5);
        }
        else if (s.Pulled == 0 && s.Pushed == 0 && s.Warnings > 0)
        {
            AddPair(warnPath, amber, s.Warnings, iconSize: 10);
        }
        else
        {
            SyncResultContent.Children.Add(MiniIcon(checkPath, green, 10.5, 2.4));
            if (s.Pulled > 0) AddPair(downPath, blue, s.Pulled, leftMargin: 5);
            if (s.Pushed > 0) AddPair(upPath, green, s.Pushed, leftMargin: 5);
            if (s.Warnings > 0) AddPair(warnPath, amber, s.Warnings, leftMargin: 5);
        }

        SyncResultBadge.ToolTip = s.Tooltip;
        SyncResultBadge.Opacity = _syncRunning ? 0.55 : 1.0;
        SyncResultBadge.Visibility = Visibility.Visible;
    }

    /// <summary>Raised when the user submits a prompt from the header Calendar Chat box.</summary>
    internal event Action<string>? ChatSubmitted;

    private void ChatPrompt_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _editMode || !Guard()) return;
        e.Handled = true;
        if (_editingDay is not null) CommitEditor();
        ChatPromptPlaceholder.Visibility = Visibility.Collapsed;
        ChatPromptBox.Visibility = Visibility.Visible;
        Activate();
        ChatPromptBox.Focus();
        ChatPromptBox.SelectAll();
        Keyboard.Focus(ChatPromptBox);
    }

    /// <summary>
    /// Raised when text of several lines (a notice, an e-mail) or a picture is pasted into the header box: that one-line box would
    /// keep only the first line, so the paste goes to the Calendar Chat window's input instead, where Enter extracts the dates.
    /// </summary>
    internal event Action<string>? ChatTextPasted;
    internal event Action<System.Windows.Media.Imaging.BitmapSource>? ChatImagePasted;

    private void ChatPromptBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true)) return;
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text) return;
        if (text.ReplaceLineEndings("\n").Trim().IndexOf('\n') < 0) return;      // one line: paste as usual
        e.CancelCommand();
        string draft = (ChatPromptBox.Text + text).Trim();
        CloseChatPromptBox();
        Dispatcher.BeginInvoke(() => ChatTextPasted?.Invoke(draft));
    }

    private void ChatPromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try
            {
                if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && Clipboard.GetImage() is { } bmp)
                {
                    e.Handled = true;
                    CloseChatPromptBox();
                    Dispatcher.BeginInvoke(() => ChatImagePasted?.Invoke(bmp));
                }
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string prompt = ChatPromptBox.Text.Trim();
            CloseChatPromptBox();
            ChatSubmitted?.Invoke(prompt);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseChatPromptBox();
        }
    }

    private void ChatPromptBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseChatPromptBox();

    private bool IsChatPromptActive() =>
        ChatPromptBox.Visibility == Visibility.Visible || ChatPromptBox.IsKeyboardFocusWithin;

    private static bool IsWithinElement(DependencyObject? source, DependencyObject container)
    {
        for (var cur = source; cur is not null;)
        {
            if (ReferenceEquals(cur, container)) return true;
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void CloseChatPromptBox()
    {
        if (ChatPromptBox.Visibility == Visibility.Visible)
        {
            ChatPromptBox.Text = "";
            ChatPromptBox.Visibility = Visibility.Collapsed;
            ChatPromptPlaceholder.Visibility = Visibility.Visible;
        }
        if (ChatPromptBox.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        if (ReferenceEquals(FocusManager.GetFocusedElement(this), ChatPromptBox))
            FocusManager.SetFocusedElement(this, null);
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string dir }) return;
        const double minW = 320, minH = 240;

        if (dir.Contains('E'))
            Width = Math.Max(minW, Width + e.HorizontalChange);
        else if (dir.Contains('W'))
        {
            double next = Math.Max(minW, Width - e.HorizontalChange);
            Left += Width - next;
            Width = next;
        }

        if (dir.Contains('S'))
            Height = Math.Max(minH, Height + e.VerticalChange);
        else if (dir.Contains('N'))
        {
            double next = Math.Max(minH, Height - e.VerticalChange);
            Top += Height - next;
            Height = next;
        }
    }

    // ---- day editor --------------------------------------------------------

    /// <summary>Open the one-line-per-item editor over the given day's cell.</summary>
    internal void OpenEditor(DateOnly date)
    {
        if (_items is null || _editMode || !Guard()) return;
        CloseChatPromptBox();
        if (_editingDay is not null) CommitEditor();
        _editingDay = date;

        EditorTitle.Text = date.ToString("D", CultureInfo.CurrentCulture);
        EditorHint.Text = DefaultEditorHint;
        EditorBox.FontFamily = new FontFamily(_settings.FontFamily);
        EditorBox.FontSize = Math.Max(12, _settings.FontSize - 1);
        LoadEditor(_items.GetByDay(DayKey(date)));
        UpdateToolbar();

        Root.UpdateLayout();
        double rw = Root.ActualWidth, rh = Root.ActualHeight;
        var r = _cells.TryGetValue(date, out var cell) && cell.IsLoaded
            ? cell.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight))
            : new Rect(rw / 2 - 130, rh / 2 - 95, 260, 190);
        double w = Math.Min(Math.Max(r.Width, 290), Math.Max(150, rw - 8));      // wide enough for the five option buttons + save
        double h = Math.Min(Math.Max(r.Height + 80, 250), Math.Max(120, rh - 8));
        EditorPanel.Width = w;
        EditorPanel.Height = h;
        EditorPanel.Margin = new Thickness(
            Math.Clamp(r.Left, 4, Math.Max(4, rw - w - 4)),
            Math.Clamp(r.Top, 4, Math.Max(4, rh - h - 4)), 0, 0);
        EditorLayer.Visibility = Visibility.Visible;

        Activate();
        EditorBox.Focus();
        EditorBox.CaretPosition = EditorBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        Keyboard.Focus(EditorBox);
    }

    // The editor is a RichTextBox with one paragraph per item. The option buttons under it act on the item of the line the caret is on:
    // they first save the typed text (SyncEditor, the editor stays open), so that every line is a real item before an option is applied to it.

    private const string DefaultEditorHint = "한 줄에 항목 하나 · Ctrl+Enter 저장 · Esc 취소";

    private List<Item> _editorItems = [];               // the day's items in line order, as of the last load / sync
    private readonly HashSet<Paragraph> _knownLines = [];
    private int _busy;                                  // > 0 while an option popup/dialog has the focus: the board must not commit the editor then
    private SettingsStore? _store;
    private ItemSeriesRepository? _itemSeries;
    private DateOnly _extendedOn;

    internal void SetSettingsStore(SettingsStore store) => _store = store;
    internal void SetItemSeries(ItemSeriesRepository series) { _itemSeries = series; Render(); }

    /// <summary>Raised when the editor created, changed or removed a D-day, so the desktop cards can be redrawn.</summary>
    internal event Action? CountdownsChanged;

    private static string ParagraphText(Paragraph p) =>
        new TextRange(p.ContentStart, p.ContentEnd).Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private List<Paragraph> Lines() => EditorBox.Document.Blocks.OfType<Paragraph>().ToList();

    private List<string> ReadEditorLines() => Lines().Select(ParagraphText).ToList();

    private void StyleLine(Paragraph p, Item? item)
    {
        if (item?.TextColor is { } tc) p.Foreground = Brush(tc, Colors.White); else p.ClearValue(TextElement.ForegroundProperty);
        if (item?.Done == true) p.TextDecorations = TextDecorations.Strikethrough; else p.ClearValue(Paragraph.TextDecorationsProperty);
        if (item?.BgColor is { } bg) p.Background = new SolidColorBrush(WithAlpha(UiColors.Parse(bg, Colors.Transparent), 0x88)); else p.ClearValue(TextElement.BackgroundProperty);
    }

    private void ResetLine(Paragraph p)
    {
        p.ClearValue(TextElement.ForegroundProperty);
        p.ClearValue(Paragraph.TextDecorationsProperty);
        p.ClearValue(TextElement.BackgroundProperty);
    }

    private FlowDocument NewDocument() => new()
    {
        PagePadding = new Thickness(0), FontFamily = EditorBox.FontFamily, FontSize = EditorBox.FontSize, Foreground = EditorBox.Foreground,
    };

    private void ShowDocument(FlowDocument doc)
    {
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        foreach (var p in doc.Blocks.OfType<Paragraph>()) _knownLines.Add(p);
        EditorBox.Document = doc;
    }

    /// <summary>Show the items as lines (colour, strike-through and cell colour included).</summary>
    private void LoadEditor(IReadOnlyList<Item> items)
    {
        _editorItems = items.ToList();
        _knownLines.Clear();
        var doc = NewDocument();
        foreach (var it in items)
        {
            var p = new Paragraph(new Run(it.Content));
            StyleLine(p, it);
            doc.Blocks.Add(p);
        }
        ShowDocument(doc);
    }

    /// <summary>Dev aid (snapshot self-test): the editor's text, as if typed by the user.</summary>
    internal string EditorText
    {
        get => string.Join("\r\n", ReadEditorLines());
        set
        {
            _editorItems = [];
            _knownLines.Clear();
            var doc = NewDocument();
            foreach (var line in value.Split('\n')) doc.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))));
            ShowDocument(doc);
        }
    }

    /// <summary>The item of the line the caret is on - only while the lines still match the loaded items one to one; null otherwise.</summary>
    private Item? CurrentLineItem()
    {
        var lines = Lines();
        if (lines.Count != _editorItems.Count) return null;
        int i = EditorBox.CaretPosition.Paragraph is { } p ? lines.IndexOf(p) : -1;
        return i >= 0 ? _editorItems[i] : null;
    }

    /// <summary>Light up the option buttons that are "on" for the current line.</summary>
    private void UpdateToolbar()
    {
        if (_editingDay is null) return;
        var it = CurrentLineItem();
        void Tint(TextBlock glyph, bool on) => glyph.Foreground = on ? Brush("#6EA8FF", Colors.CornflowerBlue) : Brush("#B8C4D6", Colors.LightGray);
        Tint(GlyphDone, it?.Done == true);
        Tint(GlyphColor, it?.TextColor is not null || it?.BgColor is not null || (_editingDay is { } ed && GetCellBg(ed) is not null));
        Tint(GlyphRepeat, it?.SeriesId is not null);
        Tint(GlyphReminder, it is not null && _itemsWithReminder.Contains(it.Id));
        Tint(GlyphCountdown, it?.CountdownId is not null);
        BtnDone.ToolTip = it?.Done == true ? "작업 완료 해제" : "작업 완료 표시";
    }

    /// <summary>
    /// Save the typed text now (the editor stays open, its lines are redrawn from the saved items) and return the item of the line the
    /// caret was on; null (with a hint) when that line is empty.
    /// </summary>
    private Item? SyncEditor()
    {
        if (_editingDay is not { } day || _items is null) return null;
        var lines = Lines();
        var texts = lines.Select(ParagraphText).ToList();
        var caretLine = EditorBox.CaretPosition.Paragraph;
        int para = caretLine is null ? -1 : lines.IndexOf(caretLine);
        int offset = para < 0 ? 0 : new TextRange(caretLine!.ContentStart, EditorBox.CaretPosition).Text.Length;
        int lineNo = para >= 0 && texts[para].Trim().Length > 0 ? texts.Take(para).Count(t => t.Trim().Length > 0) : -1;

        var items = _items.ReplaceDay(DayKey(day), texts);
        LoadEditor(items);
        if (lineNo < 0 || lineNo >= items.Count)
        {
            EditorBox.CaretPosition = EditorBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            EditorHint.Text = "텍스트가 있는 줄에서 사용하세요.";
            return null;
        }
        var target = Lines()[lineNo];
        var start = target.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        EditorBox.CaretPosition = start.GetPositionAtOffset(Math.Min(offset, items[lineNo].Content.Length)) ?? start;
        EditorHint.Text = DefaultEditorHint;
        return items[lineNo];
    }

    /// <summary>After an option changed an item: refresh its cached copy and line, redraw the board behind the editor, give the focus back.</summary>
    private void AfterOption(string itemId, bool remindersChanged = false)
    {
        if (_items?.Get(itemId) is { } fresh)
        {
            int i = _editorItems.FindIndex(x => x.Id == itemId);
            var lines = Lines();
            if (i >= 0 && i < lines.Count && lines.Count == _editorItems.Count) { _editorItems[i] = fresh; StyleLine(lines[i], fresh); }
        }
        Render();
        UpdateToolbar();
        if (remindersChanged) RemindersChanged?.Invoke();
        EditorBox.Focus();
    }

    private T Modal<T>(Func<T> show)
    {
        _busy++;
        try { return show(); }
        finally { _busy--; Dispatcher.BeginInvoke(() => { Activate(); EditorBox.Focus(); }); }
    }

    private static DateOnly ParseDay(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Today();

    /// <summary>Pasted text is taken as plain text only (the lines carry no formatting of their own).</summary>
    private void EditorBox_Pasting(object sender, System.Windows.DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText)) { e.CancelCommand(); return; }
        e.DataObject = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, e.DataObject.GetData(System.Windows.DataFormats.UnicodeText));
        e.FormatToApply = System.Windows.DataFormats.UnicodeText;
    }

    private void EditorSave_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; CommitEditor(); }
    private void EditorBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateToolbar();

    // A line made by pressing Enter inherits the strike-through / colours of the one it was split from: show it plain until it is saved.
    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editingDay is null) return;
        foreach (var p in Lines())
            if (_knownLines.Add(p)) ResetLine(p);
    }

    private void OptDone_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; DoneAtCaret(); }

    private void DoneAtCaret()
    {
        if (SyncEditor() is not { } it) return;
        bool now = !it.Done;
        if (_items!.SetDone(it.Id, now) && now && _settings.CompleteSound) Sounds.PlayComplete();
        AfterOption(it.Id);
    }

    // ---- dev hooks for the snapshot self-test (they run the same code the option buttons run) ----

    internal void DevCaretToLine(int line)
    {
        var lines = Lines();
        if (line >= 0 && line < lines.Count) EditorBox.CaretPosition = lines[line].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    internal void DevDone() => DoneAtCaret();

    /// <summary>Make the item repeat weekly and count down to its day (what the repeat and countdown dialogs do on "apply").</summary>
    internal void DevMakeMarks(string itemId)
    {
        var item = _items!.Get(itemId)!;
        _itemSeries!.SetRepeat(itemId, new RecurrenceRule { Kind = RepeatKind.Weekly }, Today());
        var cd = new Countdown(Guid.NewGuid().ToString("N"), item.Content, item.Day, CountdownType.Exam, false, false, false);
        _countdowns!.Upsert(cd);
        _items.SetCountdown(itemId, cd.Id);
        Render();
    }

    internal void DevColor(string? textHex, string? cellHex)
    {
        if (SyncEditor() is not { } it) return;
        _items!.SetTextColor(it.Id, textHex);
        _items.SetColor(it.Id, cellHex);
        AfterOption(it.Id);
    }

    internal IReadOnlyList<(string Text, bool Struck, string? Foreground)> DevLines() =>
        Lines().Select(p => (ParagraphText(p), p.TextDecorations is { Count: > 0 }, (p.Foreground as SolidColorBrush)?.Color.ToString())).ToList();

    private void OptColor_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_editingDay is not { } day) return;
        var it = Lines().Any(p => ParagraphText(p).Trim().Length > 0) ? SyncEditor() : null;
        EditorHint.Text = DefaultEditorHint;
        _busy++;
        ColorPickerPopup.Show(BtnColor, _store, it?.TextColor, GetCellBg(day),
            hex =>
            {
                var target = it ?? SyncEditor();
                if (target is null) return;
                _items!.SetTextColor(target.Id, hex);
                it = _items.Get(target.Id);
                AfterOption(target.Id);
            },
            hex =>
            {
                SetCellBg(day, hex);
                Render();
                UpdateToolbar();
                EditorBox.Focus();
            },
            () => { _busy--; Dispatcher.BeginInvoke(() => { Activate(); EditorBox.Focus(); }); });
    }

    private void OptRepeat_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_itemSeries is null || SyncEditor() is not { } it) return;
        var current = it.SeriesId is { } sid ? _itemSeries.Get(sid)?.Rule ?? RecurrenceRule.None : RecurrenceRule.None;
        var rule = Modal(() => RepeatDialog.Show(this, it.Content, ParseDay(it.Day), current));
        if (rule is null) return;
        if (rule.IsRepeating) _itemSeries.SetRepeat(it.Id, rule, Today()); else _itemSeries.ClearRepeat(it.Id);
        AfterOption(it.Id, remindersChanged: true);
    }

    private void OptReminder_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_reminders is null || SyncEditor() is not { } it) return;
        var specs = Modal(() => ReminderDialog.Show(this, it.Content, ParseDay(it.Day), _reminders.GetByOwner(ReminderOwner.Item, it.Id)));
        if (specs is null) return;
        _reminders.ReplaceForOwner(ReminderOwner.Item, it.Id, specs.Select(s => (s.OffsetMinutes, (string?)s.Time, (string?)null)));
        AfterOption(it.Id, remindersChanged: true);
    }

    private void OptCountdown_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_countdowns is null || _items is null || SyncEditor() is not { } it) return;
        var existing = it.CountdownId is { } cid ? _countdowns.List().FirstOrDefault(c => c.Id == cid) : null;
        var choice = Modal(() => ItemCountdownDialog.Show(this, it.Content, ParseDay(it.Day), existing));
        if (choice is null) return;
        if (choice.Remove)
        {
            if (existing is not null) _countdowns.Delete(existing.Id);
            _items.SetCountdown(it.Id, null);
        }
        else
        {
            // a lunar D-day made in the countdown manager keeps its own (lunar) date; otherwise the D-day is this item's day
            var cd = new Countdown(existing?.Id ?? Guid.NewGuid().ToString("N"), it.Content, existing?.Lunar == true ? existing.Date : it.Day,
                choice.Type, choice.ShowCard, choice.ShowBottom, existing?.Lunar ?? false);
            _countdowns.Upsert(cd);
            _items.SetCountdown(it.Id, cd.Id);
        }
        AfterOption(it.Id);
        CountdownsChanged?.Invoke();
    }

    internal void CommitEditor()
    {
        if (_editingDay is not { } day) return;
        var lines = ReadEditorLines();
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
        // the editor holds plain lines: the word-processor shortcuts would only add formatting that is never saved
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.B or Key.I or Key.U or Key.E or Key.L or Key.R or Key.J) e.Handled = true;
    }

    // ---- rendering ---------------------------------------------------------

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
    private static string DayKey(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Render()
    {
        var s = _settings;
        var culture = CultureInfo.CurrentCulture;
        _renderedToday = Today();
        ExtendRepeats();
        var grid = BoardLayout.Build(_anchor, _renderedToday, s);
        var byDay = _contentHidden ? [] : LoadItems(grid);
        _countdownLabels = _contentHidden || _countdowns is null ? [] : CountdownLabels();
        var eventsByDay = _contentHidden ? [] : LoadEvents(grid);
        _itemsWithReminder = _contentHidden ? [] : LoadItemReminderIds();
        _cellBgs = _contentHidden ? [] : LoadCellBgs();
        _cells.Clear();
        ApplySkin();

        // frame: background, opacity, shadow
        var bg = UiColors.Parse(s.BackgroundColor, Color.FromRgb(0x10, 0x18, 0x20));
        bg.A = (byte)Math.Round(s.BackgroundOpacity * 255);
        Frame.Background = new SolidColorBrush(bg);
        Frame.Margin = new Thickness(s.Shadow ? 12 : 0);
        Frame.Effect = s.Shadow ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black } : null;

        var text = Brush(s.TextColor, Colors.White);
        var off = Brush(s.OffDayColor, Color.FromRgb(0xFF, 0x7A, 0x7A));
        var font = new FontFamily(s.FontFamily);

        foreach (var tb in new[] { MonthTitle, PrevText, TodayText, NextText }) { tb.Foreground = text; tb.FontFamily = font; }
        GearText.Foreground = text;   // keeps its own icon font
        if (!_syncRunning) SyncIcon.Stroke = text;
        RenderSyncResult();
        MonthTitle.FontSize = s.FontSize + 7;
        MonthTitle.Text = FormatTitle(grid, culture) + (_contentHidden ? "  🔒" : "");

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

        RenderCountdownBar(text, font, headerSize + 1);
    }

    private Dictionary<string, string> _countdownLabels = [];
    private Dictionary<DateOnly, string> _cellBgs = [];
    private const string CellBgPrefix = "cell.bg.";

    private static string CellBgKey(DateOnly day) => CellBgPrefix + DayKey(day);

    private string? GetCellBg(DateOnly day) => _store?.GetString(CellBgKey(day));

    private void SetCellBg(DateOnly day, string? hex)
    {
        if (_store is null) return;
        if (string.IsNullOrWhiteSpace(hex)) _store.Remove(CellBgKey(day));
        else _store.SetString(CellBgKey(day), hex);
    }

    private Dictionary<DateOnly, string> LoadCellBgs()
    {
        var map = new Dictionary<DateOnly, string>();
        if (_store is null) return map;
        foreach (var (k, v) in _store.GetAll(CellBgPrefix))
            if (DateOnly.TryParseExact(k.AsSpan(CellBgPrefix.Length), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                map[d] = v;
        return map;
    }

    /// <summary>"D-12" per countdown id, for the items that count down to a day.</summary>
    private Dictionary<string, string> CountdownLabels()
    {
        var map = new Dictionary<string, string>();
        foreach (var cd in _countdowns!.List())
            if (CountdownCalc.Compute(cd, _renderedToday) is { } st) map[cd.Id] = st.Label;
        return map;
    }

    /// <summary>Once a day: create the occurrences of repeating items that now fall inside the one-year window.</summary>
    private void ExtendRepeats()
    {
        if (_itemSeries is null || _extendedOn == _renderedToday) return;
        _extendedOn = _renderedToday;
        try { if (_itemSeries.Extend(_renderedToday) > 0) RemindersChanged?.Invoke(); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { Log("extend repeats: " + ex.Message); }
    }

    /// <summary>The strip under the grid: the countdowns that asked to be shown there, nearest first.</summary>
    private void RenderCountdownBar(WpfBrush text, FontFamily font, double size)
    {
        CountdownBar.Children.Clear();
        var shown = _countdowns is null || _contentHidden ? []
            : CountdownCalc.Ordered(_countdowns.List().Where(c => c.ShowBottom), _renderedToday);
        CountdownBar.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (cd, status) in shown)
        {
            var tb = new TextBlock
            {
                Text = CountdownCalc.Summary(cd, status), Foreground = text, FontFamily = font, FontSize = size,
                Margin = new Thickness(0, 0, 14, 0), Opacity = status.Elapsed ? 0.65 : 0.95,
            };
            if (status.Days == 0 && !status.Elapsed) tb.FontWeight = FontWeights.Bold;
            CountdownBar.Children.Add(tb);
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
        bool isHighlighted = _highlightedDay == cell.Date;
        var border = new Border
        {
            Margin = new Thickness(s.CellGap / 2.0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,          // transparent (not null) so empty cells are clickable
            BorderBrush = isHighlighted ? Brush("#FFC857", Colors.Gold) : Brushes.Transparent,
            BorderThickness = new Thickness(isHighlighted ? 2 : 0),
            ClipToBounds = true,
        };
        if (cell.IsBlank) return border;

        var info = _calendar?.Get(cell.Date);
        var holidayBrush = Brush(s.HolidayColor, Color.FromRgb(0xFF, 0x6B, 0x6B));
        bool isHoliday = s.ShowHolidays && info?.IsDayOff == true;
        bool isDayOffCell = isHoliday || (s.ShowOffDays && cell.IsOffDay);

        var accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        if (_cellBgs.TryGetValue(cell.Date, out var cellBgHex) && UiColors.Parse(cellBgHex, Colors.Transparent) is { A: > 0 } cc)
            border.Background = new SolidColorBrush(WithAlpha(cc, 0x66));
        else if (isHighlighted) border.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xC8, 0x57));
        else if (cell.IsToday) border.Background = new SolidColorBrush(dense ? Color.FromArgb(0xB0, accent.R, accent.G, accent.B)
                                                                              : Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        else if (isDayOffCell && !string.IsNullOrWhiteSpace(s.HolidayBgColor))
        {
            var hc = UiColors.Parse(s.HolidayBgColor, Colors.Transparent);
            if (hc.A > 0) border.Background = new SolidColorBrush(WithAlpha(hc, 0x44));
        }
        if (dense || s.Mode is DisplayMode.SingleLine or DisplayMode.SingleLineFloating) border.MinHeight = size * 2.4;

        // "M/d" on the 1st only where the view can span months; a fixed month needs no label.
        bool floatingLike = s.Mode is DisplayMode.FloatingMonth or DisplayMode.SingleLineFloating;
        string label = cell.IsMonthStart && floatingLike
            ? $"{cell.Date.Month}/{cell.Date.Day}"
            : cell.Date.Day.ToString(CultureInfo.InvariantCulture);

        WpfBrush numberBrush = isHoliday ? holidayBrush : s.ShowOffDays && cell.IsOffDay ? off : text;

        var number = new TextBlock
        {
            Text = label, FontFamily = font, FontSize = size,
            Foreground = numberBrush,
            Margin = new Thickness(6, 4, 0, 0),
        };
        UIElement head = number;

        var stack = new StackPanel { Opacity = cell.InAnchorMonth ? 1.0 : 0.42 };
        stack.Children.Add(!dense && info is not null ? WithAnnotations(head, info, s, text, holidayBrush, font, size) : head);

        var shown = s.ShowCompleted ? items : items.Where(i => !i.Done).ToList();
        if (s.MoveDoneToBottom) shown = shown.OrderBy(i => i.Done).ToList();   // stable: keeps the written order inside each group
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
        // A single click does nothing (it used to open the editor, which fired by accident); double-click opens it.
        border.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && !_editMode && !e.Handled) { e.Handled = true; OpenEditor(cell.Date); } };
        _cells[cell.Date] = border;
        return border;
    }

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
            : s.ShowSolarTerms && info.Term is { } t ? (t.Name, Brush(s.TermColor, Colors.LightGreen))
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
        bool repeating = RecurrenceRule.From(occ.Event).IsRepeating;

        var tb = new TextBlock
        {
            Text = prefix + occ.Event.Title, Foreground = text, FontFamily = font, FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        };

        UIElement child;
        if (repeating)
        {
            var panel = new InlineTrailingPanel { Gap = 4 };
            panel.Children.Add(tb);
            panel.Children.Add(new TextBlock
            {
                Text = "\uE8EE", Foreground = text, FontFamily = IconFont,
                FontSize = Math.Max(8.0, size - 2.0), Opacity = 0.75, ToolTip = "반복 일정",
            });
            child = panel;
        }
        else
        {
            child = tb;
        }

        var row = new Border
        {
            Margin = new Thickness(4, _settings.LineGap, 2, 0), Padding = new Thickness(4, 0, 2, 0), CornerRadius = new CornerRadius(2),
            BorderBrush = color, BorderThickness = new Thickness(2.5, 0, 0, 0), Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = child,
        };
        row.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (e.ClickCount == 2 && !_editMode) EditEvent(occ); };
        row.ContextMenu = BuildEventMenu(occ);
        return row;
    }

    internal void NewEvent(DateOnly date) => RunEventEditor(null, date);
    private void EditEvent(EventOccurrence occ) => RunEventEditor(occ.Event.Id, occ.Date);

    private void RunEventEditor(string? eventId, DateOnly date)
    {
        if (_series is null || _editMode || !Guard()) return;
        CloseChatPromptBox();
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
            Margin = new Thickness(4, s.LineGap, 2, 0),
            Background = item.BgColor is { } bgHex ? new SolidColorBrush(WithAlpha(UiColors.Parse(bgHex, Colors.Transparent), 0x66)) : Brushes.Transparent,
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
            m.MouseLeftButtonDown += (_, e) => e.Handled = true;   // keep the press from reaching the cell's double-click handler
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

        UIElement textCol;
        if (item.SeriesId is not null)
        {
            var p = new InlineTrailingPanel { Gap = 4 };
            p.Children.Add(t);
            p.Children.Add(new TextBlock
            {
                Text = "\uE8EE", Foreground = fg, FontFamily = IconFont,
                FontSize = Math.Max(8.0, size - 2.0),
                Opacity = item.Done ? 0.4 : 0.75,
                ToolTip = "반복 항목",
            });
            textCol = p;
        }
        else
        {
            textCol = t;
        }
        Grid.SetColumn(textCol, 1);
        row.Children.Add(textCol);

        // small marks at the right end of the line: D-day label, reminder (⏰)
        var marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item.CountdownId is { } cdId && _countdownLabels.TryGetValue(cdId, out var dday))
            marks.Children.Add(new TextBlock { Text = dday, Foreground = fg, FontFamily = font, FontSize = size - 1, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0), ToolTip = "기념일까지 카운트다운" });
        if (s.ShowReminderMark && _itemsWithReminder.Contains(item.Id))
            marks.Children.Add(new TextBlock { Text = "⏰", FontSize = size - 1, Margin = new Thickness(3, 0, 0, 0), Opacity = 0.85, ToolTip = "알림 설정됨" });
        if (marks.Children.Count > 0)
        {
            Grid.SetColumn(marks, 2);
            row.Children.Add(marks);
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
        menu.Items.Add(new Separator());
        menu.Items.Add(ColorMenu("셀 배경색", hex => SetCellBg(date, hex)));
        return menu;
    }

    private ContextMenu BuildItemMenu(Item item, DateOnly date)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry(item.Done ? "완료 해제" : "완료로 표시", () => ToggleDone(item)));
        menu.Items.Add(MenuEntry("편집…", () => OpenEditor(date)));
        menu.Items.Add(MenuEntry("이력 보기…", () => ShowItemHistory(item)));
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

    private void ShowItemHistory(Item item)
    {
        if (_items is null || _history is null || _editMode || !Guard()) return;
        CloseChatPromptBox();
        if (_editingDay is not null) CommitEditor();
        if (HistoryWindow.Show(this, _items, _history, item)) Render();
        SendBack();
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

    private static SolidColorBrush Brush(string hex, Color fallback) => UiColors.Frozen(UiColors.Parse(hex, fallback));

    // Re-attach after Explorer restarts (TaskbarCreated is broadcast on restart).
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && !_editMode && IsVisible)
            Dispatcher.BeginInvoke(AttachToDesktop);
        return 0;
    }

    private static void Log(string text) => AppLog.Write(text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
