using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskCal.Core;
using DeskCal.Data;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>
/// The notes module: free-form notes with a title, text and colour, a filter box, change history with restore, and delete
/// (soft: "휴지통에서 복원" brings the most recent deleted note back). Edits are saved automatically when the selection
/// changes, the window closes, or Ctrl+S is pressed.
/// </summary>
internal sealed class NotesWindow : Window
{
    private static NotesWindow? _open;

    private readonly NoteRepository _notes;
    private readonly HistoryRepository _history;
    private readonly TextBox _filter = new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox _list = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _title = new() { Padding = new Thickness(5, 4, 5, 4), FontWeight = FontWeights.SemiBold, FontSize = 15 };
    private readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(5), Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _color = new() { MinWidth = 90 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private string? _currentId;
    private string _loadedTitle = "", _loadedText = "";
    private bool _loading;

    public static void ShowOrActivate(NoteRepository notes, HistoryRepository history)
    {
        if (_open is { } w) { w.Show(); w.Activate(); return; }
        _open = new NotesWindow(notes, history);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    /// <summary>Dev aid: build the window without showing it and render it.</summary>
    internal static void Snapshot(NoteRepository notes, HistoryRepository history, string path)
    {
        var w = new NotesWindow(notes, history);
        DevHarness.RenderToPng((FrameworkElement)w.Content, path, 740, 480, System.Windows.Media.Colors.White);
    }

    private NotesWindow(NoteRepository notes, HistoryRepository history)
    {
        _notes = notes; _history = history;
        Title = "M-DeskCal 메모"; Width = 760; Height = 520; MinWidth = 560; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13;

        _color.Items.Add("색 없음");
        foreach (var (name, hex) in MainWindow.Palette.Where(p => p.Hex is not null)) _color.Items.Add(new ComboBoxItem { Content = name, Tag = hex });
        _color.SelectedIndex = 0;

        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0), Width = 230 };
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        leftButtons.Children.Add(Dlg.Btn("새 메모", (_, _) => NewNote()));
        leftButtons.Children.Add(Dlg.Btn("삭제", (_, _) => DeleteCurrent()));
        DockPanel.SetDock(_filter, Dock.Top); DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(_filter); left.Children.Add(leftButtons); left.Children.Add(_list);

        var top = new DockPanel();
        DockPanel.SetDock(_color, Dock.Right);
        _color.Margin = new Thickness(6, 0, 0, 0);
        top.Children.Add(_color); top.Children.Add(_title);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(Dlg.Btn("저장 (Ctrl+S)", (_, _) => SaveCurrent(announce: true)));
        bottom.Children.Add(Dlg.Btn("이력 보기…", (_, _) => ShowHistory()));
        bottom.Children.Add(Dlg.Btn("삭제한 메모 되살리기", (_, _) => RestoreDeleted()));
        bottom.Children.Add(_status);

        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(top); right.Children.Add(bottom); right.Children.Add(_text);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left); root.Children.Add(right);
        Content = root;

