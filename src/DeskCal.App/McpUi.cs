using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DeskCal.Data;

namespace DeskCal.App;

/// <summary>
/// The AI connection: switch it on, make the access token, approve or reject what an AI client asked for, and read the log.
/// Nothing an AI asks for is applied before the user presses "승인" here.
/// </summary>
internal static class McpWindow
{
    private static Window? _open;

    /// <summary>Where the MCP server program is expected (next to the app).</summary>
    public static string ServerPath =>
        new[] { Path.Combine(AppContext.BaseDirectory, "deskcal-mcp.exe"), Path.Combine(AppContext.BaseDirectory, "mcp", "deskcal-mcp.exe") }
            .FirstOrDefault(File.Exists) ?? Path.Combine(AppContext.BaseDirectory, "deskcal-mcp.exe");

    public static void ShowOrActivate(McpAccess access, McpRepository repo, McpChanges changes, Action changed, bool openOnApprovals = false)
    {
        if (_open is not null) { _open.Activate(); return; }

        var win = Dlg.Create("AI 연동 (MCP)", 640, 560);
        _open = win;
        win.Closed += (_, _) => _open = null;
        var tabs = new TabControl { Margin = new Thickness(14) };

        // ---- connection ----
        var enabled = new CheckBox { Content = "AI 연동 사용", IsChecked = access.Enabled, Margin = new Thickness(0, 0, 0, 8) };
        enabled.Click += (_, _) => access.Enabled = enabled.IsChecked == true;
        var tokenState = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var snippet = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Height = 130,
                                     HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        void ShowTokenState() => tokenState.Text = access.HasToken
            ? "접근 토큰이 만들어져 있습니다. 토큰은 만든 직후에만 볼 수 있습니다 (잊었으면 새로 만드세요 — 이전 토큰은 즉시 쓸 수 없게 됩니다)."
            : "아직 접근 토큰이 없습니다. 아래 버튼으로 만드세요.";
        ShowTokenState();

        var make = Dlg.Btn("새 토큰 만들기", (_, _) =>
        {
            if (access.HasToken && System.Windows.MessageBox.Show(win, "새 토큰을 만들면 지금 쓰는 토큰은 바로 쓸 수 없게 됩니다. AI 프로그램의 설정도 새 토큰으로 바꿔야 합니다.", "새 토큰",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            string token = access.NewToken();
            var config = new { mcpServers = new Dictionary<string, object> { ["m-deskcal"] = new { command = ServerPath, env = new Dictionary<string, string> { ["DESKCAL_MCP_TOKEN"] = token } } } };
            snippet.Text = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            snippet.Visibility = Visibility.Visible;
            tokenState.Text = "새 토큰입니다. 아래 설정을 복사해 AI 프로그램(예: Claude Desktop 의 설정 파일)에 붙여넣으세요. 이 창을 닫으면 토큰을 다시 볼 수 없습니다.";
            if (!access.Enabled) { access.Enabled = true; enabled.IsChecked = true; }
        });
        var copy = Dlg.Btn("설정 복사", (_, _) => { if (snippet.Text.Length > 0) System.Windows.Clipboard.SetText(snippet.Text); });
        var revoke = Dlg.Btn("토큰 폐기", (_, _) =>
        {
            access.RevokeToken(); snippet.Text = ""; snippet.Visibility = Visibility.Collapsed; ShowTokenState();
        });
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        btnRow.Children.Add(make); btnRow.Children.Add(copy); btnRow.Children.Add(revoke);

        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11.5,
            Text = "AI 프로그램은 일정·할 일·디데이·메모·시간표를 읽고 검색할 수 있고, 빈 시간·음력·공휴일 같은 계산도 이 앱에 맡길 수 있습니다. "
                 + "추가·수정·삭제는 '요청'만 만들어지고, 아래 '승인 대기' 탭에서 직접 승인해야 반영됩니다. "
                 + "달력이 잠겨 있으면 아무것도 제공하지 않습니다. 모든 호출은 '기록' 탭에 남습니다.\n"
                 + "※ AI 가 읽은 내용은 그 AI 프로그램으로 전달됩니다. 클라우드 AI(예: Claude Desktop)를 연결하면 달력 내용이 이 PC 밖으로 나갑니다.\n"
                 + "※ 일정 제목·메모에 AI 에게 말을 거는 글이 섞여 있을 수 있습니다. 승인할 때는 내용을 꼭 읽어 보세요.\n"
                 + (File.Exists(ServerPath) ? "" : "\n⚠ MCP 서버 프로그램을 찾을 수 없습니다: " + ServerPath),
        };
        var connect = new StackPanel { Margin = new Thickness(10) };
        foreach (UIElement e in new UIElement[] { enabled, tokenState, btnRow, snippet, info }) connect.Children.Add(e);

        // ---- approvals ----
        var waiting = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
        var none = new TextBlock { Text = "승인을 기다리는 요청이 없습니다.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) };
        void Reload()
        {
            repo.ExpireOld();
            waiting.Items.Clear();
            foreach (var p in repo.ListWaiting())
            {
                var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2), Tag = p };
                row.Children.Add(new TextBlock { Text = Dlg.When(p.CreatedAt), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray });
                row.Children.Add(new TextBlock { Text = p.Summary, TextWrapping = TextWrapping.Wrap });
                waiting.Items.Add(row);
            }
            none.Visibility = waiting.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            detail.Text = "";
        }
        waiting.SelectionChanged += (_, _) => detail.Text = waiting.SelectedItem is StackPanel { Tag: PendingChange p } ? "요청 내용(원문):\n" + p.Args : "";

        bool Decide(PendingChange p, bool approve)
        {
            if (!approve) return repo.Decide(p.Id, PendingStatus.Rejected, "거부됨");
            try
            {
                string result = changes.Apply(p.Tool, p.Args);
                repo.Decide(p.Id, PendingStatus.Approved, result);
                repo.Audit("approve:" + p.Tool, p.Id, "ok");
                return true;
            }
            catch (Exception ex)
            {
                repo.Decide(p.Id, PendingStatus.Failed, ex.Message);
                repo.Audit("approve:" + p.Tool, p.Id, "error: " + ex.Message);
                System.Windows.MessageBox.Show(win, "적용하지 못했습니다: " + ex.Message, "AI 요청");
                return false;
            }
        }

        var approve = Dlg.Btn("승인", (_, _) =>
        {
            if (waiting.SelectedItem is not StackPanel { Tag: PendingChange p }) { System.Windows.MessageBox.Show(win, "요청을 목록에서 고르세요.", "AI 요청"); return; }
            Decide(p, true); Reload(); changed();
        });
        var reject = Dlg.Btn("거부", (_, _) =>
        {
            if (waiting.SelectedItem is not StackPanel { Tag: PendingChange p }) { System.Windows.MessageBox.Show(win, "요청을 목록에서 고르세요.", "AI 요청"); return; }
            Decide(p, false); Reload();
        });
        var rejectAll = Dlg.Btn("모두 거부", (_, _) => { foreach (var p in repo.ListWaiting()) Decide(p, false); Reload(); });
        var approvalButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        approvalButtons.Children.Add(approve); approvalButtons.Children.Add(reject); approvalButtons.Children.Add(rejectAll);
        var approvals = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(approvalButtons, Dock.Bottom); DockPanel.SetDock(detail, Dock.Bottom); DockPanel.SetDock(none, Dock.Top);
        approvals.Children.Add(approvalButtons); approvals.Children.Add(detail); approvals.Children.Add(none); approvals.Children.Add(waiting);
        Reload();

        // ---- log ----
        var log = new ListBox { Margin = new Thickness(10) };
        void ReloadLog()
        {
            log.Items.Clear();
            foreach (var a in repo.ListAudit(300))
                log.Items.Add(new TextBlock { Text = $"{Dlg.When(a.At)}  {a.Tool}  →  {a.Outcome}", ToolTip = a.Args, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        ReloadLog();

        tabs.Items.Add(new TabItem { Header = "연결", Content = connect });
        var approvalTab = new TabItem { Header = "승인 대기", Content = approvals };
        tabs.Items.Add(approvalTab);
        var logTab = new TabItem { Header = "기록", Content = log };
        logTab.GotFocus += (_, _) => ReloadLog();
        tabs.Items.Add(logTab);
        tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == tabs) { if (tabs.SelectedItem == approvalTab) Reload(); else if (tabs.SelectedItem == logTab) ReloadLog(); } };
        if (openOnApprovals) tabs.SelectedItem = approvalTab;

        win.Content = tabs;
        win.Show();
    }
}
