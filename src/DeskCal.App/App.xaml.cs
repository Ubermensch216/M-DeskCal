using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;
using Forms = System.Windows.Forms;

namespace DeskCal.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _board;
    private BoardSettingsStore? _boardStore;

    internal static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskCal");

    private static string HolidaysPath => Path.Combine(DataDir, "holidays.json");

    private KoreanDays _days = new();
    private ReminderService? _reminderService;

    private const string ShowSignalName = "Local\\M-DeskCal-Show";
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private AppPreferencesStore? _prefsStore;
    private AppPreferences _prefs = new();
    private HotkeyManager? _hotkeys;
    private readonly StartupRegistration _startup = new();
    private CountdownRepository? _countdowns;
    private CountdownCardsWindow? _cards;
    private SearchService? _searchService;
    private ItemRepository? _itemRepo;
    private HistoryRepository? _historyRepo;
    private static string ExePath => Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    /// <summary>Tell the instance that is already running to bring its calendar forward (waits briefly while it is still starting).</summary>
    private static void SignalRunningInstance()
    {
        for (int i = 0; i < 20; i++)
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var handle)) { using (handle) handle.Set(); return; }
            Thread.Sleep(100);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "Local\\M-DeskCal-SingleInstance", out bool created);
        if (!created)
        {
            SignalRunningInstance();   // "I was launched again" = the user wants to see the calendar
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => { if (_board is null) return; _board.BringToFront(); RememberVisibility(); }),
            null, Timeout.Infinite, executeOnlyOnce: false);

        Directory.CreateDirectory(DataDir);

        // --db=<path> (dev aid) uses another database file, e.g. a scratch one for snapshots.
        var dbArg = e.Args.FirstOrDefault(a => a.StartsWith("--db="));
        var db = new DeskCalDb(dbArg is null ? Path.Combine(DataDir, "deskcal.db") : dbArg["--db=".Length..]);
        db.Migrate();
        var settings = new SettingsStore(db);
        _boardStore = new BoardSettingsStore(settings);
        _prefsStore = new AppPreferencesStore(settings);
        _prefs = _prefsStore.Load();

        var clock = new SystemClock();
        var items = new ItemRepository(db, clock);
        var events = new EventRepository(db, clock);
        var reminders = new ReminderRepository(db, clock);
        Func<DateOnly, bool> isDayOff = d => _days.Holidays.IsDayOff(d);
        var series = new EventSeriesService(db, clock, isDayOff);
        bool snapshotMode = e.Args.Any(a => a.StartsWith("--snapshot="));

        var history = new HistoryRepository(db, clock);
        _countdowns = new CountdownRepository(db, clock);
        _itemRepo = items; _historyRepo = history;
        _searchService = new SearchService(items, events, isDayOff);

        _board = new MainWindow();
        _board.SetItems(items);
        _board.SetHistory(history);
        _board.SetCountdowns(_countdowns);
        _board.SetEvents(events, series, reminders);
        var loaded = HolidayOverrideFile.Load(HolidaysPath);
        _days = new KoreanDays(loaded.Overrides);
        _board.SetCalendar(_days);
        if (loaded.Errors.Count > 0 && !snapshotMode)
            System.Windows.MessageBox.Show("holidays.json 에 문제가 있어 해당 항목을 건너뛰었습니다.\n\n" + string.Join("\n", loaded.Errors),
                "M-DeskCal 휴일 데이터");
        _board.ApplySettings(_boardStore.Load());

        // Restore the saved board rectangle (physical pixels), ignoring implausible values.
        int w = settings.GetInt("board.w", 640), h = settings.GetInt("board.h", 480);
        if (w >= 320 && h >= 240)
            _board.SetRect(new(settings.GetInt("board.x", 200), settings.GetInt("board.y", 150), w, h));
        _cards = new CountdownCardsWindow();
        _cards.SetPosition(settings.GetInt("cards.x", 880), settings.GetInt("cards.y", 150));
        _cards.Moved += (cx, cy) => { settings.SetInt("cards.x", cx); settings.SetInt("cards.y", cy); };
        _board.RectCommitted += r =>
        {
            settings.SetInt("board.x", r.X); settings.SetInt("board.y", r.Y);
            settings.SetInt("board.w", r.Width); settings.SetInt("board.h", r.Height);
        };

        var posArg = e.Args.FirstOrDefault(a => a.StartsWith("--pos="));
        if (posArg is not null && posArg[6..].Split(',') is [var px, var py] &&
            int.TryParse(px, out int x) && int.TryParse(py, out int y))
            _board.SetPosition(x, y); // dev aid: physical screen pixels

        if (snapshotMode)
        {
            // Dev aid: render the UI offscreen and run editor self-tests, then exit.
            var dir = e.Args.First(a => a.StartsWith("--snapshot="))["--snapshot=".Length..];
            DevHarness.Run(_board, items, series, events, reminders, _days, dir, e.Args.Contains("--seed"), _countdowns!, _searchService!, history);
            Shutdown();
            return;
        }

        // A launch started by Windows restores the last visibility; a launch by the user always shows the calendar.
        bool autoStart = e.Args.Contains(StartupRegistration.AutoStartFlag);
        bool startHidden = autoStart && !_prefs.BoardVisible;
        if (e.Args.Contains("--plain")) _board.Show();             // --plain: stay a normal window (debug)
        else if (!startHidden) _board.ShowBoard();

        if (e.Args.Contains("--demo-reminder") && dbArg is not null)
        {
            // Dev aid (scratch DB only): an event 3 minutes from now with a reminder 2 minutes before it -> fires within a minute.
            var start = DateTime.Now.AddMinutes(3);
            series.Create(new EventDraft("데모 알림 일정", "", DateOnly.FromDateTime(start), new TimeOnly(start.Hour, start.Minute), null, null,
                RecurrenceRule.None, null, [new ReminderSpec(2)]));
        }

        if (!e.Args.Contains("--no-reminders"))
        {
            var presenter = new ReminderPresenter();
            _reminderService = new ReminderService(new ReminderProcessor(db, clock, isDayOff),
                msgs => presenter.Show(msgs, _board.Settings.ReminderSound), missedWindow: TimeSpan.FromHours(24));
            _board.RemindersChanged += _reminderService.Reschedule;
            _reminderService.Start();
        }

        UpdateCards();
        _trayDay = DateTime.Today;
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseDoubleClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ToggleBoard(); };
        UpdateTrayText(failed: []);

        // The icon shows today's date; redraw it when the day changes.
        _trayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _trayTimer.Tick += (_, _) =>
        {
            if (DateTime.Today == _trayDay) return;
            _trayDay = DateTime.Today;
            UpdateCards();
            var old = _tray.Icon;
            _tray.Icon = TrayIcon.Create();
            old?.Dispose();
        };
        _trayTimer.Start();

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += action => Dispatcher.BeginInvoke(() => OnHotkey(action));
        UpdateTrayText(_hotkeys.Apply(_prefs));

        // Keep the Run entry pointing at the current exe if the program folder was moved.
        try { if (_startup.IsStale(ExePath)) _startup.Enable(ExePath); } catch { /* registry unavailable: leave it */ }

        if (e.Args.Contains("--edit")) EditItem.Checked = true; // dev aid: start in edit mode
    }

    // ---- visibility, shortcuts, auto-start ------------------------------------

    private DateTime _trayDay;
    private System.Windows.Threading.DispatcherTimer? _trayTimer;

    private void ToggleBoard()
    {
        if (_board!.BoardVisible) _board.HideBoard(); else _board.BringToFront();
        RememberVisibility();
    }

    private void RememberVisibility()
    {
        _prefs = _prefs with { BoardVisible = _board!.BoardVisible };
        _prefsStore?.Save(_prefs);
        UpdateCards();   // the cards come and go with the calendar
    }

    /// <summary>Redraw the desktop D-day cards (and the strip under the calendar).</summary>
    private void UpdateCards()
    {
        if (_cards is null || _countdowns is null || _board is null) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _cards.Update(CountdownCalc.Ordered(_countdowns.List().Where(c => c.ShowCard), today), _board.Settings, _board.BoardVisible);
    }

    private void CountdownsChanged() { UpdateCards(); _board?.Refresh(); }

    private void OpenSearch() =>
        SearchWindow.ShowOrActivate(_searchService!, _itemRepo!, _historyRepo!,
            day => { _board!.GoTo(day); _board.BringToFront(); RememberVisibility(); },
            () => _board!.Refresh());

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.BringToFront:
                _board!.BringToFront(); RememberVisibility();
                break;
            case HotkeyAction.ToggleVisible:
                ToggleBoard();
                break;
            case HotkeyAction.NewEvent:
                _board!.BringToFront(); RememberVisibility();
                _board.NewEvent(DateOnly.FromDateTime(DateTime.Today));
                break;
            case HotkeyAction.Search:
                OpenSearch();
                break;
        }
    }

    /// <summary>Tray tooltip: the name plus the active shortcuts, and a warning for any that could not be registered.</summary>
    private void UpdateTrayText(IReadOnlyList<HotkeyAction> failed)
    {
        if (_tray is null) return;
        string text = "M-DeskCal";
        if (_prefs.HotkeysEnabled)
        {
            text += $"\n{_prefs.BringToFront.Format()} 앞으로 · {_prefs.ToggleVisible.Format()} 숨김";
            if (failed.Count > 0) text += $"\n사용 중이라 등록 못 함: {failed.Count}개";
        }
        _tray.Text = text.Length <= 63 ? text : text[..63];   // NotifyIcon.Text is limited to 63 characters
        if (failed.Count > 0)
            _tray.ShowBalloonTip(5000, "M-DeskCal 단축키",
                "다른 프로그램이 이미 사용 중인 단축키가 있습니다: " + string.Join(", ", failed.Select(a => $"{HotkeyManager.ActionName(a)}({_prefs.Get(a).Format()})"))
                + "\n트레이 메뉴의 '단축키 설정…'에서 바꿀 수 있습니다.", Forms.ToolTipIcon.Warning);
    }

    private void ApplyPrefs(AppPreferences next)
    {
        _prefs = next;
        _prefsStore!.Save(_prefs);
        UpdateTrayText(_hotkeys?.Apply(_prefs) ?? []);
    }

    // ---- tray menu -----------------------------------------------------------

    private Forms.ToolStripMenuItem EditItem = null!;

    private Forms.ContextMenuStrip BuildMenu()
    {
        var board = _board!;
        var menu = new Forms.ContextMenuStrip();
        var refresh = new List<Action>();   // re-sync check marks each time the menu opens

        var showItem = new Forms.ToolStripMenuItem();
        showItem.Click += (_, _) => ToggleBoard();
        menu.Items.Add(showItem);
        refresh.Add(() => showItem.Text = board.BoardVisible ? "달력 숨기기" : "달력 보이기");
        menu.Items.Add("새 일정 (오늘)…", null, (_, _) => OnHotkey(HotkeyAction.NewEvent));
        menu.Items.Add("검색…", null, (_, _) => OpenSearch());
        menu.Items.Add("디데이 관리…", null, (_, _) => CountdownWindow.ShowOrActivate(_countdowns!, CountdownsChanged));
        EditItem = new Forms.ToolStripMenuItem("편집 모드 (위치/크기 조정)") { CheckOnClick = true };
        EditItem.CheckedChanged += (_, _) => { board.SetEditMode(EditItem.Checked); _cards?.SetEditMode(EditItem.Checked); };
        menu.Items.Add(EditItem);
        menu.Items.Add("오늘로 이동", null, (_, _) => board.GoToday());
        menu.Items.Add(new Forms.ToolStripSeparator());

        // display mode
        var modeMenu = new Forms.ToolStripMenuItem("표시 모드");
        (DisplayMode Mode, string Name)[] modes =
        [
            (DisplayMode.FixedMonth, "고정 월"), (DisplayMode.FloatingMonth, "떠다니는 월 (주 단위)"),
            (DisplayMode.FullMonth, "전체 월 (6줄 고정)"), (DisplayMode.SingleLine, "한 줄 (월 전체)"),
            (DisplayMode.SingleLineFloating, "한 줄 (오늘부터)"),
        ];
        foreach (var (mode, name) in modes)
        {
            var item = new Forms.ToolStripMenuItem(name);
            item.Click += (_, _) => Update(s => s with { Mode = mode });
            modeMenu.DropDownItems.Add(item);
            refresh.Add(() => item.Checked = board.Settings.Mode == mode);
        }
        menu.Items.Add(modeMenu);

        // first day of week
        var dowMenu = new Forms.ToolStripMenuItem("주 시작 요일");
        foreach (var (dow, name) in new[] { (DayOfWeek.Sunday, "일요일"), (DayOfWeek.Monday, "월요일"), (DayOfWeek.Saturday, "토요일") })
        {
            var item = new Forms.ToolStripMenuItem(name);
            item.Click += (_, _) => Update(s => s with { FirstDayOfWeek = dow });
            dowMenu.DropDownItems.Add(item);
            refresh.Add(() => item.Checked = board.Settings.FirstDayOfWeek == dow);
        }
        menu.Items.Add(dowMenu);

        // calendar annotations
        AddToggle("음력 표시", s => s.ShowLunar, (s, v) => s with { ShowLunar = v });
        AddToggle("24절기 표시", s => s.ShowSolarTerms, (s, v) => s with { ShowSolarTerms = v });
        AddToggle("공휴일 표시", s => s.ShowHolidays, (s, v) => s with { ShowHolidays = v });
        AddToggle("기념일·명절 표시", s => s.ShowFestivals, (s, v) => s with { ShowFestivals = v });
        menu.Items.Add("휴일 데이터 편집…", null, (_, _) =>
        {
            try
            {
                HolidayOverrideFile.EnsureTemplate(HolidaysPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HolidaysPath) { UseShellExecute = true });
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "휴일 데이터 편집"); }
        });
        menu.Items.Add("휴일 데이터 다시 읽기", null, (_, _) =>
        {
            var r = HolidayOverrideFile.Load(HolidaysPath);
            _days = new KoreanDays(r.Overrides);
            board.SetCalendar(_days);
            System.Windows.MessageBox.Show(
                r.Errors.Count == 0 ? $"사용자 휴일 {r.Overrides.Count}건을 적용했습니다."
                                    : $"{r.Overrides.Count}건 적용, 문제 {r.Errors.Count}건:\n\n" + string.Join("\n", r.Errors),
                "M-DeskCal 휴일 데이터");
        });
        menu.Items.Add(new Forms.ToolStripSeparator());

        // items
        var bulletMenu = new Forms.ToolStripMenuItem("항목 번호/기호");
        foreach (var (style, name) in new[] { (BulletStyle.Dot, "점 (•)"), (BulletStyle.Number, "번호 (1. 2.)"), (BulletStyle.None, "없음") })
        {
            var item = new Forms.ToolStripMenuItem(name);
            item.Click += (_, _) => Update(s => s with { Bullet = style });
            bulletMenu.DropDownItems.Add(item);
            refresh.Add(() => item.Checked = board.Settings.Bullet == style);
        }
        menu.Items.Add(bulletMenu);
        AddToggle("완료된 항목 표시", s => s.ShowCompleted, (s, v) => s with { ShowCompleted = v });
        AddToggle("완료 효과음", s => s.CompleteSound, (s, v) => s with { CompleteSound = v });
        AddToggle("알림음", s => s.ReminderSound, (s, v) => s with { ReminderSound = v });

        // toggles
        AddToggle("주차 표시", s => s.ShowWeekNumbers, (s, v) => s with { ShowWeekNumbers = v });
        AddToggle("쉬는 날 색상 표시", s => s.ShowOffDays, (s, v) => s with { ShowOffDays = v });
        AddToggle("그림자", s => s.Shadow, (s, v) => s with { Shadow = v });

        // opacity
        var opMenu = new Forms.ToolStripMenuItem("배경 투명도");
        foreach (int pct in new[] { 0, 20, 40, 60, 80, 100 })
        {
            var item = new Forms.ToolStripMenuItem(pct == 0 ? "완전 투명" : $"{pct}%");
            item.Click += (_, _) => Update(s => s with { BackgroundOpacity = pct / 100.0 });
            opMenu.DropDownItems.Add(item);
            refresh.Add(() => item.Checked = Math.Abs(board.Settings.BackgroundOpacity - pct / 100.0) < 0.001);
        }
        menu.Items.Add(opMenu);

        menu.Items.Add("표시 설정 기본값으로", null, (_, _) =>
        {
            _boardStore!.ResetToDefaults();
            board.ApplySettings(_boardStore.Load());
            UpdateCards();
        });

        menu.Items.Add(new Forms.ToolStripSeparator());

        // shortcuts and start-up
        var hotkeysItem = new Forms.ToolStripMenuItem("전역 단축키 사용");
        hotkeysItem.Click += (_, _) => ApplyPrefs(_prefs with { HotkeysEnabled = !_prefs.HotkeysEnabled });
        menu.Items.Add(hotkeysItem);
        refresh.Add(() => hotkeysItem.Checked = _prefs.HotkeysEnabled);
        menu.Items.Add("단축키 설정…", null, (_, _) =>
        {
            if (HotkeyDialog.Edit(_prefs) is { } edited) ApplyPrefs(edited with { HotkeysEnabled = _prefs.HotkeysEnabled });
        });

        var startupItem = new Forms.ToolStripMenuItem("Windows 시작 시 자동 실행");
        startupItem.Click += (_, _) =>
        {
            try
            {
                if (_startup.IsEnabled()) _startup.Disable(); else _startup.Enable(ExePath);
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("자동 실행 설정을 바꾸지 못했습니다.\n\n" + ex.Message, "M-DeskCal"); }
        };
        menu.Items.Add(startupItem);
        refresh.Add(() => { try { startupItem.Checked = _startup.IsEnabled(); } catch { startupItem.Checked = false; } });

        menu.Items.Add("데이터 폴더 열기", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataDir) { UseShellExecute = true }); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "데이터 폴더"); }
        });

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("진단 정보 보기", null, (_, _) =>
            System.Windows.MessageBox.Show(board.Diagnostics(), "M-DeskCal 진단"));
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        menu.Opening += (_, _) => { foreach (var r in refresh) r(); };
        return menu;

        void Update(Func<BoardSettings, BoardSettings> change)
        {
            var next = change(board.Settings);
            board.ApplySettings(next);
            _boardStore!.Save(board.Settings);
            UpdateCards();
        }

        void AddToggle(string name, Func<BoardSettings, bool> get, Func<BoardSettings, bool, BoardSettings> set)
        {
            var item = new Forms.ToolStripMenuItem(name);
            item.Click += (_, _) => Update(s => set(s, !get(s)));
            menu.Items.Add(item);
            refresh.Add(() => item.Checked = get(board.Settings));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayTimer?.Stop();
        _hotkeys?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _reminderService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
