using System.IO;
using System.Windows;
using DeskCal.Core;
using Forms = System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;

namespace DeskCal.App;

/// <summary>
/// Dev aid for the user manual (README): with <c>--snapshot=DIR --db=FILE --seed --docshots</c> the menus, the settings tabs and the
/// connection windows are rendered offscreen to <c>doc-*.png</c>, next to the board snapshots the harness already writes.
/// </summary>
public partial class App
{
    private void DocShots(string dir)
    {
        var board = _board!;
        var stand = WpfColor.FromRgb(0x3A, 0x5F, 0x7A);
        string P(string name) => Path.Combine(dir, name);

        board.ApplySettings(new BoardSettings());
        board.GoToday();

        // the tray menu (a WinForms strip: draw it without showing it). Built first: it creates the shared edit-mode item.
        using (var tray = BuildMenu())
        {
            _ = tray.Handle;
            foreach (var item in tray.Items.OfType<Forms.ToolStripMenuItem>().Where(i => string.IsNullOrEmpty(i.Text))) item.Text = "달력 숨기기";   // normally filled when the menu opens
            tray.PerformLayout();
            var size = tray.GetPreferredSize(System.Drawing.Size.Empty);
            tray.Size = size;
            using var bmp = new System.Drawing.Bitmap(size.Width, size.Height);
            tray.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
            bmp.Save(P("doc-tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        // the gear's quick menu
        DevHarness.RenderToPng(BuildQuickMenu(), P("doc-gear-menu.png"), 0, 0, stand);

        // position / size adjustment mode
        board.SetEditMode(true);
        board.SaveSnapshot(P("doc-edit-mode.png"));
        board.SetEditMode(false);

        // one customised look: floating month, week numbers, Monday first, tinted holidays, glassier panel
        board.ApplySettings(new BoardSettings
        {
            Mode = DisplayMode.FloatingMonth, MaxRows = 5, PastWeeks = 1, ShowWeekNumbers = true, FirstDayOfWeek = DayOfWeek.Monday,
            HolidayBgColor = "#33FF6B6B", BackgroundOpacity = 0.35, FontSize = 14,
        });
        board.SaveSnapshot(P("doc-custom-look.png"));
        board.ApplySettings(new BoardSettings());

        // the settings window, every tab, tall enough to show the whole page
        var host = MakeSettingsHost();
        for (int tab = 0; tab < 7; tab++)
        {
            var win = SettingsWindow.CreateForSnapshot(host, tab);
            DevHarness.RenderToPng(win.Surface, P($"doc-settings-{tab}.png"), 800, 1100, WpfColor.FromRgb(0xEE, 0xF1, 0xF6));
        }

        // the quick-input window: a sentence the rules read, and one they cannot
        var quickHost = new QuickInputHost(_quickInput!, _batch!, _query!, _accuracy!, () => DateOnly.FromDateTime(DateTime.Today), () => board.Settings.FirstDayOfWeek, () => { }, _ => { }, _ => { });
        foreach (var (file, text) in new[] { ("doc-quick-input.png", "다음 주 화요일 오후 2시 A사 미팅 1시간, 15분 전 알림"), ("doc-quick-input-miss.png", "치과 예약 잡아줘") })
        {
            var quick = QuickInputWindow.Build(quickHost, out var quickBox);
            quickBox.Text = text;
            DevHarness.RenderToPng((FrameworkElement)quick.Content, P(file), 620, 480, WpfColor.FromRgb(0xF0, 0xF0, 0xF0));
        }

        // "extract from text": a pasted notice, analysed
        var extractHost = new ExtractHost(_extraction!, _batch!, () => new DateOnly(2026, 11, 14), () => DayOfWeek.Monday, () => { }, _ => { });
        var extract = ExtractWindow.Build(extractHost, out var extractBox, out var extractRun);
        extractBox.Text = "[공지] 11월 정기 회의 안내\n- 일시: 11월 20일(금) 오후 2시\n- 장소: 3층 회의실\n- 제출 서류는 11월 18일까지 제출해 주세요.\n감사합니다.";
        extractRun();
        DevHarness.RenderToPng((FrameworkElement)extract.Content, P("doc-extract.png"), 760, 640, WpfColor.FromRgb(0xF0, 0xF0, 0xF0));

        // the briefing window (today and the week behind)
        var brief = BriefingWindow.Build(MakeBriefingHost(), out _);
        DevHarness.RenderToPng((FrameworkElement)brief.Content, P("doc-briefing.png"), 560, 520, WpfColor.FromRgb(0xF0, 0xF0, 0xF0));

        // "복습 계획 만들기": a topic and the exam day, planned
        var plan = ReviewPlanWindow.Build(MakeReviewPlanHost(), out var planTopic, out var planRefresh);
        planTopic.Text = "한국사 근현대사 단원";
        planRefresh();
        DevHarness.RenderToPng((FrameworkElement)plan.Content, P("doc-review-plan.png"), 640, 640, WpfColor.FromRgb(0xF0, 0xF0, 0xF0));

        // "미완료 정리": a few to-dos left on past days (the temp database only), one already pushed several times
        var todayDate = DateOnly.FromDateTime(DateTime.Today);
        foreach (var (back, text) in new[] { (-6, "세금 신고 서류 정리"), (-4, "병원 예약 전화"), (-2, "독서 감상문 초안") })
            _itemRepo!.Add(todayDate.AddDays(back).ToString("yyyy-MM-dd"), text);
        var rollover = RolloverWindow.Build(MakeRolloverHost(), out var rolloverRefresh);
        rolloverRefresh();
        DevHarness.RenderToPng((FrameworkElement)rollover.Content, P("doc-rollover.png"), 700, 640, WpfColor.FromRgb(0xF0, 0xF0, 0xF0));

        // connection windows: they are real windows, so show each one just long enough to draw it
        Window? Grab(Action open)
        {
            var before = Windows.OfType<Window>().ToHashSet();
            open();
            var win = Windows.OfType<Window>().FirstOrDefault(w => !before.Contains(w));
            if (win is not null) { win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = -20000; win.Top = -20000; }
            return win;
        }
        void Render(Window? win, string file)
        {
            if (win is null) return;
            win.UpdateLayout();
            DevHarness.RenderToPng((FrameworkElement)win.Content, P(file), win.ActualWidth, win.ActualHeight, WpfColor.FromRgb(0xF3, 0xF3, 0xF3));
            win.Close();
        }

        Render(Grab(() => SyncWindow.ShowOrActivate(_sync!)), "doc-sync.png");

        _mcpAccess!.Enabled = true;
        _mcpRepo!.AddPending("create_event", "{\"title\":\"치과 정기검진\",\"start\":\"2026-11-20T15:00\"}", "일정 추가: 치과 정기검진 (11월 20일 15:00)");
        _mcpRepo.AddPending("add_item", "{\"day\":\"2026-11-18\",\"text\":\"세금 신고 서류 준비\"}", "할 일 추가: 11월 18일 \"세금 신고 서류 준비\"");
        Render(Grab(() => McpWindow.ShowOrActivate(_mcpAccess!, _mcpRepo!, _mcpChanges!, () => { }, openOnApprovals: true)), "doc-mcp-approvals.png");
    }
}
