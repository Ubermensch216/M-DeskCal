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
                           ReminderRepository reminders, KoreanDays days, string dir, bool seed,
                           CountdownRepository countdowns, SearchService search, HistoryRepository history, ExchangeService exchange,
                           NoteRepository notes, TimetableRepository timetable, SettingsStore settings)
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

        SettingsSnapshots(board, settings, dir, report);

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

            ItemOptionChecks(board, items, settings, countdowns, today, dir, report);

            EventEditorSelfTests(series, events, reminders, today, dir, report);
            SearchHistoryCountdownChecks(board, items, search, history, countdowns, today, dir, report);
            ExchangeChecks(exchange, dir, report);
            Milestone9Checks(board, items, events, days, notes, history, timetable, settings, exchange, dir, report);
        }

        File.WriteAllText(Path.Combine(dir, "selftest.txt"), report.ToString());
    }

    /// <summary>The day editor's option buttons: done / colours through the editor, and the repeat, reminder, countdown dialogs and the palette as PNGs.</summary>
    private static void ItemOptionChecks(MainWindow board, ItemRepository items, SettingsStore settings, CountdownRepository countdowns, DateOnly today, string dir, StringBuilder report)
    {
        string day = today.ToString("yyyy-MM-dd");
        board.OpenEditor(today);
        board.DevCaretToLine(1);
        board.DevDone();                                    // "새 항목" -> done
        board.DevCaretToLine(0);
        board.DevColor("#E53935", "#FFE08A");               // first line: red text on a yellow cell
        var stored = items.GetByDay(day);
        bool ok = stored.Count == 3 && stored[1].Done && !stored[0].Done && !stored[2].Done
                  && stored[0].TextColor == "#E53935" && stored[0].BgColor == "#FFE08A";
        var lines = board.DevLines();
        bool shown = lines.Count == 3 && lines[1].Struck && !lines[0].Struck && lines[0].Foreground == "#FFE53935" && lines[2].Foreground == "#FFFFFFFF";      // untouched lines keep the editor's white
        report.AppendLine($"{(ok ? "PASS" : "FAIL")}: editor done/colour buttons -> {string.Join(" | ", stored.Select(i => $"{i.Content}[done={i.Done},fg={i.TextColor},bg={i.BgColor}]"))}");
        report.AppendLine($"{(shown ? "PASS" : "FAIL")}: editor lines show the state -> {string.Join(" | ", lines.Select(l => $"{l.Text}[struck={l.Struck},fg={l.Foreground}]"))}");
        board.SaveSnapshot(Path.Combine(dir, "opt-editor.png"));
        board.DevCaretToLine(1);
        board.DevDone();                                    // and back
        bool undone = !items.GetByDay(day)[1].Done;
        report.AppendLine($"{(undone ? "PASS" : "FAIL")}: editor done button toggles back");
        board.CommitEditor();

        var white = System.Windows.Media.Colors.White;
        const string sample = "★2026 공공 AI 대전환 챌린지 (본 제출)";
        void Snap(string file, Action show)
        {
            ItemDialog.Preview = win => RenderToPng((FrameworkElement)win.Content, Path.Combine(dir, file), win.Width, 0, white);
            try { show(); } finally { ItemDialog.Preview = null; }
        }
        Snap("opt-repeat.png", () => RepeatDialog.Show(null, sample, today, new RecurrenceRule { Kind = RepeatKind.Weekly }));
        Snap("opt-reminder.png", () => ReminderDialog.Show(null, sample, today, [new Reminder("r", ReminderOwner.Item, "i", 0, "09:15", null, null), new Reminder("r2", ReminderOwner.Item, "i", 1440, "09:15", null, null)]));
        Snap("opt-countdown.png", () => ItemCountdownDialog.Show(null, sample, today.AddDays(30), null));

        settings.SetString("editor.recent_text_colors", "#E53935,#4CAF50");
        settings.SetString("editor.recent_cell_colors", "#FFE08A");
        RenderToPng(ColorPickerPopup.BuildContent(settings, "#E53935", "#FFE08A", (_, _) => { }, _ => { }), Path.Combine(dir, "opt-colors.png"), 0, 0, white);
        settings.Remove("editor.recent_text_colors");
        settings.Remove("editor.recent_cell_colors");
        report.AppendLine("PASS: option dialogs and palette rendered (opt-*.png)");

        var third = items.GetByDay(day)[2];
        board.DevMakeMarks(third.Id);
        var nextWeek = items.GetByDay(today.AddDays(7).ToString("yyyy-MM-dd")).FirstOrDefault(i => i.Content == third.Content);
        bool repeated = nextWeek is not null && nextWeek.SeriesId == items.Get(third.Id)!.SeriesId && items.Get(third.Id)!.CountdownId is not null;
        board.SaveSnapshot(Path.Combine(dir, "opt-board-marks.png"));
        report.AppendLine($"{(repeated ? "PASS" : "FAIL")}: repeat + countdown marks on the board (opt-board-marks.png)");
        foreach (var cd in countdowns.List().Where(c => c.Id == items.Get(third.Id)!.CountdownId)) countdowns.Delete(cd.Id);   // the later countdown checks expect their own list
        items.SetCountdown(third.Id, null);
    }

    /// <summary>The settings window, one PNG per tab, plus a check that a change made through it reaches the board and the database.</summary>
    private static void SettingsSnapshots(MainWindow board, SettingsStore store, string dir, StringBuilder report)
    {
        var boardStore = new BoardSettingsStore(store);
        var host = new SettingsHost(
            Get: () => board.Settings,
            Update: change => { board.ApplySettings(change(board.Settings)); boardStore.Save(board.Settings); },
            ResetDefaults: () => { boardStore.ResetToDefaults(); board.ApplySettings(boardStore.Load()); },
            GetPrefs: () => new AppPreferences(), SetPrefs: _ => { },
            GetAutoStart: () => false, SetAutoStart: _ => { },
            GetTimetableWidget: () => false, SetTimetableWidget: _ => { },
            OpenNotes: () => { }, OpenCountdowns: () => { }, OpenTimetable: () => { },
            EditHolidays: () => { }, ReloadHolidays: () => { }, OpenDataFolder: () => { }, ShowDiagnostics: () => { });

        board.ApplySettings(new BoardSettings());
        for (int tab = 0; tab < 6; tab++)
        {
            var win = SettingsWindow.CreateForSnapshot(host, tab);
            RenderToPng(win.Surface, Path.Combine(dir, $"settings-{tab}.png"), 800, 680, System.Windows.Media.Color.FromRgb(0xEE, 0xF1, 0xF6));
        }

        var before = board.Settings;
        host.Update(s => s with { Mode = DisplayMode.FloatingMonth, PastWeeks = 1, LineGap = 3, MoveDoneToBottom = true });
        var saved = boardStore.Load();
        bool ok = board.Settings.Mode == DisplayMode.FloatingMonth && saved.PastWeeks == 1 && saved.LineGap == 3 && saved.MoveDoneToBottom;
        report.AppendLine($"{(ok ? "PASS" : "FAIL")}: settings window change reaches the board and the database");
        board.SaveSnapshot(Path.Combine(dir, "settings-applied-floating.png"));
        host.ResetDefaults();
        report.AppendLine($"{(board.Settings == new BoardSettings() && before == new BoardSettings() ? "PASS" : "FAIL")}: settings reset to defaults");
        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth });
    }

    /// <summary>Flat-background offscreen render of any element (the board's own snapshot has its own copy).</summary>
    internal static void RenderToPng(FrameworkElement root, string path, double w = 0, double h = 0, System.Windows.Media.Color? bg = null)
    {
        root.Measure(new Size(w > 0 ? w : double.PositiveInfinity, h > 0 ? h : double.PositiveInfinity));
        double rw = w > 0 ? w : root.DesiredSize.Width, rh = h > 0 ? h : root.DesiredSize.Height;
        root.Arrange(new Rect(0, 0, rw, rh));
        root.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg ?? System.Windows.Media.Color.FromRgb(0x3A, 0x5F, 0x7A)), null, new Rect(0, 0, rw, rh));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, rw, rh));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(rw), (int)Math.Ceiling(rh), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void SearchHistoryCountdownChecks(MainWindow board, ItemRepository items, SearchService search, HistoryRepository history,
                                                    CountdownRepository countdowns, DateOnly today, string dir, StringBuilder report)
    {
        void Check(string name, bool ok, string detail = "") => report.AppendLine($"{(ok ? "PASS" : "FAIL")}: {name}{(detail.Length > 0 ? " -> " + detail : "")}");

        // history: edit a seeded line twice, look at the list, restore the oldest
        var target = items.GetByDay(today.ToString("yyyy-MM-dd"))[2];            // "운동"
        items.UpdateContent(target.Id, "운동 30분");
        items.UpdateContent(target.Id, "운동 1시간");
        var versions = history.List(EntityNames.Item, target.Id);
        Check("history lists earlier versions newest first", versions.Count == 2 && versions[0].Content == "운동 30분" && versions[1].Content == "운동", string.Join(" | ", versions.Select(v => v.Content)));
        items.RestoreFromHistory(versions[1].Id);
        Check("restore brings the old text back and keeps the replaced one", items.Get(target.Id)!.Content == "운동" && history.List(EntityNames.Item, target.Id).Count == 3);

        // search over the seeded data
        var hits = search.Run(new SearchQuery("회의"));
        Check("search finds 회의 in a to-do line and an event", hits.Any(h => h.Kind == SearchKind.Item) && hits.Any(h => h.Kind == SearchKind.Event), string.Join(", ", hits.Select(h => $"{h.Kind}:{h.Text}")));
        var done = search.Run(new SearchQuery("", Done: DoneFilter.Done));
        Check("done filter", done.Count == 2 && done.All(h => h.Done), done.Count.ToString());
        var yellow = search.Run(new SearchQuery("", TextColor: "#FFE066"));
        Check("colour filter", yellow.Count == 1 && yellow[0].Text == "치과 예약", yellow.Count.ToString());

        // countdowns: card + strip under the calendar
        countdowns.Add("기말시험", today.AddDays(12).ToString("yyyy-MM-dd"), CountdownType.Exam, showCard: true, showBottom: true);
        var bday = new DateOnly(1990, today.Month, Math.Min(today.Day, 28)).AddDays(30);
        countdowns.Add("민수 생일", bday.ToString("yyyy-MM-dd"), CountdownType.Birthday, showCard: true, showBottom: true);
        countdowns.Add("만난 날", today.AddDays(-99).ToString("yyyy-MM-dd"), CountdownType.Memorial, showCard: true);
        countdowns.Add("설날", "2000-01-01", CountdownType.Festival, showBottom: true, lunar: true);
        var ordered = CountdownCalc.Ordered(countdowns.List(), today);
        Check("countdowns computed", ordered.Count == 4 && ordered[0].Status.Label == "D-12", string.Join(", ", ordered.Select(o => CountdownCalc.Summary(o.Countdown, o.Status))));

        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth });
        board.GoToday();
        board.Refresh();
        board.SaveSnapshot(Path.Combine(dir, "m7-board-with-countdowns.png"));

        var cards = new CountdownCardsWindow();
        cards.Rebuild(CountdownCalc.Ordered(countdowns.List().Where(c => c.ShowCard), today), new BoardSettings());
        cards.SaveSnapshot(Path.Combine(dir, "m7-cards.png"));

        SearchWindow.Snapshot(search, items, history, "회의", Path.Combine(dir, "m7-search.png"));
        Check("snapshots written", File.Exists(Path.Combine(dir, "m7-search.png")) && File.Exists(Path.Combine(dir, "m7-cards.png")));
    }

    /// <summary>Write the seeded data as JSON / CSV / ICS next to the snapshots (so the files can be inspected) and read each back.</summary>
    private static void ExchangeChecks(ExchangeService exchange, string dir, StringBuilder report)
    {
        var bundle = exchange.Collect();
        foreach (var format in Enum.GetValues<ExchangeFormat>().Where(f => f != ExchangeFormat.Xlsx))
        {
            string path = Path.Combine(dir, "m8-export" + ExchangeFiles.Extension(format));
            ExchangeFiles.WriteFile(path, format, bundle, DateTimeOffset.Now);
            var read = ExchangeFiles.ReadFile(path);
            bool ok = read.Bundle.Items.Count == bundle.Items.Count && read.Bundle.Events.Count == bundle.Events.Count && read.Warnings.Count == 0;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}: export {format} -> {new FileInfo(path).Length} bytes, read back items={read.Bundle.Items.Count}/{bundle.Items.Count} events={read.Bundle.Events.Count}/{bundle.Events.Count} warnings={read.Warnings.Count}");
        }
    }

    /// <summary>M9: notes, timetable (editor + widget), background photos, content lock, print page, Excel file.</summary>
    private static void Milestone9Checks(MainWindow board, ItemRepository items, EventRepository events, KoreanDays days, NoteRepository notes,
                                         HistoryRepository history, TimetableRepository timetable, SettingsStore settings, ExchangeService exchange,
                                         string dir, StringBuilder report)
    {
        void Check(string name, bool ok, string detail = "") => report.AppendLine($"{(ok ? "PASS" : "FAIL")}: {name}{(detail.Length > 0 ? " -> " + detail : "")}");
        var today = DateOnly.FromDateTime(DateTime.Today);

        // --- notes
        var n1 = notes.Add("장보기", "우유\n빵\n달걀"); notes.SetColor(n1.Id, "#FFE066");
        notes.Update(n1.Id, "장보기", "우유\n빵\n달걀\n치즈");
        var n2 = notes.Add("회의 메모", "안건 1. 일정 점검\n안건 2. 예산");
        NotesWindow.Snapshot(notes, history, Path.Combine(dir, "m9-notes.png"));
        Check("notes written and rendered", File.Exists(Path.Combine(dir, "m9-notes.png")) && notes.List().Count == 2 && history.List(EntityNames.Note, n1.Id).Count == 1);

        // --- timetable: a week, widget + editor
        timetable.SetCell(DayOfWeek.Monday, 1, "수학", "301호", "#74C0FC");
        timetable.SetCell(DayOfWeek.Monday, 2, "영어", "", "#FFE066");
        timetable.SetCell(DayOfWeek.Tuesday, 1, "국어", "201호", "#69DB7C");
        timetable.SetCell(DayOfWeek.Wednesday, 3, "체육", "운동장", "#FFA94D");
        timetable.SetCell(DayOfWeek.Thursday, 5, "과학", "실험실", "#B197FC");
        timetable.SetCell(DayOfWeek.Friday, 7, "동아리");
        var widget = new TimetableWidgetWindow();
        widget.Rebuild(timetable.LoadConfig(settings), timetable.GetCells(), new BoardSettings());
        widget.SaveSnapshot(Path.Combine(dir, "m9-timetable-widget.png"));
        TimetableWindow.Snapshot(timetable, settings, Path.Combine(dir, "m9-timetable-editor.png"));
        Check("timetable cells stored", timetable.GetCells().Count == 6);

        // --- background photos: three generated pictures, each fit mode
        string skinDir = Path.Combine(dir, "skin-photos");
        Directory.CreateDirectory(skinDir);
        for (int i = 0; i < 3; i++) MakePicture(Path.Combine(skinDir, $"photo{i + 1}.png"), 900, 600, i);
        File.WriteAllText(Path.Combine(skinDir, "broken.png"), "not a picture");        // must be skipped, not crash
        foreach (var fit in new[] { SkinFit.Fill, SkinFit.Fit, SkinFit.Tile })
        {
            board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth, SkinFolder = skinDir, SkinFit = fit, BackgroundOpacity = 0.8 });
            board.SaveSnapshot(Path.Combine(dir, $"m9-skin-{fit}.png"));
        }
        Check("skin: unreadable picture is skipped", MainWindow.LoadPicture(Path.Combine(skinDir, "broken.png")) is null && MainWindow.LoadPicture(Path.Combine(skinDir, "photo1.png")) is { PixelWidth: 900 });
        board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth });

        // --- content lock
        board.SetContentHidden(true);
        board.SaveSnapshot(Path.Combine(dir, "m9-locked.png"));
        board.SetContentHidden(false);
        Check("content lock render", File.Exists(Path.Combine(dir, "m9-locked.png")) && !board.ContentHidden);

        // --- print page
        var page = PrintableMonth.Gather(new DateOnly(today.Year, today.Month, 1), DayOfWeek.Sunday, items, events, d => days.Holidays.IsDayOff(d), days);
        RenderToPng(PrintableMonth.Build(page, 1050, 740), Path.Combine(dir, "m9-print.png"), 1050, 740, System.Windows.Media.Colors.White);
        Check("print page rendered", File.Exists(Path.Combine(dir, "m9-print.png")));

        // --- Excel
        string xlsx = Path.Combine(dir, "m9-export.xlsx");
        ExchangeFiles.WriteFile(xlsx, ExchangeFormat.Xlsx, exchange.Collect(), DateTimeOffset.Now, d => days.Holidays.IsDayOff(d),
            new XlsxOptions(null, null, DayOfWeek.Sunday, d => days.Get(d).HolidayName));
        Check("xlsx written", new FileInfo(xlsx).Length > 2000, new FileInfo(xlsx).Length + " bytes");
    }

    private static void MakePicture(string path, int w, int h, int variant)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var colors = variant switch
            {
                0 => (System.Windows.Media.Color.FromRgb(0x1E, 0x3C, 0x72), System.Windows.Media.Color.FromRgb(0x2A, 0x9D, 0x8F)),
                1 => (System.Windows.Media.Color.FromRgb(0x6A, 0x1B, 0x4D), System.Windows.Media.Color.FromRgb(0xF4, 0xA2, 0x61)),
                _ => (System.Windows.Media.Color.FromRgb(0x26, 0x4D, 0x2E), System.Windows.Media.Color.FromRgb(0xE9, 0xC4, 0x6A)),
            };
            dc.DrawRectangle(new LinearGradientBrush(colors.Item1, colors.Item2, 35), null, new Rect(0, 0, w, h));
            for (int i = 0; i < 12; i++)
                dc.DrawEllipse(new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255)), null, new Point(w * (i + 1) / 13.0, h * (0.2 + 0.6 * ((i * 7) % 5) / 5.0)), 40 + i * 6, 40 + i * 6);
            dc.DrawText(new FormattedText($"photo {variant + 1}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 64, Brushes.White, 1.0), new Point(30, 20));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
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
