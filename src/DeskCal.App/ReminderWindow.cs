using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DeskCal.Core;
using DeskCal.Data;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace DeskCal.App;

/// <summary>
/// The reminder pop-up: a small always-on-top card in the bottom-right corner that does not steal keyboard focus.
/// Reminders that arrive while it is open are added to it. "10분 뒤 다시" shows them again later (not persisted).
/// </summary>
internal sealed class ReminderPresenter(Func<bool>? bugiAlertEnabled = null)
{
    private ReminderWindow? _window;

    public void Show(IReadOnlyList<ReminderMessage> messages, bool playSound)
    {
        if (messages.Count == 0) return;
        if (_window is null || !_window.IsLoaded)
        {
            _window = new ReminderWindow(this);
            _window.Closed += (_, _) => _window = null;
            _window.Add(messages);
            _window.Show();
        }
        else _window.Add(messages);

        if (playSound && messages.Any(m => m.Sound != "none")) Sounds.PlayReminder();
        if (bugiAlertEnabled?.Invoke() == true) BugiNotifier.NotifyInBackground(messages);
    }

    internal void Snooze(IReadOnlyList<ReminderMessage> messages, TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Show(messages.Select(m => m with { Late = true }).ToList(), playSound: true);
        };
        timer.Start();
    }
}

internal sealed class ReminderWindow : Window
{
    private const int MaxLines = 7;
    private readonly ReminderPresenter _presenter;
    private readonly List<ReminderMessage> _messages = [];
    private readonly StackPanel _lines = new();

    public ReminderWindow(ReminderPresenter presenter)
    {
        _presenter = presenter;
        Title = "M-DeskCal 알림";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = 360; SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");

        var title = new TextBlock { Text = "⏰ 알림", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

        var ok = Pill("확인", Color.FromRgb(0x3B, 0x82, 0xF6));
        ok.Click += (_, _) => Close();
        var snooze = Pill("10분 뒤 다시", Color.FromRgb(0x44, 0x50, 0x60));
        snooze.Click += (_, _) => { _presenter.Snooze(_messages.ToList(), TimeSpan.FromMinutes(10)); Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(snooze); buttons.Children.Add(ok);

        var stack = new StackPanel();
        stack.Children.Add(title); stack.Children.Add(_lines); stack.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(14), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x18, 0x24, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0xA8, 0xFF)), BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 },
            Child = stack,
        };
        SizeChanged += (_, _) => Position();
        Loaded += (_, _) => Position();
    }

    public void Add(IReadOnlyList<ReminderMessage> messages)
    {
        _messages.AddRange(messages);
        _lines.Children.Clear();
        foreach (var m in _messages.Take(MaxLines)) _lines.Children.Add(Line(m));
        if (_messages.Count > MaxLines)
            _lines.Children.Add(new TextBlock { Text = $"… 외 {_messages.Count - MaxLines}건", Foreground = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 0) });
    }

    private static UIElement Line(ReminderMessage m)
    {
        string when = m.Occurrence.ToString("M/d", CultureInfo.InvariantCulture) + (m.StartTime is { } t ? $" {t:HH:mm}" : " 종일");
        string timing = m.OffsetMinutes <= 0 ? "지금 시작" : ReminderText.DescribeOffset(m.OffsetMinutes);
        if (m.OwnerType == ReminderOwner.Item) timing = "할 일 · " + timing;
        if (m.Late) timing += " · 놓친 알림";

        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
        panel.Children.Add(new TextBlock { Text = $"{when}  {m.Title}", Foreground = Brushes.White, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = timing, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xCC)), FontSize = 11.5 });
        return panel;
    }

    private static Button Pill(string text, Color bg) => new()
    {
        Content = text, Foreground = Brushes.White, Background = new SolidColorBrush(bg), BorderThickness = new Thickness(0),
        Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
    };

    private void Position()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }
}
