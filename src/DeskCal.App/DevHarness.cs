using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;

namespace DeskCal.App;

/// <summary>
/// Developer-only checks run with <c>--snapshot=DIR</c>: renders the board and dialogs offscreen to PNG files and drives
/// the editors through their real code paths, so UI wiring can be verified without touching the screen.
/// </summary>
internal static class DevHarness
{
    public static void Run(MainWindow board, ItemRepository items, EventSeriesService series, EventRepository events,
                           ReminderRepository reminders, KoreanDays days, string dir, bool seed)
    {
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        board.Left = -20000; board.Top = -20000;
        board.Show();
        var today = DateOnly.FromDateTime(DateTime.Today);
        string D(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");

        if (seed)
        {
            items.ReplaceDay(D(0), ["기획서 검토", "점심 약속 12:00", "운동", "장보기", "책 반납"]);
            items.ReplaceDay(D(2), ["치과 예약", "세금 신고"]);
            items.ReplaceDay(D(-3), ["보고서 제출", "팀 회식"]);
            items.ReplaceDay(D(5), ["월간 회의 준비"]);
            var day2 = items.GetByDay(D(2));
            items.SetDone(day2[1].Id, true);
            items.SetTextColor(day2[0].Id, "#FFE066");
            items.SetColor(items.GetByDay(D(0))[0].Id, "#74C0FC");
            items.SetDone(items.GetByDay(D(-3))[0].Id, true);
            reminders.Add(ReminderOwner.Item, items.GetByDay(D(0))[1].Id, 0, "09:00");

            series.Create(Draft("팀 회의", today, new TimeOnly(10, 0), new TimeOnly(11, 0), new RecurrenceRule { Kind = RepeatKind.Weekly }, new ReminderSpec(10)));
            series.Create(Draft("스탠드업", today.AddDays(-14), new TimeOnly(9, 30), null, new RecurrenceRule { Kind = RepeatKind.Weekday }));
            series.Create(new EventDraft("출장", "", today.AddDays(1), null, today.AddDays(3), null, RecurrenceRule.None, "#FFA94D", []));
            series.Create(new EventDraft("프로젝트 마감", "", today.AddDays(4), null, null, null, RecurrenceRule.None, "#FF6B6B", [new ReminderSpec(1440, "09:00")]));
            series.Create(new EventDraft("월말 정산", "", new DateOnly(today.Year, today.Month, 1), null, null, null, new RecurrenceRule { Kind = RepeatKind.MonthLastDay }, null, []));
            series.Create(new EventDraft("영단어 복습", "", today.AddDays(-1), null, null, null, new RecurrenceRule { Kind = RepeatKind.Ebbinghaus }, "#69DB7C", []));
        }

        foreach (var mode in Enum.GetValues<DisplayMode>())
        {
            board.ApplySettings(new BoardSettings { Mode = mode, ShowWeekNumbers = true, FirstDayOfWeek = DayOfWeek.Monday, MaxRows = 5 });
            board.SaveSnapshot(Path.Combine(dir, $"{(int)mode}-{mode}.png"));
        }
        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth });
        foreach (var (y, m) in new[] { (2026, 2), (2026, 5), (2026, 10), (2025, 10) })
        {
            board.GoTo(new DateOnly(y, m, 1));
            board.SaveSnapshot(Path.Combine(dir, $"m4-{y}-{m:00}.png"));
        }
        board.GoToday();
        board.SaveSnapshot(Path.Combine(dir, "m5-board.png"));
        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth, Bullet = BulletStyle.Number });
        board.SaveSnapshot(Path.Combine(dir, "m3-numbered.png"));
        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth, Bullet = BulletStyle.None, ShowCompleted = false });
        board.SaveSnapshot(Path.Combine(dir, "m3-hide-completed.png"));
        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth });
        board.OpenEditor(today);
        board.SaveSnapshot(Path.Combine(dir, "m3-editor.png"));

        // M6: the tray icon at 16 px (real size) and 64 px (to look at), over the taskbar colours.
        foreach (int px in new[] { 16, 64 })
        {
            using var icon = TrayIcon.Create(px);
            using var bmp = new System.Drawing.Bitmap(px * 2, px * 2);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.FromArgb(0x20, 0x20, 0x20));
                g.DrawIcon(icon, new System.Drawing.Rectangle(px / 2, px / 2, px, px));
            }
            bmp.Save(Path.Combine(dir, $"m6-tray-{px}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        if (seed)
        {
            // M3: type new text into the day editor and check what reached the database.
            var before = items.GetByDay(D(0));
            board.EditorText = "기획서 검토\r\n새 항목\r\n\r\n운동   ";
            board.CommitEditor();
            var after = items.GetByDay(D(0));
            bool ok3 = after.Select(i => i.Content).SequenceEqual(["기획서 검토", "새 항목", "운동"])
                       && after[0].Id == before[0].Id && after[0].BgColor == "#74C0FC"
                       && after[2].Id == before[2].Id && after[1].Id == before[1].Id;
            report.AppendLine($"{(ok3 ? "PASS" : "FAIL")}: day editor -> {string.Join(" | ", after.Select(i => i.Content))}");
            board.SaveSnapshot(Path.Combine(dir, "m3-after-edit.png"));

            EventEditorSelfTests(series, events, reminders, today, dir, report);
        }

        File.WriteAllText(Path.Combine(dir, "selftest.txt"), report.ToString());
    }

    private static EventDraft Draft(string title, DateOnly date, TimeOnly start, TimeOnly? end, RecurrenceRule rule, params ReminderSpec[] r) =>
        new(title, "", date, start, null, end, rule, null, r);

    // ---- event editor driven through its real controls ---------------------------------------

    private static void EventEditorSelfTests(EventSeriesService series, EventRepository events, ReminderRepository reminders,
                                             DateOnly today, string dir, StringBuilder report)
    {
        var d1 = today.AddDays(1);

        // 1. a new weekly event typed into the form
        var w = new EventEditorWindow(series, null, d1);
        RenderWindow(w, Path.Combine(dir, "m5-editor-new.png"), keepOpen: true);
        w.TitleBox.Text = "UI 자체점검 스터디";
        w.StartTime.Text = "14:00"; w.EndTime.Text = "15:30";
        w.RepeatBox.SelectedIndex = 2;                                   // 매주
        w.IntervalBox.Text = "2";
        foreach (var b in w.WeekdayBoxes) b.IsChecked = false;
        w.WeekdayBoxes[(int)DayOfWeek.Monday].IsChecked = true;
        w.WeekdayBoxes[(int)DayOfWeek.Wednesday].IsChecked = true;
        w.EndKindBox.SelectedIndex = 1; w.CountBox.Text = "6";            // 6회
        w.ReminderBox.SelectedIndex = 5;                                  // 30분 전
        bool saved = w.Save();
        var e1 = events.GetAllActive().FirstOrDefault(e => e.Title == "UI 자체점검 스터디");
        var r1 = e1 is null ? [] : reminders.GetByOwner(ReminderOwner.Event, e1.Id);
        bool ok1 = saved && e1 is not null && e1.RRule == "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6"
                   && e1.Start == $"{d1:yyyy-MM-dd}T14:00" && e1.End == $"{d1:yyyy-MM-dd}T15:30"
                   && r1.Count == 1 && r1[0].OffsetMinutes == 30;
        report.AppendLine($"{(ok1 ? "PASS" : "FAIL")}: new weekly event -> {e1?.RRule} {e1?.Start}-{e1?.End} reminders={string.Join(",", r1.Select(r => r.OffsetMinutes))}");

        // 2. all-day, monthly by weekday, reminder the day before at 08:30
        w = new EventEditorWindow(series, null, today);
        w.TitleBox.Text = "UI 자체점검 월례회";
        w.AllDayBox.IsChecked = true;
        w.RepeatBox.SelectedIndex = 3;                                   // 매월
        w.MonthlyModeBox.SelectedIndex = 1;                              // N번째 요일
        w.ReminderBox.SelectedIndex = 8;                                 // 1일 전
        w.ReminderClock.Text = "08:30";
        bool saved2 = w.Save();
        var e2 = events.GetAllActive().FirstOrDefault(e => e.Title == "UI 자체점검 월례회");
        var r2 = e2 is null ? [] : reminders.GetByOwner(ReminderOwner.Event, e2.Id);
        bool ok2 = saved2 && e2 is not null && e2.AllDay && e2.Start == $"{today:yyyy-MM-dd}"
                   && e2.RRule is { } rr && rr.StartsWith("FREQ=MONTHLY;BYDAY=") && rr.EndsWith(RecurrenceExpanderDow(today))
                   && r2.Count == 1 && r2[0].OffsetMinutes == 1440 && r2[0].CustomTime == "08:30";
        report.AppendLine($"{(ok2 ? "PASS" : "FAIL")}: all-day monthly event -> {e2?.RRule} reminders={string.Join(",", r2.Select(r => $"{r.OffsetMinutes}@{r.CustomTime}"))}");

        // 3. bad input is refused with a message and nothing is stored
        int count = events.GetAllActive().Count;
        w = new EventEditorWindow(series, null, today);
        w.TitleBox.Text = "";
        bool refusedTitle = !w.Save() && w.ErrorText.Text.Length > 0;
        w.TitleBox.Text = "시간 오류"; w.StartTime.Text = "25:99";
        bool refusedTime = !w.Save() && w.ErrorText.Text.Contains("HH:mm");
        w.StartTime.Text = "10:00"; w.EndTime.Text = "09:00";
        bool refusedOrder = !w.Save() && w.ErrorText.Text.Contains("종료");
        w.EndTime.Text = ""; w.RepeatBox.SelectedIndex = 1; w.IntervalBox.Text = "0";
        bool refusedInterval = !w.Save() && w.ErrorText.Text.Contains("간격");
        bool nothing = events.GetAllActive().Count == count;
        report.AppendLine($"{(refusedTitle && refusedTime && refusedOrder && refusedInterval && nothing ? "PASS" : "FAIL")}: invalid input refused (title={refusedTitle} time={refusedTime} order={refusedOrder} interval={refusedInterval} stored-nothing={nothing})");
        w.Close();

        // 4. editing a single (non-repeating) event keeps its id; the form shows what was stored
        var single = events.GetAllActive().First(e => e.Title == "프로젝트 마감");
        w = new EventEditorWindow(series, single.Id, DateOnly.Parse(single.Start));
        RenderWindow(w, Path.Combine(dir, "m5-editor-edit.png"), keepOpen: true);
        bool prefilled = w.TitleBox.Text == "프로젝트 마감" && w.AllDayBox.IsChecked == true && w.ReminderClock.Text == "09:00";
        w.TitleBox.Text = "프로젝트 마감(수정)";
        bool saved4 = w.Save();
        bool ok4 = prefilled && saved4 && events.Get(single.Id)!.Title == "프로젝트 마감(수정)";
        report.AppendLine($"{(ok4 ? "PASS" : "FAIL")}: edit single event (prefilled={prefilled}, saved={saved4})");

        // 5. a repeating event opened on a later occurrence shows that occurrence's date
        var weekly = events.GetAllActive().First(e => e.Title == "팀 회의");
        var third = DateOnly.Parse(weekly.Start[..10]).AddDays(14);
        w = new EventEditorWindow(series, weekly.Id, third);
        bool showsOccurrence = w.StartDate.SelectedDate == third.ToDateTime(TimeOnly.MinValue) && w.RepeatBox.SelectedIndex == 2;
        report.AppendLine($"{(showsOccurrence ? "PASS" : "FAIL")}: repeating event shows the occurrence date {third:yyyy-MM-dd}");
        w.Close();

        // reminder pop-up card with sample messages
        var win = new ReminderWindow(new ReminderPresenter());
        var now = DateTime.Now;
        win.Add([
            new ReminderMessage("a", ReminderOwner.Event, "e", "팀 회의", today, new TimeOnly(10, 0), now, 10, null, false),
            new ReminderMessage("b", ReminderOwner.Event, "e", "프로젝트 마감", today.AddDays(1), null, now, 1440, null, true),
            new ReminderMessage("c", ReminderOwner.Item, "i", "점심 약속 12:00", today, null, now, 0, null, false),
        ]);
        RenderWindow(win, Path.Combine(dir, "m5-reminder-popup.png"), keepOpen: false);
    }

    private static string RecurrenceExpanderDow(DateOnly d) => new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }[(int)d.DayOfWeek];

    /// <summary>Show a window far off-screen, lay it out, and save what it draws.</summary>
    private static void RenderWindow(Window w, string path, bool keepOpen)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000; w.Top = -20000; w.ShowActivated = false;
        w.Show();
        w.UpdateLayout();
        var el = (FrameworkElement)w.Content;
        int width = Math.Max(1, (int)Math.Ceiling(el.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(el.ActualHeight));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x5F, 0x7A)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(w.Background is { } bg && bg != Brushes.Transparent ? bg : Brushes.Transparent, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, width, height));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        if (!keepOpen) w.Close();
    }
}
