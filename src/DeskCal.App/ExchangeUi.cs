using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskCal.Core.Exchange;
using DeskCal.Data;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace DeskCal.App;

/// <summary>The dialogs around <see cref="ExchangeService"/>: choose what to export, preview and confirm what to import.</summary>
internal static class ExchangeUi
{
    private static readonly (ExchangeFormat Format, string Label, string Hint)[] Formats =
    [
        (ExchangeFormat.Json, "JSON — 백업 (모든 데이터, 다시 가져오면 그대로 복원)", "할 일·일정(반복·알림·제외일 포함)·디데이·메모"),
        (ExchangeFormat.Csv, "CSV — 엑셀에서 열기", "할 일·일정만. UTF-8(BOM)이라 엑셀에서 한글이 깨지지 않음"),
        (ExchangeFormat.Ics, "ICS — 다른 달력 프로그램과 교환", "할 일·일정만. 구글/아웃룩/애플 달력과 호환"),
        (ExchangeFormat.Xlsx, "Excel (.xlsx) — 보기·인쇄용", "'목록' 시트 + 달마다 달력 시트. 보기 전용(다시 가져오기는 JSON/CSV 사용)"),
    ];

    private static readonly string[] Ranges = ["전체 (백업에는 이쪽을 권장)", "최근 6개월", "최근 1년", "올해", "기간 지정…"];

    // ---- export ------------------------------------------------------------------

    public static void Export(ExchangeService service, Func<DateOnly, bool> isDayOff, Func<DateOnly, string?> holidayName, DayOfWeek firstDay)
    {
        if (Ask() is not { } o) return;

        var dlg = new SaveFileDialog
        {
            Title = "내보내기", FileName = $"M-DeskCal-{DateTime.Today:yyyyMMdd}{ExchangeFiles.Extension(o.Format)}",
            Filter = o.Format switch
            {
                ExchangeFormat.Json => "M-DeskCal 백업 (*.json)|*.json",
                ExchangeFormat.Csv => "CSV (*.csv)|*.csv",
                ExchangeFormat.Xlsx => "Excel (*.xlsx)|*.xlsx",
                _ => "iCalendar (*.ics)|*.ics",
            },
            AddExtension = true, OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bundle = service.Collect(o.From, o.To);
            ExchangeFiles.WriteFile(dlg.FileName, o.Format, bundle, DateTimeOffset.Now, isDayOff, new XlsxOptions(o.From, o.To, firstDay, holidayName));
            MessageBox.Show($"내보냈습니다.\n\n할 일 {bundle.Items.Count}건, 일정 {bundle.Events.Count}건" +
                            (o.Format == ExchangeFormat.Json ? $", 디데이 {bundle.Countdowns.Count}건, 메모 {bundle.Notes.Count}건" : "") + $"\n{dlg.FileName}",
                "내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("파일을 저장하지 못했습니다.\n\n" + ex.Message, "내보내기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record ExportOptions(ExchangeFormat Format, DateOnly? From, DateOnly? To);

    private static ExportOptions? Ask()
    {
        ExportOptions? result = null;
        var win = Dlg.Create("내보내기", 500, 460);
        win.SizeToContent = SizeToContent.Height; win.ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "형식", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var radios = new List<RadioButton>();
        foreach (var (_, label, hint) in Formats)
        {
            var r = new RadioButton { Margin = new Thickness(0, 3, 0, 0), GroupName = "fmt" };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label });
            sp.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray });
            r.Content = sp;
            radios.Add(r); panel.Children.Add(r);
        }
        radios[0].IsChecked = true;

