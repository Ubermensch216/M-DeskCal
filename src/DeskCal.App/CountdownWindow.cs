using System.Windows;
using System.Windows.Controls;
using DeskCal.Core;
using DeskCal.Data;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>Manage D-day countdowns: add / edit / delete, and choose where each one is shown (desktop card, strip under the calendar).</summary>
internal sealed class CountdownWindow : Window
{
    private static CountdownWindow? _open;

    private static readonly (CountdownType Type, string Label, string Hint)[] Types =
    [
        (CountdownType.Custom, "일반 D-day", "D-12 / D-Day / D+3"),
        (CountdownType.Exam, "시험", "D-12 / D-Day / D+3"),
        (CountdownType.Birthday, "생일 (매년 반복, 나이 표시)", "다음 생일까지, 만 N세"),
        (CountdownType.Festival, "명절·기념일 (매년 반복)", "다음 해당일까지"),
        (CountdownType.Memorial, "만난 날·시작일 (N일째)", "시작일을 1일째로 센 경과일, N주년"),
    ];

    private readonly CountdownRepository _repo;
    private readonly Action _changed;
    private readonly ListBox _list = new() { MinHeight = 120, MaxHeight = 220, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _title = new() { Padding = new Thickness(5, 3, 5, 3) };
    private readonly TextBox _date = new() { Padding = new Thickness(5, 3, 5, 3) };
    private readonly CheckBox _lunar = new() { Content = "음력 날짜", Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox _type = new() { Padding = new Thickness(5, 3, 5, 3) };
    private readonly CheckBox _card = new() { Content = "바탕화면에 카드로 표시", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _bottom = new() { Content = "달력 아래 줄에 표시", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _hint = new() { Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 6, 0, 0) };
    private string? _editingId;

    public static void ShowOrActivate(CountdownRepository repo, Action changed)
    {
        if (_open is { } w) { w.Show(); w.Activate(); return; }
        _open = new CountdownWindow(repo, changed);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private CountdownWindow(CountdownRepository repo, Action changed)
    {
        _repo = repo; _changed = changed;
        Title = "M-DeskCal 디데이"; Width = 480; SizeToContent = SizeToContent.Height; MinHeight = 480; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; Topmost = true;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13;

        foreach (var t in Types) _type.Items.Add(t.Label);
        _type.SelectionChanged += (_, _) => _hint.Text = _type.SelectedIndex >= 0 ? "표시: " + Types[_type.SelectedIndex].Hint : "";

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(_list);

        panel.Children.Add(Label("제목")); panel.Children.Add(_title);
        panel.Children.Add(Label("날짜 (yyyy-MM-dd, 음력이면 음력 연-월-일)")); panel.Children.Add(_date);
        panel.Children.Add(_lunar);
        panel.Children.Add(Label("종류")); panel.Children.Add(_type); panel.Children.Add(_hint);
        panel.Children.Add(_card); panel.Children.Add(_bottom);
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Dlg.Btn("새로 만들기", (_, _) => Clear()));
        buttons.Children.Add(Dlg.Btn("저장", (_, _) => Save(), isDefault: true));
        buttons.Children.Add(Dlg.Btn("삭제", (_, _) => Delete()));
        buttons.Children.Add(new Button { Content = "닫기", Padding = new Thickness(12, 5, 12, 5), IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;

        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: Countdown cd }) Load(cd); };
        Clear();
        Reload();
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 6, 0, 2) };

    private void Reload(string? select = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        _list.Items.Clear();
        foreach (var cd in _repo.List())
        {
            var s = CountdownCalc.Compute(cd, today);
            string text = s is null ? $"⚠ {cd.Title} (날짜를 해석할 수 없음)" : CountdownCalc.Summary(cd, s);
            var item = new ListBoxItem { Content = text, Tag = cd };
            _list.Items.Add(item);
            if (cd.Id == select) item.IsSelected = true;
        }
    }

    private void Clear()
    {
        _editingId = null;
        _title.Text = ""; _date.Text = DateTime.Today.ToString("yyyy-MM-dd");
        _lunar.IsChecked = false; _type.SelectedIndex = 0; _card.IsChecked = true; _bottom.IsChecked = false;
        _error.Text = "";
        _list.SelectedItem = null;
        _title.Focus();
    }

    private void Load(Countdown cd)
    {
        _editingId = cd.Id;
        _title.Text = cd.Title; _date.Text = cd.Date; _lunar.IsChecked = cd.Lunar;
        _type.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Type == cd.Type));
        _card.IsChecked = cd.ShowCard; _bottom.IsChecked = cd.ShowBottom;
        _error.Text = "";
    }

    private void Save()
    {
        string date = _date.Text.Trim();
        if (CountdownCalc.Validate(_title.Text, date, _lunar.IsChecked == true) is { } problem) { _error.Text = problem; return; }
        var cd = new Countdown(_editingId ?? Guid.NewGuid().ToString("N"), _title.Text.Trim(), date, Types[Math.Max(0, _type.SelectedIndex)].Type,
            _card.IsChecked == true, _bottom.IsChecked == true, _lunar.IsChecked == true);
        _repo.Upsert(cd);
        _editingId = cd.Id;
        _error.Text = "";
        Reload(cd.Id);
        _changed();
    }

    private void Delete()
    {
        if (_editingId is null) { _error.Text = "삭제할 디데이를 목록에서 고르세요."; return; }
        if (MessageBox.Show(this, $"'{_title.Text}' 디데이를 삭제할까요?", "디데이 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _repo.Delete(_editingId);
        Clear();
        Reload();
        _changed();
    }
}
