using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace DeskCal.App;

/// <summary>M0 prototype board: a month grid placed on the desktop layer.</summary>
public partial class MainWindow : Window
{
    private bool _editMode;
    private DesktopHost.RectInt _rect = new(200, 150, 640, 480); // physical pixels
    private uint _taskbarCreated;
    private bool _attached;

    public MainWindow()
    {
        InitializeComponent();
        BuildMonth(DateTime.Today);
        SourceInitialized += (_, _) =>
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
        };
    }

    public void AttachToDesktop()
    {
        _attached = DesktopHost.Attach(this, _rect);
        Log($"attach -> {_attached}\n{DesktopHost.LastDiagnostics}");
    }

    public string Diagnostics() =>
        $"attached={_attached} edit={_editMode}\nrect={_rect}\n{DesktopHost.LastDiagnostics}";

    public void SetEditMode(bool on)
    {
        if (on == _editMode) return;
        _editMode = on;
        if (on)
        {
            _rect = DesktopHost.GetScreenRect(this);
            DesktopHost.Detach(this, _rect);
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
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editMode) DragMove();
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Max(320, Width + e.HorizontalChange);
        Height = Math.Max(240, Height + e.VerticalChange);
        _ = dpi;
    }

    private void BuildMonth(DateTime today)
    {
        var ci = CultureInfo.CurrentCulture;
        MonthTitle.Text = today.ToString("yyyy년 M월", ci);
        foreach (var d in new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                  DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday })
            Header.Children.Add(Cell(ci.DateTimeFormat.GetAbbreviatedDayName(d), d == DayOfWeek.Sunday, false, true));

        var first = new DateTime(today.Year, today.Month, 1);
        for (int i = 0; i < (int)first.DayOfWeek; i++) Days.Children.Add(new Border());
        for (int d = 1; d <= DateTime.DaysInMonth(today.Year, today.Month); d++)
        {
            var date = new DateTime(today.Year, today.Month, d);
            Days.Children.Add(Cell(d.ToString(), date.DayOfWeek == DayOfWeek.Sunday, date == today, false));
        }
    }

    private static UIElement Cell(string text, bool red, bool isToday, bool header) => new Border
    {
        Margin = new Thickness(2),
        CornerRadius = new CornerRadius(6),
        Background = isToday ? new SolidColorBrush(Color.FromArgb(0xAA, 0x3B, 0x82, 0xF6)) : Brushes.Transparent,
        Child = new TextBlock
        {
            Text = text,
            Foreground = red ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)) : Brushes.White,
            FontSize = header ? 13 : 16,
            Margin = new Thickness(6, 4, 0, 0),
        },
    };

    // Re-attach after Explorer restarts (TaskbarCreated is broadcast on restart).
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && !_editMode)
            Dispatcher.BeginInvoke(() => { _rect = new(_rect.X, _rect.Y, _rect.Width, _rect.Height); AttachToDesktop(); });
        return 0;
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(App.DataDir, "m0.log"), $"[{DateTime.Now:s}] {text}\n"); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
