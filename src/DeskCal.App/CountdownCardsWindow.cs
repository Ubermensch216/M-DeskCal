using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DeskCal.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DeskCal.App;

/// <summary>
/// The D-day cards on the desktop: one window holding a stack of cards, pinned just above the wallpaper exactly like the
/// calendar board (see <see cref="DesktopHost"/>). It can be dragged in edit mode; its position is saved by the caller.
/// </summary>
internal sealed class CountdownCardsWindow : Window
{
    private readonly StackPanel _stack = new();
    private readonly Border _frame;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _x = 80, _y = 80;
    private bool _editMode, _attached;

    /// <summary>Raised after an edit-mode drag, with the new top-left corner in physical pixels.</summary>
    public event Action<int, int>? Moved;

    public CountdownCardsWindow()
    {
        Title = "M-DeskCal 디데이 카드";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        _frame = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(14), Child = _stack };
        Content = _frame;

        _timer.Tick += (_, _) => { if (!_editMode && IsVisible && !IsActive) DesktopHost.Repin(this); };
    }

    public void SetPosition(int x, int y) { _x = x; _y = y; }

    /// <summary>Rebuild the cards. Hides the window when there is nothing to show.</summary>
    public void Update(IReadOnlyList<(Countdown Countdown, CountdownStatus Status)> cards, BoardSettings s, bool allowed)
    {
        if (cards.Count == 0 || !allowed) { _stack.Children.Clear(); if (IsVisible) Hide(); return; }
        Rebuild(cards, s);
        if (!IsVisible) Present(); else if (!_editMode) Dispatcher.BeginInvoke(Pin);
    }

    /// <summary>Fill the stack with cards without touching the screen (also used by the offscreen snapshots).</summary>
    internal void Rebuild(IReadOnlyList<(Countdown Countdown, CountdownStatus Status)> cards, BoardSettings s)
    {
        _stack.Children.Clear();

        var bg = Parse(s.BackgroundColor, Color.FromRgb(0x10, 0x18, 0x20));
        bg.A = (byte)Math.Round(Math.Max(0.35, s.BackgroundOpacity) * 255);   // keep cards readable even on a "transparent" board
        var text = new SolidColorBrush(Parse(s.TextColor, Colors.White));
        var font = new FontFamily(s.FontFamily);

        foreach (var (cd, st) in cards)
        {
            var card = new StackPanel { Margin = new Thickness(14, 10, 14, 10), MinWidth = 150 };
            card.Children.Add(new TextBlock
            {
                Text = st.Label, Foreground = st.Days == 0 && !st.Elapsed ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)) : text,
                FontFamily = font, FontSize = 30, FontWeight = FontWeights.SemiBold,
            });
            card.Children.Add(new TextBlock { Text = cd.Title, Foreground = text, FontFamily = font, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 });
            card.Children.Add(new TextBlock
            {
                Text = (st.Detail.Length > 0 ? st.Detail + " · " : "") + st.Target.ToString("yyyy-MM-dd"),
                Foreground = text, Opacity = 0.6, FontFamily = font, FontSize = 11.5,
            });
            _stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8), Child = card,
                Effect = s.Shadow ? new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5 } : null,
            });
        }
        _frame.Margin = new Thickness(s.Shadow ? 8 : 0);
    }

    /// <summary>Dev aid: render the current cards over a flat stand-in wallpaper.</summary>
    internal void SaveSnapshot(string path) => DevHarness.RenderToPng(_frame, path);

    private void Present()
    {
        Show();
        UpdateLayout();
        Pin();
        _timer.Start();
    }

    private void Pin()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        _attached = DesktopHost.Pin(this, new DesktopHost.RectInt(_x, _y, w, h));
    }

    public void SetEditMode(bool on)
    {
        if (on == _editMode) return;
        _editMode = on;
        if (on)
        {
            if (!IsVisible) return;
            DesktopHost.Float(this);
            _frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57));
        }
        else
        {
            _frame.BorderBrush = Brushes.Transparent;
            if (!IsVisible) return;
            var r = DesktopHost.GetScreenRect(this);
            (_x, _y) = (r.X, r.Y);
            Pin();
            Moved?.Invoke(_x, _y);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editMode) DragMove();
    }

    internal string Diagnostics() => $"cards visible={IsVisible} attached={_attached} pos=({_x},{_y})";

    private static Color Parse(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }
}
