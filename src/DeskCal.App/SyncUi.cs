using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DeskCal.Core;
using DeskCal.Data;
using DeskCal.Sync;

namespace DeskCal.App;

/// <summary>Keeps the calendar password with Windows DPAPI: only this Windows user on this computer can read it back.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("M-DeskCal.sync.v1");

    public string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}

/// <summary>Runs syncs in the background, one at a time, and tells the UI when one is done.</summary>
internal sealed class SyncCoordinator(DeskCalDb db, IClock clock, Func<DateOnly, bool> isDayOff, ISecretProtector protector, SettingsStore settings)
{
    public const string IntervalKey = "sync.interval_min";

    public SyncRepository Accounts { get; } = new(db, clock);
    public bool Running { get; private set; }

    /// <summary>Raised on the UI thread after every run: the report, or the error that stopped it.</summary>
    public event Action<SyncAccount, SyncReport?, string?>? Finished;

    public int IntervalMinutes
    {
        get => settings.GetInt(IntervalKey, 30);
        set => settings.SetInt(IntervalKey, value);
    }

    public ISecretProtector Protector => protector;

    public async Task<(SyncReport? Report, string? Error)> RunAsync(SyncAccount account)
    {
        if (Running) return (null, "이미 동기화 중입니다.");
        Running = true;
        SyncReport? report = null; string? error = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var provider = CalDavProvider.Create(account, protector);
            report = await Task.Run(() => new SyncEngine(db, clock, isDayOff).RunAsync(account, provider, cts.Token));
            Accounts.RecordResult(account.Id, report.Summary() + (report.Errors.Count > 0 ? "\n" + string.Join("\n", report.Errors.Take(3)) : ""));
        }
        catch (CalendarServerException ex) { error = ex.Message; }
        catch (OperationCanceledException) { error = "동기화가 너무 오래 걸려 중단했습니다."; }
        catch (Exception ex) { error = "동기화 중 예상하지 못한 오류: " + ex.Message; }
        finally { Running = false; }
        if (error is not null) Accounts.RecordResult(account.Id, "실패: " + error);
        Finished?.Invoke(Accounts.GetAccount(account.Id) ?? account, report, error);
        return (report, error);
    }

    public async Task RunAllAsync()
    {
        foreach (var a in Accounts.ListAccounts().Where(a => a.Enabled)) await RunAsync(a);
    }
}

/// <summary>Set up the calendar server connection, test it and sync now.</summary>
internal static class SyncWindow
{
    private static Window? _open;

    public static void ShowOrActivate(SyncCoordinator sync)
    {
        if (_open is not null) { _open.Activate(); return; }

        var win = Dlg.Create("외부 캘린더 동기화 (CalDAV)", 560, 640);
        _open = win;
        win.Closed += (_, _) => _open = null;

        var account = sync.Accounts.ListAccounts().FirstOrDefault();
        var name = new TextBox { Text = account?.Name ?? "내 캘린더" };
        var url = new TextBox { Text = account?.Url ?? "" };
        var user = new TextBox { Text = account?.Username ?? "" };
        var pass = new PasswordBox();
        var twoWay = new CheckBox { Content = "여기서 만든·고친 일정을 서버에도 올리기 (양방향)", IsChecked = account?.TwoWay ?? false, Margin = new Thickness(0, 8, 0, 2) };
        var enabled = new CheckBox { Content = "자동 동기화 사용", IsChecked = account?.Enabled ?? true, Margin = new Thickness(0, 2, 0, 2) };
        var interval = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        (int Minutes, string Label)[] intervals = [(0, "자동으로는 안 함"), (15, "15분마다"), (30, "30분마다"), (60, "1시간마다"), (240, "4시간마다")];
        foreach (var (_, label) in intervals) interval.Items.Add(label);
        interval.SelectedIndex = Math.Max(0, Array.FindIndex(intervals, i => i.Minutes == sync.IntervalMinutes));

        var status = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 90, Margin = new Thickness(0, 8, 0, 0) };
        void ShowLast(SyncAccount? a) => status.Text = a?.LastSyncAt is { } at ? $"마지막 동기화: {Dlg.When(at)}\n{a.LastResult}" : "아직 동기화한 적이 없습니다.";
        ShowLast(account);