        panel.Children.Add(new TextBlock { Text = "기간", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        var range = new ComboBox { Padding = new Thickness(6, 3, 6, 3) };
        foreach (var r in Ranges) range.Items.Add(r);
        range.SelectedIndex = 0;
        panel.Children.Add(range);

        var from = new DatePicker { SelectedDate = DateTime.Today.AddMonths(-1), Margin = new Thickness(0, 0, 8, 0) };
        var to = new DatePicker { SelectedDate = DateTime.Today };
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        custom.Children.Add(from); custom.Children.Add(new TextBlock { Text = "~", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }); custom.Children.Add(to);
        panel.Children.Add(custom);
        range.SelectionChanged += (_, _) => custom.Visibility = range.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        panel.Children.Add(new TextBlock
        {
            Text = "기간을 고르면 그 기간의 할 일과 일정만 내보냅니다. 디데이와 메모는 '전체'일 때만 JSON 에 포함됩니다.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 8, 0, 0),
        });

        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, MinHeight = 18, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(error);

        var ok = Dlg.Btn("다음", (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            (DateOnly? f, DateOnly? t) = range.SelectedIndex switch
            {
                1 => ((DateOnly?)today.AddMonths(-6), (DateOnly?)today),
                2 => (today.AddYears(-1), today),
                3 => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
                4 => (from.SelectedDate is { } a ? DateOnly.FromDateTime(a) : null, to.SelectedDate is { } b ? DateOnly.FromDateTime(b) : null),
                _ => (null, null),
            };
            if (range.SelectedIndex == 4 && (f is null || t is null || f > t)) { error.Text = "시작일과 종료일을 올바르게 고르세요."; return; }
            result = new ExportOptions(Formats[Math.Max(0, radios.FindIndex(r => r.IsChecked == true))].Format, f, t);
            win.DialogResult = true;
        }, isDefault: true);
        var cancel = new Button { Content = "취소", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.ShowDialog();
        return result;
    }

    // ---- import ------------------------------------------------------------------

    /// <param name="afterChange">Called after data was added (redraw, re-arm the reminder timer, refresh cards).</param>
    public static void Import(ExchangeService service, DeskCalDb db, string backupDir, Action afterChange)
    {
        var dlg = new OpenFileDialog
        {
            Title = "가져오기",
            Filter = "지원하는 파일 (*.json;*.csv;*.ics)|*.json;*.csv;*.ics|M-DeskCal 백업 (*.json)|*.json|CSV (*.csv)|*.csv|iCalendar (*.ics)|*.ics|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        ExchangeReadResult read;
        try { read = ExchangeFiles.ReadFile(dlg.FileName); }
        catch (ExchangeFormatException ex) { MessageBox.Show(ex.Message, "가져오기", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show("파일을 읽지 못했습니다.\n\n" + ex.Message, "가져오기", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var b = read.Bundle;
        if (b.Total == 0)
        {
            MessageBox.Show("가져올 항목이 없습니다." + WarningText(read.Warnings), "가져오기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string preview = $"{Path.GetFileName(dlg.FileName)}\n\n할 일 {b.Items.Count}건, 일정 {b.Events.Count}건" +
                         (b.Countdowns.Count + b.Notes.Count > 0 ? $", 디데이 {b.Countdowns.Count}건, 메모 {b.Notes.Count}건" : "") +
                         " 을(를) 가져옵니다.\n\n이미 있는 항목(같은 ID)은 건너뛰고 기존 내용은 바꾸지 않습니다.\n가져오기 전에 현재 데이터를 자동으로 백업합니다." +
                         WarningText(read.Warnings) + "\n\n계속할까요?";
        if (MessageBox.Show(preview, "가져오기", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        try
        {
            string backup = Backup(db, backupDir);
            var r = service.Apply(b);
            afterChange();
            MessageBox.Show($"가져왔습니다.\n\n추가 {r.TotalAdded}건 (할 일 {r.ItemsAdded}, 일정 {r.EventsAdded}, 디데이 {r.CountdownsAdded}, 메모 {r.NotesAdded})" +
                            (r.TotalSkipped > 0 ? $"\n이미 있어 건너뜀 {r.TotalSkipped}건" : "") + $"\n\n되돌리려면 백업을 쓰세요:\n{backup}",
                "가져오기", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("가져오는 중 오류가 나서 아무것도 추가하지 않았습니다.\n\n" + ex.Message, "가져오기", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string WarningText(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return "";
        var shown = warnings.Take(12).Select(w => "• " + w);
        return $"\n\n알림 {warnings.Count}건:\n" + string.Join("\n", shown) + (warnings.Count > 12 ? $"\n… 외 {warnings.Count - 12}건" : "");
    }

    /// <summary>Copy of the database taken just before an import; only the ten newest are kept.</summary>
    private static string Backup(DeskCalDb db, string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        db.Backup(path);
        foreach (var old in new DirectoryInfo(dir).GetFiles("before-import-*.db").OrderByDescending(f => f.Name).Skip(10))
        {
            try { old.Delete(); } catch (IOException) { /* in use: leave it */ }
        }
        return path;
    }
}