        _filter.TextChanged += (_, _) => Reload(_currentId);
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveCurrent(reload: false);     // reloading here would put the selection back on the note being left
            if (_list.SelectedItem is ListBoxItem { Tag: Note n }) Load(n); else Load(null);
        };
        _color.SelectionChanged += (_, _) =>
        {
            if (_loading || _currentId is null) return;
            _notes.SetColor(_currentId, (_color.SelectedItem as ComboBoxItem)?.Tag as string);
            Reload(_currentId);
        };
        Closing += (_, _) => SaveCurrent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SaveCurrent(announce: true); } };

        Reload(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0; else Load(null);
    }

    private void Reload(string? select)
    {
        _loading = true;
        string q = _filter.Text.Trim();
        _list.Items.Clear();
        foreach (var n in _notes.List().Where(n => q.Length == 0 || (n.Title + "\n" + n.Content).Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var bar = new Border { Width = 4, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(2), Background = TryBrush(n.Color) ?? Brushes.Transparent };
            DockPanel.SetDock(bar, Dock.Left);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = n.Title.Length > 0 ? n.Title : "(제목 없음)", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = FirstLine(n.Content), Foreground = Brushes.Gray, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(n.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Foreground = Brushes.Gray, FontSize = 10.5 });
            row.Children.Add(bar); row.Children.Add(info);
            var item = new ListBoxItem { Content = row, Tag = n };
            _list.Items.Add(item);
            if (n.Id == select) item.IsSelected = true;
        }
        _loading = false;
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();

    private static SolidColorBrush? TryBrush(string? hex)
    {
        try { return hex is null ? null : (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!; } catch { return null; }
    }

    private void Load(Note? n)
    {
        _loading = true;
        _currentId = n?.Id;
        _loadedTitle = _title.Text = n?.Title ?? "";
        _loadedText = _text.Text = n?.Content ?? "";
        _title.IsEnabled = _text.IsEnabled = _color.IsEnabled = n is not null;
        _color.SelectedIndex = Math.Max(0, _color.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => string.Equals(i.Tag as string, n?.Color, StringComparison.OrdinalIgnoreCase)) + 1);
        _status.Text = n is null ? "  메모를 고르거나 '새 메모'를 누르세요." : "";
        _loading = false;
    }

    private void SaveCurrent(bool announce = false, bool reload = true)
    {
        if (_currentId is null) return;
        if (_title.Text == _loadedTitle && _text.Text == _loadedText) { if (announce) _status.Text = "  변경 사항 없음"; return; }
        _notes.Update(_currentId, _title.Text.Trim(), _text.Text);
        _loadedTitle = _title.Text; _loadedText = _text.Text;
        _status.Text = "  저장됨 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (reload) Reload(_currentId);
    }

    private void NewNote()
    {
        SaveCurrent();
        _filter.Text = "";
        var n = _notes.Add("", "");
        Reload(n.Id);
        _title.Focus();
    }

    private void DeleteCurrent()
    {
        if (_currentId is null) return;
        var title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : "(제목 없음)";
        if (MessageBox.Show(this, $"'{title}' 메모를 삭제할까요?\n('삭제한 메모 되살리기'로 되돌릴 수 있습니다.)", "메모 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SaveCurrent(reload: false);
        _lastDeleted = _currentId;
        _notes.SoftDelete(_currentId);
        _currentId = null;
        Reload(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0; else Load(null);
    }

    private static string? _lastDeleted;

    private void RestoreDeleted()
    {
        if (_lastDeleted is null || !_notes.Undelete(_lastDeleted)) { MessageBox.Show(this, "이 창에서 삭제한 메모가 없습니다.", "메모"); return; }
        string id = _lastDeleted; _lastDeleted = null;
        SaveCurrent();
        _filter.Text = "";
        Reload(id);
    }

    private void ShowHistory()
    {
        if (_currentId is null) return;
        SaveCurrent();
        string id = _currentId;
        var win = Dlg.Create("메모 이력", 480, 420);
        win.Owner = this; win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
        var empty = new TextBlock { Text = "이전 내용이 없습니다. 메모를 고치면 이전 내용이 여기에 남습니다.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

        void Fill()
        {
            list.Items.Clear();
            foreach (var h in _history.List(EntityNames.Note, id))
            {
                var sp = new StackPanel { Tag = h, Margin = new Thickness(0, 2, 0, 2) };
                sp.Children.Add(new TextBlock { Text = Dlg.When(h.At), FontSize = 11, Foreground = Brushes.Gray });
                sp.Children.Add(new TextBlock { Text = h.Content.Length > 300 ? h.Content[..300] + "…" : h.Content, TextWrapping = TextWrapping.Wrap });
                list.Items.Add(sp);
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var restore = Dlg.Btn("이 내용으로 복원", (_, _) =>
        {
            if (list.SelectedItem is not StackPanel { Tag: HistoryEntry h }) { MessageBox.Show(win, "복원할 이전 내용을 고르세요.", "이력"); return; }
            if (_notes.RestoreFromHistory(h.Id) is null) return;
            Reload(id); Load(_notes.Get(id)); Fill();
        });
        var close = new Button { Content = "닫기", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(restore); buttons.Children.Add(close);

        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        var mid = new Grid(); mid.Children.Add(list); mid.Children.Add(empty);
        dock.Children.Add(mid);
        win.Content = dock;
        Fill();
        win.ShowDialog();
    }
}
