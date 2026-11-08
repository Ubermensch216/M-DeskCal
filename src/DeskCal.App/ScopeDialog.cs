using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace DeskCal.App;

public enum SeriesScope { Cancel, This, Future, All }

/// <summary>"This event / this and following events / all events" prompt for repeating events.</summary>
internal static class ScopeDialog
{
    public static SeriesScope Ask(Window? owner, string verb)
    {
        var result = SeriesScope.Cancel;
        var win = new Window
        {
            Title = "반복 일정 " + verb, Width = 330, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner, Topmost = true, ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic"), FontSize = 13,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"반복 일정을 {verb}합니다. 적용 범위를 선택하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        void Add(string text, SeriesScope scope)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
            b.Click += (_, _) => { result = scope; win.DialogResult = true; };
            panel.Children.Add(b);
        }
        Add("이 일정만", SeriesScope.This);
        Add("이 일정 및 이후 모든 일정", SeriesScope.Future);
        Add("모든 일정", SeriesScope.All);
        var cancel = new Button { Content = "취소", Padding = new Thickness(8, 6, 8, 6), IsCancel = true, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(cancel);

        win.Content = panel;
        win.ShowDialog();
        return result;
    }
}