        TextBlock Hint(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 6) };
        UIElement Field(string label, UIElement box) { var p = new StackPanel { Margin = new Thickness(0, 4, 0, 0) }; p.Children.Add(new TextBlock { Text = label }); p.Children.Add(box); return p; }

        var form = new StackPanel();
        form.Children.Add(Hint("CalDAV 를 지원하는 캘린더(Nextcloud, Synology, Radicale, iCloud 등)의 '캘린더 하나'의 주소를 입력합니다. 일정만 동기화하며, 날짜 칸의 할 일·메모·디데이는 서버로 보내지 않습니다."));
        form.Children.Add(Field("이름", name));
        form.Children.Add(Field("캘린더 주소 (https://…)", url));
        form.Children.Add(Field("사용자 이름", user));
        form.Children.Add(Field(account is null ? "비밀번호" : "비밀번호 (바꿀 때만 입력)", pass));
        form.Children.Add(Hint("iCloud·Google 처럼 2단계 인증을 쓰는 서비스는 '앱 전용 비밀번호'를 만들어 입력하세요. 비밀번호는 이 Windows 계정으로만 풀 수 있게 암호화해 저장합니다."));
        form.Children.Add(enabled);
        form.Children.Add(Field("자동 동기화 주기", interval));
        form.Children.Add(twoWay);
        form.Children.Add(Hint("꺼 두면 서버 → 이 프로그램으로 받기만 합니다 (서버는 절대 바뀌지 않음). 켜도 다른 프로그램이 만든 일정은 서버에서 고치거나 지우지 않고, 양쪽이 모두 바뀐 일정은 서버 내용을 적용하며 이쪽 내용을 '[충돌]' 일정으로 남깁니다."));

        SyncAccount? Save()
        {
            if (!CalDavProvider.TryParseUrl(url.Text, out _, out var err)) { System.Windows.MessageBox.Show(win, err, "주소 확인"); return null; }
            if (user.Text.Trim().Length == 0) { System.Windows.MessageBox.Show(win, "사용자 이름을 입력하세요.", "동기화"); return null; }
            if (account is null && pass.Password.Length == 0) { System.Windows.MessageBox.Show(win, "비밀번호를 입력하세요.", "동기화"); return null; }
            string? secret = pass.Password.Length > 0 ? sync.Protector.Protect(pass.Password) : null;
            bool first = account is null;
            if (first && twoWay.IsChecked == true &&
                System.Windows.MessageBox.Show(win, "양방향으로 켜면 이 프로그램의 모든 일정이 처음 동기화할 때 서버에 올라갑니다.\n계속할까요?", "양방향 동기화",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return null;
            if (account is null)
                account = sync.Accounts.AddAccount(SyncRepository.KindCalDav, name.Text, url.Text, user.Text, secret!, twoWay.IsChecked == true);
            else
                sync.Accounts.UpdateAccount(account.Id, name.Text, url.Text, user.Text, secret, twoWay.IsChecked == true, enabled.IsChecked == true);
            sync.IntervalMinutes = intervals[Math.Max(0, interval.SelectedIndex)].Minutes;
            account = sync.Accounts.GetAccount(account.Id);
            pass.Clear();
            return account;
        }

        Button? syncBtn = null, testBtn = null;
        void Busy(bool busy) { if (syncBtn is not null) syncBtn.IsEnabled = !busy; if (testBtn is not null) testBtn.IsEnabled = !busy; }

        testBtn = Dlg.Btn("연결 시험", async (_, _) =>
        {
            var a = Save(); if (a is null) return;
            Busy(true); status.Text = "연결하는 중…"; status.Foreground = ModernUi.AccentBrush;
            try
            {
                var list = await CalDavProvider.Create(a, sync.Protector).ListAsync(CancellationToken.None);
                status.Text = $"연결되었습니다. 서버에 일정 {list.Count}건이 있습니다.";
                status.Foreground = ModernUi.SuccessBrush;
            }
            catch (CalendarServerException ex) { status.Text = "연결하지 못했습니다: " + ex.Message; status.Foreground = ModernUi.DangerBrush; }
            finally { Busy(false); }
        });
        syncBtn = Dlg.Btn("지금 동기화", async (_, _) =>
        {
            var a = Save(); if (a is null) return;
            Busy(true); status.Text = "동기화하는 중…"; status.Foreground = ModernUi.AccentBrush;
            var (report, error) = await sync.RunAsync(a);
            Busy(false);
            status.Text = error is not null ? "실패: " + error
                : report!.Summary() + (report.Notes.Count > 0 ? "\n\n" + string.Join("\n", report.Notes.Take(8)) : "") + (report.Errors.Count > 0 ? "\n\n오류:\n" + string.Join("\n", report.Errors.Take(5)) : "");
            status.Foreground = error is not null ? ModernUi.DangerBrush : ModernUi.SuccessBrush;
        }, isDefault: true);
        var save = Dlg.Btn("저장", (_, _) => { if (Save() is not null) { status.Text = "저장했습니다."; status.Foreground = ModernUi.SuccessBrush; } });
        var remove = Dlg.Btn("연결 끊기", (_, _) =>
        {
            if (account is null) return;
            if (System.Windows.MessageBox.Show(win, "이 서버와의 연결을 끊습니다. 이미 받아 온 일정은 이 프로그램에 그대로 남고, 서버의 일정도 지우지 않습니다.", "연결 끊기",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            sync.Accounts.DeleteAccount(account.Id);
            win.Close();
        });
        var close = new Button { Content = "닫기", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new Button[] { testBtn, syncBtn, save, remove, close }) buttons.Children.Add(b);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel(); body.Children.Add(form); body.Children.Add(status);
        scroll.Content = body;
        root.Children.Add(scroll);
        win.Content = root;
        win.Show();
    }
}
