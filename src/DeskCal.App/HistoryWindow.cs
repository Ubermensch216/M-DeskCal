using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DeskCal.Core;
using DeskCal.Data;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace DeskCal.App;

/// <summary>Shared look for the small tool windows (search, history, countdowns).</summary>
internal static class Dlg
{
    public static Window Create(string title, double width, double height, bool topmost = true)
    {
        var w = new Window();
        ModernUi.ConfigureWindow(w, title, width, height, topmost: topmost, showInTaskbar: false);
        w.MinWidth = Math.Min(width, 360);
        w.MinHeight = Math.Min(height, 260);
        return w;
    }

    public static Button Btn(string text, RoutedEventHandler click, bool isDefault = false) =>
        isDefault ? ModernUi.PrimaryBtn(text, click, isDefault: true) : ModernUi.SecondaryBtn(text, click);

    public static string When(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Earlier versions of one to-do line, with restore. Restoring keeps the replaced text as a new history entry.</summary>
internal static class HistoryWindow
{
    /// <returns>True when something was restored or deleted (the board needs a redraw).</returns>
    public static bool Show(Window? owner, ItemRepository items, HistoryRepository history, Item item)
    {
        bool changed = false;
        var win = Dlg.Create("이력 보기", 460, 420);
        win.Owner = owner;
        if (owner is not null) win.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var current = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        var list = new ListBox { Margin = new Thickness(0, 0, 0, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var empty = new TextBlock { Text = "이전 내용이 없습니다. 항목을 고치면 이전 내용이 여기에 남습니다.", Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

        void Refresh()
        {
            var now = items.Get(item.Id);
            current.Text = $"{item.Day}  ·  현재: {now?.Content ?? "(삭제됨)"}";
            list.Items.Clear();
            foreach (var h in history.List(EntityNames.Item, item.Id))
            {
                var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2), Tag = h };
                row.Children.Add(new TextBlock { Text = Dlg.When(h.At), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray });
                row.Children.Add(new TextBlock { Text = h.Content, TextWrapping = TextWrapping.Wrap });
                list.Items.Add(row);
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        var restore = Dlg.Btn("이 내용으로 복원", (_, _) =>
        {
            if (list.SelectedItem is not StackPanel { Tag: HistoryEntry h }) { System.Windows.MessageBox.Show(win, "복원할 이전 내용을 목록에서 고르세요.", "이력"); return; }
            if (items.RestoreFromHistory(h.Id) is not null) { changed = true; Refresh(); }
        });
        var delete = Dlg.Btn("이력 삭제", (_, _) =>
        {
            if (history.List(EntityNames.Item, item.Id).Count == 0) return;
            if (System.Windows.MessageBox.Show(win, "이 항목의 이전 내용을 모두 영구 삭제합니다. 되돌릴 수 없습니다.", "이력 삭제",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            history.Delete(EntityNames.Item, item.Id);
            changed = true; Refresh();
        });
        var close = new Button { Content = "닫기", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(restore); buttons.Children.Add(delete); buttons.Children.Add(close);

        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(current, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(current); dock.Children.Add(buttons);
        var middle = new Grid();
        middle.Children.Add(list); middle.Children.Add(empty);
        dock.Children.Add(middle);
        win.Content = dock;

        Refresh();
        win.ShowDialog();
        return changed;
    }
}
