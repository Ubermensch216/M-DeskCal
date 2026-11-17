using System.Globalization;
using System.IO;
using System.Windows;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;
using Forms = System.Windows.Forms;
using WpfMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

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
    private ExchangeService? _exchange;
    private DeskCalDb? _db;
    private ItemRepository? _itemRepo;
    private HistoryRepository? _historyRepo;
    private EventRepository? _eventRepo;
    private NoteRepository? _noteRepo;
    private TimetableRepository? _timetableRepo;
    private SettingsStore? _settingsStore;
    private ContentLockStore? _lock;
    private TimetableWidgetWindow? _ttWidget;
    private SyncCoordinator? _sync;
    private System.Windows.Threading.DispatcherTimer? _syncTimer, _mcpTimer, _gitTimer;
    private GitSyncCoordinator? _gitSync;
    private McpAccess? _mcpAccess;
    private McpRepository? _mcpRepo;
    private McpChanges? _mcpChanges;
    private DeskCal.Ai.AiSettingsStore? _aiStore;
    private DeskCal.Ai.QuickInputService? _quickInput;
    private DeskCal.Ai.AiBatchService? _batch;
    private DeskCal.Ai.Intent.ExtractionService? _extraction;
    private DeskCal.Ai.Intent.QueryService? _query;
    private DeskCal.Data.EmbeddingRepository? _embedRepo;
    private DeskCal.Ai.EmbeddingSync? _embedSync;
    private DeskCal.Ai.SemanticSearchService? _semantic;
    private DeskCal.Ai.BriefingSettingsStore? _briefStore;
    private DeskCal.Ai.BriefingBackoff? _backoff;
    private DeskCal.Ai.WeeklyReviewBuilder? _reviewBuilder;
    private DeskCal.Ai.RolloverService? _rollover;
    private System.Windows.Threading.DispatcherTimer? _aiTimer;
    private int _aiTick;
    private DeskCal.Ai.AccuracyStore? _accuracy;
    private DeskCal.Ai.ConflictService? _conflicts;
    private int _mcpNotified;
    private bool _mcpBalloon;
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
        // The calendar is flat text on a translucent panel: software rendering is plenty. With hardware rendering the graphics driver
        // reserved ~600 MB (8 x 64 MB write-combined pools) for the always-on widget; software rendering keeps it near 50 MB.
        // DESKCAL_HARDWARE_RENDER=1 switches the old behaviour back on.
        if (Environment.GetEnvironmentVariable("DESKCAL_HARDWARE_RENDER") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Dev aid: an offscreen snapshot run on a scratch database (--snapshot=DIR --db=FILE) never shows a window or touches the real
        // data, so it may run next to the user's own running calendar instead of handing over to it.
        bool snapshotMode = e.Args.Any(a => a.StartsWith("--snapshot="));
        var dbArg = e.Args.FirstOrDefault(a => a.StartsWith("--db="));
        if ((snapshotMode || e.Args.Contains("--seed") || e.Args.Contains("--docshots")) && dbArg is null)
        {
            Console.Error.WriteLine("--snapshot / --seed / --docshots require --db=<scratch.db>.");
            Shutdown(2);
            return;
        }
        bool offscreenDev = snapshotMode && dbArg is not null;
        if (!offscreenDev)
        {
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
        }

        Directory.CreateDirectory(DataDir);

        // --db=<path> (dev aid) uses another database file, e.g. a scratch one for snapshots.
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
        Func<DateOnly, bool> isDayOff = d => _days.Holidays.IsDayOff(d);      // public holidays only (callers add the weekend where they need it)
        var series = new EventSeriesService(db, clock, isDayOff);

        var history = new HistoryRepository(db, clock);
        _countdowns = new CountdownRepository(db, clock);
        _itemRepo = items; _historyRepo = history;
        _searchService = new SearchService(items, events, isDayOff);
        _exchange = new ExchangeService(db, clock, isDayOff);
        _db = db;
        _eventRepo = events;
        _noteRepo = new NoteRepository(db, clock);
        _timetableRepo = new TimetableRepository(db, clock);
        _settingsStore = settings;
        _lock = new ContentLockStore(settings);
        _sync = new SyncCoordinator(db, clock, isDayOff, new DpapiSecretProtector(), settings);
        _gitSync = new GitSyncCoordinator(db, clock, settings, DataDir, HolidaysPath);
        _mcpAccess = new McpAccess(settings);
        _mcpRepo = new McpRepository(db, clock);
        _mcpChanges = new McpChanges(db, clock, isDayOff);
        _aiStore = new DeskCal.Ai.AiSettingsStore(settings);
        _conflicts = new DeskCal.Ai.ConflictService(events, isDayOff);
        _quickInput = new DeskCal.Ai.QuickInputService(_aiStore, new DeskCal.Ai.AiGate(settings), _mcpChanges, _mcpRepo, conflicts: _conflicts);
        _batch = new DeskCal.Ai.AiBatchService(db, _mcpChanges, settings, clock, Path.Combine(DataDir, "backup"));
        _extraction = new DeskCal.Ai.Intent.ExtractionService(_mcpChanges, _conflicts, items);
        _query = new DeskCal.Ai.Intent.QueryService(events, items, isDayOff, _timetableRepo, settings);
        var aiGate = new DeskCal.Ai.AiGate(settings);
        _embedRepo = new DeskCal.Data.EmbeddingRepository(db, clock);
        _embedSync = new DeskCal.Ai.EmbeddingSync(_embedRepo, items, events, _aiStore, aiGate);
        _semantic = new DeskCal.Ai.SemanticSearchService(_embedRepo, items, events, _searchService!, _aiStore, aiGate);
        _briefStore = new DeskCal.Ai.BriefingSettingsStore(settings);
        _backoff = new DeskCal.Ai.BriefingBackoff(settings, clock);
        _reviewBuilder = new DeskCal.Ai.WeeklyReviewBuilder(events, items, new DeskCal.Data.ActivityRepository(db, clock), isDayOff);
        _accuracy = new DeskCal.Ai.AccuracyStore(settings);
        _rollover = new DeskCal.Ai.RolloverService(items, new DeskCal.Data.ActivityRepository(db, clock), settings, clock, IsNonWorkingDay, day =>
            DeskCal.Core.FreeTimeFinder.BusyFromEvents(DeskCal.Core.EventOccurrences.Expand(events.GetAllActive(), events.GetExceptions(), day, day, isDayOff))
                .Where(b => b.Date == day).Sum(b => b.EndMinute - b.StartMinute));
        // a notice in the event editor when the new event overlaps or repeats another (not in the scripted snapshot runs: they must never wait for a click)
        if (!snapshotMode)
            EventEditorWindow.ClashCheck = (day, title, start, end, ownId) => _conflicts!.Check(day, title, start, end, ownId).Select(c => c.Describe()).ToList();

        if (dbArg is not null && e.Args.FirstOrDefault(a => a.StartsWith("--sync-check=")) is { } syncCheck)
        {
            // Dev aid (scratch DB only): --sync-check=<url>|<user>|<password>|<report file> runs one real sync through the
            // same code the sync window uses (DPAPI-protected password included), writes what happened, and exits.
            SyncCheck(_sync, syncCheck["--sync-check=".Length..].Split('|'));
            Shutdown();
            return;
        }

        if (dbArg is not null && e.Args.FirstOrDefault(a => a.StartsWith("--ocr-check=")) is { } ocrCheck)
        {
            // Dev aid (scratch DB only): --ocr-check=<picture>|<report file>|<base day yyyy-MM-dd> reads the picture with the same OCR code the
            // extraction window uses, runs the text through the same extractor, writes both, and exits.
            OcrCheck(ocrCheck["--ocr-check=".Length..].Split('|'));
            Shutdown();
            return;
        }

        _board = new MainWindow();
        _board.SetItems(items);
        _board.SetHistory(history);
        _board.SetCountdowns(_countdowns);
        _board.SetEvents(events, series, reminders);
        var loaded = HolidayOverrideFile.Load(HolidaysPath);
        _days = new KoreanDays(loaded.Overrides);
        _board.SetCalendar(_days);
        _board.SetSettingsStore(settings);
        _board.SetItemSeries(new ItemSeriesRepository(db, clock, isDayOff));      // repeating items: also creates the occurrences that came into range
        _board.CountdownsChanged += CountdownsChanged;
        if (loaded.Errors.Count > 0 && !snapshotMode)
            System.Windows.MessageBox.Show("holidays.json 에 문제가 있어 해당 항목을 건너뛰었습니다.\n\n" + string.Join("\n", loaded.Errors),
                "M-DeskCal 휴일 데이터");
        _board.ApplySettings(_boardStore.Load());
        _board.EnsureUnlocked = EnsureUnlocked;
        _board.GearClicked += ShowQuickMenu;
        _board.SyncClicked += OnHeaderSyncClick;
        _board.SyncResultClicked += OpenPreferredSyncSettings;
        _board.SyncAnchor.ContextMenu = BuildSyncButtonMenu();
        _board.ChatSubmitted += prompt => OpenCalendarChat(prompt);
        if (_lock.Hidden) _board.SetContentHidden(true);      // a restart must not undo the lock

        // Restore the saved board rectangle (physical pixels), falling back onto a connected monitor when off-screen or implausible.
        var screens = Forms.Screen.AllScreens
            .Select(sc => new PixelRect(sc.WorkingArea.X, sc.WorkingArea.Y, sc.WorkingArea.Width, sc.WorkingArea.Height))
            .ToList();
        var priWa = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var primary = new PixelRect(priWa.X, priWa.Y, priWa.Width, priWa.Height);

        int w = settings.GetInt("board.w", 640), h = settings.GetInt("board.h", 480);
        if (w >= 320 && h >= 240)
        {
            var saved = new PixelRect(settings.GetInt("board.x", 200), settings.GetInt("board.y", 150), w, h);
            var restored = WindowBounds.RestoreOrFallback(saved, screens, primary, new(200, 150, 640, 480));
            _board.SetRect(new(restored.X, restored.Y, restored.Width, restored.Height));
        }
        _cards = new CountdownCardsWindow();
        var (cx, cy) = WindowBounds.RestorePointOrFallback(settings.GetInt("cards.x", 880), settings.GetInt("cards.y", 150), screens, primary, 880, 150);
        _cards.SetPosition(cx, cy);
        _ttWidget = new TimetableWidgetWindow();
        var (tx, ty) = WindowBounds.RestorePointOrFallback(settings.GetInt("timetable.x", 880), settings.GetInt("timetable.y", 420), screens, primary, 880, 420);
        _ttWidget.SetPosition(tx, ty);
        if (!snapshotMode)
        {
            _cards.Moved += (mcx, mcy) => { settings.SetInt("cards.x", mcx); settings.SetInt("cards.y", mcy); };
            _ttWidget.Moved += (mtx, mty) => { settings.SetInt("timetable.x", mtx); settings.SetInt("timetable.y", mty); };
            _board.RectCommitted += r =>
            {
                settings.SetInt("board.x", r.X); settings.SetInt("board.y", r.Y);
                settings.SetInt("board.w", r.Width); settings.SetInt("board.h", r.Height);
            };
        }

        var posArg = e.Args.FirstOrDefault(a => a.StartsWith("--pos="));
        if (posArg is not null && posArg[6..].Split(',') is [var px, var py] &&
            int.TryParse(px, out int x) && int.TryParse(py, out int y))
            _board.SetPosition(x, y); // dev aid: physical screen pixels

        if (snapshotMode)
        {
            // Dev aid: render the UI offscreen and run editor self-tests, then exit.
            var dir = e.Args.First(a => a.StartsWith("--snapshot="))["--snapshot=".Length..];
            DevHarness.Run(_board, items, series, events, reminders, _days, dir, e.Args.Contains("--seed"), _countdowns!, _searchService!, history, _exchange!, _noteRepo!, _timetableRepo!, settings);
            if (e.Args.Contains("--docshots")) DocShots(dir);
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
                msgs => presenter.Show(_board.ContentHidden ? msgs.Select(m => m with { Title = "🔒 잠긴 알림" }).ToList() : msgs, _board.Settings.ReminderSound), missedWindow: TimeSpan.FromHours(24));
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

        StartBackgroundWork();
        if (dbArg is not null)   // dev aids (scratch DB only): open a window at start-up so it can be looked at
        {
            if (e.Args.Contains("--open-sync")) OpenSync();
            if (e.Args.Contains("--open-mcp")) OpenMcp(approvals: true);
        }

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

    /// <summary>Redraw the desktop widgets: the D-day cards and the timetable. They follow the calendar's visibility and the content lock.</summary>
    private void UpdateCards()
    {
        if (_cards is null || _countdowns is null || _board is null) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        bool show = _board.BoardVisible && !_board.ContentHidden;
        _cards.Update(CountdownCalc.Ordered(_countdowns.List().Where(c => c.ShowCard), today), _board.Settings, show);
        if (_ttWidget is not null && _timetableRepo is not null && _settingsStore is not null)
            _ttWidget.Update(_timetableRepo.LoadConfig(_settingsStore), _timetableRepo.GetCells(), _board.Settings,
                show && _settingsStore.GetBool("timetable.widget", false));
    }

    // ---- content lock ---------------------------------------------------------------

    /// <summary>Called whenever something wants to show or change the user's content: true when it may (asks for the password if needed).</summary>
    private bool EnsureUnlocked()
    {
        if (!_board!.ContentHidden) return true;
        if (_lock!.HasPassword && !LockUi.Unlock(_lock)) return false;
        SetLocked(false);
        return true;
    }

    private void SetLocked(bool locked)
    {
        _board!.SetContentHidden(locked);
        _lock!.Hidden = locked;
        if (locked)
            foreach (var w in Windows.OfType<Window>().Where(w => w is SearchWindow or NotesWindow or TimetableWindow or CountdownWindow).ToList()) w.Close();
        UpdateCards();
    }

    private void OpenTimetable() { if (EnsureUnlocked()) TimetableWindow.ShowOrActivate(_timetableRepo!, _settingsStore!, UpdateCards); }

    private void PrintMonth()
    {
        if (!EnsureUnlocked()) return;
        var a = _board!.Anchor;
        PrintUi.Print(PrintableMonth.Gather(new DateOnly(a.Year, a.Month, 1), _board.Settings.FirstDayOfWeek, _itemRepo!, _eventRepo!, d => _days.Holidays.IsDayOff(d), _days));
    }

    /// <summary>Redraw everything an import can touch and re-arm the reminder timer for the new reminders.</summary>
    private void AfterImport()
    {
        _board?.Refresh();
        UpdateCards();
        _reminderService?.Reschedule();
    }

    private void CountdownsChanged() { UpdateCards(); _board?.Refresh(); }

    private void OpenCalendarChat(string? initialPrompt = null)
    {
        if (!EnsureUnlocked()) return;
        CalendarChatWindow.ShowOrActivate(new CalendarChatHost(
            _quickInput!, _batch!, _query!, _accuracy!,
            _searchService!, _itemRepo!, _historyRepo!,
            () => DateOnly.FromDateTime(DateTime.Today),
            () => _board!.Settings.FirstDayOfWeek,
            AfterImport,
            day => { _board!.GoToAndHighlight(day); _board.BringToFront(CalendarChatWindow.CurrentWindow); RememberVisibility(); },
            day => _board!.NewEvent(day),
            _semantic,
            SyncIndexAsync,
            () => _aiStore!.Load(),
            s => DeskCal.Ai.Llm.LlmClientFactory.Create(s),
            _extraction),
            initialPrompt);
    }

    // ---- meaning search index, morning briefing -----------------------------------------------------

    private void OcrCheck(string[] a)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var baseDay = a.Length > 2 && DateOnly.TryParse(a[2], out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
            var r = Task.Run(() => OcrReader.ReadFileAsync(a[0])).GetAwaiter().GetResult();
            sb.AppendLine($"language={r.Language} lines={r.Lines} elapsed={r.Elapsed.TotalSeconds:F2}s");
            sb.AppendLine("--- raw"); sb.AppendLine(r.Raw);
            sb.AppendLine("--- cleaned"); sb.AppendLine(r.Text);
            sb.AppendLine("--- extracted (base day " + baseDay + ")");
            foreach (var row in _extraction!.Analyze(r.Text, baseDay, DayOfWeek.Monday))
                sb.AppendLine($"{row.Candidate.Kind} | {row.Candidate.Title} | {(row.Proposal is null ? "-" : row.Proposal.Summary)} | {row.Candidate.Reason}");
        }
        catch (Exception ex) { sb.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message); }
        File.WriteAllText(a.Length > 1 ? a[1] : "ocr-check.txt", sb.ToString(), new System.Text.UTF8Encoding(false));
    }

    private async Task SyncIndexAsync()
    {
        try { if (_embedSync!.Enabled) await _embedSync.SyncAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or DeskCal.Ai.Llm.LlmException) { /* the next round tries again */ }
    }

    private string IndexStatus()
    {
        var ai = _aiStore!.Load();
        if (!ai.SemanticSearch) return "의미 검색이 꺼져 있습니다.";
        if (ai.EmbedModel.Length == 0) return "임베딩 모델을 고르세요.";
        try { return $"색인 {_embedRepo!.Count()}건 · 새로 계산할 글 {_embedSync!.Pending()}건"; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return "색인 상태를 읽을 수 없습니다."; }
    }

    private async Task<string> BuildIndexAsync()
    {
        if (!_embedSync!.Enabled) return "AI 기능과 의미 검색을 켜고 임베딩 모델을 고른 뒤(달력이 잠겨 있지 않을 때) 만들 수 있습니다.";
        var r = await _embedSync.SyncAsync(CancellationToken.None, 2000);
        return $"{r.Embedded}건을 계산했습니다 (지운 것 {r.Removed}건, 남은 것 {r.Pending}건)." + (r.Error is { } e ? " 오류: " + e : "") + " " + IndexStatus();
    }

    private BriefingHost MakeBriefingHost() => new(
        () => DateOnly.FromDateTime(DateTime.Today),
        (day, today) => new DeskCal.Ai.BriefingBuilder(_eventRepo!, _itemRepo!, _countdowns!, _timetableRepo!, _settingsStore!, _days, d => _days.Holidays.IsDayOff(d)).Build(day, today),
        last => _reviewBuilder!.Build(last), () => _aiStore!.Load(), s => DeskCal.Ai.Llm.LlmClientFactory.Create(s), _backoff!,
        day => { _board!.GoToAndHighlight(day); _board.BringToFront(BriefingWindow.CurrentWindow); RememberVisibility(); },
        () => _rollover!.Find(DateOnly.FromDateTime(DateTime.Today)).Count, OpenRollover);

    /// <summary>Unfinished to-dos of past days: move them to today, or spread them over the next working days.</summary>
    private void OpenRollover()
    {
        if (!EnsureUnlocked()) return;
        RolloverWindow.ShowOrActivate(MakeRolloverHost());
    }

    private RolloverHost MakeRolloverHost() => new(
        _rollover!, () => DateOnly.FromDateTime(DateTime.Today), IsNonWorkingDay, AfterImport, day => _board!.GoTo(day));

    /// <summary>Weekend or public holiday: what "working day" planning (rollover, review plan) steers around.</summary>
    private bool IsNonWorkingDay(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || _days.Holidays.IsDayOff(d);

    private void OpenBriefing(bool auto = false)
    {
        if (auto ? _board!.ContentHidden : !EnsureUnlocked()) return;
        var ai = _aiStore!.Load();
        BriefingWindow.Show(MakeBriefingHost(), auto, polishAutomatically: auto && _briefStore!.Load().UseLlm && ai.Enabled && ai.Model.Length > 0);
    }

    /// <summary>Once a minute: show the morning briefing when it is due, and keep the meaning-search index up to date (every ten minutes).</summary>
    private void StartAiWork()
    {
        _aiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _aiTimer.Tick += (_, _) =>
        {
            _aiTimer!.Interval = TimeSpan.FromMinutes(1);
            if (_board is null) return;
            try
            {
                var now = DateTime.Now;
                var s = _briefStore!.Load();
                if (s.ShouldShow(now, _days.Holidays.IsDayOff(DateOnly.FromDateTime(now))) && !_board.ContentHidden)
                {
                    _briefStore.MarkShown(DateOnly.FromDateTime(now));
                    OpenBriefing(auto: true);
                }
                if (_aiTick++ % 10 == 1) _ = SyncIndexAsync();
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* try again next minute */ }
        };
        _aiTimer.Start();
    }

    // ---- external calendar sync and the AI connection ------------------------------------------------

    private System.Windows.Threading.DispatcherTimer? _maintTimer;

    /// <summary>
    /// An always-on widget allocates almost nothing, so the garbage collector rarely runs, and handles of finished threads and
    /// events (about 12 per minute here) stay open until the objects holding them are finalized. Measured: handles grew steadily
    /// while memory stayed flat. A collection every few minutes returns them; it costs a few milliseconds.
    /// </summary>
    private void StartMaintenance()
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("DESKCAL_MAINT_SECONDS"), out int s) && s >= 10 ? s : 300;
        _maintTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        _maintTimer.Tick += (_, _) =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        };
        _maintTimer.Start();
    }

    private void StartBackgroundWork()
    {
        StartMaintenance();
        StartAiWork();
        _sync!.StateChanged += UpdateHeaderSyncRunning;
        _sync.Finished += (account, report, error) =>
        {
            if (report?.ChangedLocal == true) AfterImport();
            if (!_headerSyncActive) _board?.SetSyncResult(BuildCalDavBadge(account.Name, report, error));
            if (error is not null && _syncUserVisible) _tray?.ShowBalloonTip(5000, "외부 캘린더 동기화", error, Forms.ToolTipIcon.Warning);
        };

        // sync a little after start-up, then at the chosen interval
        _syncTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _syncTimer.Tick += async (_, _) =>
        {
            int minutes = _sync.IntervalMinutes;
            _syncTimer.Interval = TimeSpan.FromMinutes(minutes > 0 ? minutes : 5);   // when off, only look again for a changed setting
            if (minutes <= 0 || _sync.Running || (_board?.ContentHidden ?? false)) return;
            _syncUserVisible = false;
            await _sync.RunAllAsync();
        };
        _syncTimer.Start();

        // Git sync: a little after start-up (so changes made on another computer arrive), then at the chosen interval
        _gitSync!.StateChanged += UpdateHeaderSyncRunning;
        _gitSync.Finished += (report, error) =>
        {
            if (report?.HolidaysChanged == true) ApplyHolidaysFile();
            if (report?.ChangedLocal == true) AfterImport();
            if (!_headerSyncActive) _board?.SetSyncResult(BuildGitBadge(report, error));
            if (error is not null && _gitUserVisible) _tray?.ShowBalloonTip(5000, "Git 동기화", error, Forms.ToolTipIcon.Warning);
        };
        _gitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _gitTimer.Tick += async (_, _) =>
        {
            int minutes = _gitSync.Config.IntervalMinutes;
            _gitTimer.Interval = TimeSpan.FromMinutes(minutes > 0 ? minutes : 5);   // when off, only look again for a changed setting
            if (minutes <= 0 || !_gitSync.Configured || _gitSync.Running || (_board?.ContentHidden ?? false)) return;
            _gitUserVisible = false;
            await _gitSync.RunAsync();
        };
        _gitTimer.Start();

        // AI requests: tell the user when new ones arrive (without saying what they are: the calendar may be locked)
        _mcpTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _mcpTimer.Tick += (_, _) =>
        {
            if (!_mcpAccess!.Enabled) { _mcpNotified = 0; return; }
            try
            {
                _mcpRepo!.ExpireOld();
                int n = _mcpRepo.CountWaiting();
                if (n > _mcpNotified && _tray is not null)
                {
                    _mcpBalloon = true;
                    _tray.ShowBalloonTip(8000, "AI 요청", $"AI 가 보낸 변경 요청 {n}건이 승인을 기다립니다. 눌러서 확인하세요.", Forms.ToolTipIcon.Info);
                }
                _mcpNotified = n;
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* busy: try again in 5 s */ }
        };
        _mcpTimer.Start();
        _tray!.BalloonTipClicked += (_, _) => { if (_mcpBalloon) { _mcpBalloon = false; OpenMcp(approvals: true); } };
    }

    private bool _syncUserVisible;
    private bool _headerSyncActive;

    private void UpdateHeaderSyncRunning() =>
        _board?.SetSyncRunning(_headerSyncActive || (_sync?.Running ?? false) || (_gitSync?.Running ?? false));

    private void OpenPreferredSyncSettings()
    {
        if (!EnsureUnlocked()) return;
        bool hasGit = _gitSync?.Configured == true;
        bool hasCalDav = _sync?.Accounts.ListAccounts().Any(a => a.Enabled) == true;
        if (!hasGit && hasCalDav) OpenSync();
        else OpenSettings(SettingsWindow.SyncTabIndex);
    }

    private WpfMenu BuildSyncButtonMenu()
    {
        var menu = ModernUi.CreateModernContextMenu();
        menu.Items.Add(ModernUi.MenuEntry("지금 동기화", ModernUi.Icons.RepeatPlan, OnHeaderSyncClick));
        menu.Items.Add(ModernUi.MenuDivider());
        menu.Items.Add(ModernUi.MenuEntry("Git 저장소 동기화 설정…", ModernUi.Icons.Cloud, () => OpenSettings(SettingsWindow.SyncTabIndex)));
        menu.Items.Add(ModernUi.MenuEntry("외부 캘린더(CalDAV) 설정…", ModernUi.Icons.Link, OpenSync));
        return menu;
    }

    private static MainWindow.SyncStatusBadgeData BuildGitBadge(DeskCal.Sync.GitSyncReport? report, string? error)
    {
        string stamp = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (error is not null || report is null)
            return new(0, 0, 0, 1, $"{stamp} Git 동기화 실패: {error ?? "알 수 없는 오류"}\n클릭하면 설정을 엽니다.");
        int pulled = report.Received + report.RemovedHere;
        int pushed = report.Sent + report.RemovedThere;
        int warnings = report.Conflicts + report.Skipped;
        return new(pulled, pushed, warnings, 0, $"{stamp} Git 동기화: {report.Summary()}\n클릭하면 설정을 엽니다.");
    }

    private static MainWindow.SyncStatusBadgeData BuildCalDavBadge(string accountName, DeskCal.Sync.SyncReport? report, string? error)
    {
        string stamp = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (error is not null || report is null)
            return new(0, 0, 0, 1, $"{stamp} {accountName} 동기화 실패: {error ?? "알 수 없는 오류"}\n클릭하면 설정을 엽니다.");
        int pulled = report.PulledNew + report.PulledUpdated + report.DeletedLocal;
        int pushed = report.PushedNew + report.PushedUpdated + report.DeletedRemote;
        int warnings = report.Conflicts + report.Deferred;
        int errors = report.Errors.Count;
        return new(pulled, pushed, warnings, errors, $"{stamp} {accountName}: {report.Summary()}\n클릭하면 설정을 엽니다.");
    }

    private async void OnHeaderSyncClick()
    {
        if (!EnsureUnlocked()) return;
        if (_headerSyncActive || (_sync?.Running ?? false) || (_gitSync?.Running ?? false)) return;

        bool hasGit = _gitSync!.Configured;
        var accounts = _sync!.Accounts.ListAccounts().Where(a => a.Enabled).ToList();
        if (!hasGit && accounts.Count == 0)
        {
            OpenPreferredSyncSettings();
            return;
        }

        _headerSyncActive = true;
        UpdateHeaderSyncRunning();
        int pulled = 0, pushed = 0, warnings = 0, errors = 0;
        var details = new List<string>();
        try
        {
            if (hasGit)
            {
                _gitUserVisible = false;
                var (report, error) = await _gitSync.RunAsync();
                if (error is not null || report is null)
                {
                    errors++;
                    details.Add("Git 실패: " + (error ?? "오류"));
                }
                else
                {
                    pulled += report.Received + report.RemovedHere;
                    pushed += report.Sent + report.RemovedThere;
                    warnings += report.Conflicts + report.Skipped;
                    details.Add("Git: " + report.Summary());
                }
            }

            if (accounts.Count > 0)
            {
                _syncUserVisible = false;
                foreach (var a in accounts)
                {
                    var (report, error) = await _sync.RunAsync(a);
                    if (error is not null || report is null)
                    {
                        errors++;
                        details.Add($"{a.Name} 실패: {error ?? "오류"}");
                    }
                    else
                    {
                        pulled += report.PulledNew + report.PulledUpdated + report.DeletedLocal;
                        pushed += report.PushedNew + report.PushedUpdated + report.DeletedRemote;
                        warnings += report.Conflicts + report.Deferred;
                        errors += report.Errors.Count;
                        details.Add($"{a.Name}: {report.Summary()}");
                    }
                }
            }

            string stamp = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            string tooltip = $"{stamp} 동기화 결과\n{string.Join("\n", details)}\n클릭하면 동기화 설정을 엽니다.";
            _board?.SetSyncResult(new MainWindow.SyncStatusBadgeData(pulled, pushed, warnings, errors, tooltip));
        }
        finally
        {
            _headerSyncActive = false;
            UpdateHeaderSyncRunning();
        }
    }

    private static void SyncCheck(SyncCoordinator sync, string[] p)
    {
        var lines = new List<string>();
        try
        {
            var secret = sync.Protector.Protect(p[2]);
            lines.Add($"protected password stored as: {secret[..Math.Min(14, secret.Length)]}… (plain password absent: {!secret.Contains(p[2])})");
            lines.Add("round trip ok: " + (sync.Protector.Unprotect(secret) == p[2]));
            lines.Add("tampered value is refused: " + (sync.Protector.Unprotect(secret[..^4] + "AAAA") is null));
            var account = sync.Accounts.AddAccount(SyncRepository.KindCalDav, "check", p[0], p[1], secret, twoWay: true);
            var (report, error) = Task.Run(() => sync.RunAsync(account)).GetAwaiter().GetResult();   // off the UI thread, or the wait would block the continuation it needs
            lines.Add("error: " + (error ?? "(none)"));
            if (report is not null)
            {
                lines.Add("summary: " + report.Summary());
                lines.AddRange(report.Notes.Select(n => "note: " + n)); lines.AddRange(report.Errors.Select(x => "ERROR: " + x));
            }
            lines.Add("stored result: " + sync.Accounts.GetAccount(account.Id)?.LastResult);
        }
        catch (Exception ex) { lines.Add("EXCEPTION: " + ex); }
        File.WriteAllLines(p[3], lines);
    }

    private void OpenSync() { if (EnsureUnlocked()) SyncWindow.ShowOrActivate(_sync!); }

    private async void SyncNow()
    {
        if (!EnsureUnlocked()) return;
        var accounts = _sync!.Accounts.ListAccounts().Where(a => a.Enabled).ToList();
        if (accounts.Count == 0) { SyncWindow.ShowOrActivate(_sync); return; }
        _syncUserVisible = true;
        foreach (var a in accounts)
        {
            var (report, error) = await _sync.RunAsync(a);
            _tray?.ShowBalloonTip(4000, "외부 캘린더 동기화", error ?? report!.Summary(), error is null ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        }
    }

    private bool _gitUserVisible;

    private async void GitSyncNow()
    {
        if (!EnsureUnlocked()) return;
        if (!_gitSync!.Configured) { OpenSettings(SettingsWindow.SyncTabIndex); return; }
        _gitUserVisible = true;
        var (report, error) = await _gitSync.RunAsync();
        _tray?.ShowBalloonTip(4000, "Git 동기화", error ?? report!.Summary(), error is null ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    private async Task<string> GitSyncFromSettings()
    {
        if (!EnsureUnlocked()) return "잠금을 풀어야 동기화할 수 있습니다.";
        _gitUserVisible = false;       // the settings window shows the outcome itself
        var (report, error) = await _gitSync!.RunAsync();
        return error is null ? "성공 - " + report!.Summary() : "실패 - " + error;
    }

    private void OpenMcp(bool approvals = false)
    {
        if (!EnsureUnlocked()) return;
        McpWindow.ShowOrActivate(_mcpAccess!, _mcpRepo!, _mcpChanges!, AfterImport, approvals);
    }

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
                OpenCalendarChat();
                break;
            case HotkeyAction.ToggleContent:
                if (_board!.ContentHidden) EnsureUnlocked(); else SetLocked(true);
                break;
            case HotkeyAction.QuickInput:
                OpenCalendarChat();
                break;
        }
    }

    /// <summary>Tray tooltip: the name plus the active shortcuts, and a warning for any that could not be registered.</summary>
    private void UpdateTrayText(IReadOnlyList<HotkeyAction> failed)
    {
        if (_tray is null) return;
        string text = "M-DeskCal";
        var shown = new List<string>();
        if (!_prefs.QuickInput.IsEmpty) shown.Add($"{_prefs.QuickInput.Format()} 채팅");
        if (!_prefs.BringToFront.IsEmpty) shown.Add($"{_prefs.BringToFront.Format()} 앞으로");
        if (!_prefs.ToggleVisible.IsEmpty) shown.Add($"{_prefs.ToggleVisible.Format()} 숨김");
        if (shown.Count > 0) text += "\n" + string.Join(" · ", shown.Take(2));
        if (failed.Count > 0) text += $"\n사용 중이라 등록 못 함: {failed.Count}개";
        _tray.Text = text.Length <= 63 ? text : text[..63];   // NotifyIcon.Text is limited to 63 characters
        if (failed.Count > 0)
            _tray.ShowBalloonTip(5000, "M-DeskCal 단축키",
                "다른 프로그램이 이미 사용 중인 단축키가 있습니다: " + string.Join(", ", failed.Select(a => $"{HotkeyManager.ActionName(a)}({_prefs.Get(a).Format()})"))
                + "\n설정 → 고급에서 바꿀 수 있습니다.", Forms.ToolTipIcon.Warning);
    }

    private void ApplyPrefs(AppPreferences next)
    {
        _prefs = next;
        _prefsStore!.Save(_prefs);
        UpdateTrayText(_hotkeys?.Apply(_prefs) ?? []);
    }

    // ---- settings: shared by the tray menu, the gear's quick menu and the settings window ----------

    /// <summary>Apply a change to the board settings and save it; the desktop widgets follow the board's look.</summary>
    private void UpdateSettings(Func<BoardSettings, BoardSettings> change)
    {
        var board = _board!;
        board.ApplySettings(change(board.Settings));
        _boardStore!.Save(board.Settings);
        UpdateCards();
    }

    private void ResetBoardSettings()
    {
        _boardStore!.ResetToDefaults();
        _board!.ApplySettings(_boardStore.Load());
        UpdateCards();
    }

    private void OpenCountdowns() { if (EnsureUnlocked()) CountdownWindow.ShowOrActivate(_countdowns!, CountdownsChanged); }

    private void EditHolidays()
    {
        try
        {
            HolidayOverrideFile.EnsureTemplate(HolidaysPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HolidaysPath) { UseShellExecute = true });
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "휴일 데이터 편집"); }
    }

    /// <summary>Read holidays.json again without asking anything (it was replaced by a sync).</summary>
    private void ApplyHolidaysFile()
    {
        var r = HolidayOverrideFile.Load(HolidaysPath);
        _days = new KoreanDays(r.Overrides);
        _board?.SetCalendar(_days);
        if (r.Errors.Count > 0) AppLog.Write("holidays.json from sync: " + string.Join("; ", r.Errors));
    }

    private void ReloadHolidays()
    {
        var r = HolidayOverrideFile.Load(HolidaysPath);
        _days = new KoreanDays(r.Overrides);
        _board!.SetCalendar(_days);
        System.Windows.MessageBox.Show(
            r.Errors.Count == 0 ? $"사용자 휴일 {r.Overrides.Count}건을 적용했습니다."
                                : $"{r.Overrides.Count}건 적용, 문제 {r.Errors.Count}건:\n\n" + string.Join("\n", r.Errors),
            "M-DeskCal 휴일 데이터");
    }

    private static void OpenDataFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "데이터 폴더"); }
    }

    private void SetEditMode(bool on) { if (EditItem.Checked != on) EditItem.Checked = on; }   // the tray item drives the board and the widgets

    private SettingsHost MakeSettingsHost() => new(
        Get: () => _board!.Settings,
        Update: UpdateSettings,
        ResetDefaults: ResetBoardSettings,
        GetPrefs: () => _prefs,
        SetPrefs: ApplyPrefs,
        GetAutoStart: () => { try { return _startup.IsEnabled(); } catch { return false; } },
        SetAutoStart: on =>
        {
            try { if (on) _startup.Enable(ExePath); else _startup.Disable(); }
            catch (Exception ex) { System.Windows.MessageBox.Show("자동 실행 설정을 바꾸지 못했습니다.\n\n" + ex.Message, "M-DeskCal"); }
        },
        GetTimetableWidget: () => _settingsStore!.GetBool("timetable.widget", false),
        SetTimetableWidget: on => { _settingsStore!.SetBool("timetable.widget", on); UpdateCards(); },
        OpenCountdowns: OpenCountdowns,
        OpenTimetable: OpenTimetable,
        EditHolidays: EditHolidays,
        ReloadHolidays: ReloadHolidays,
        OpenDataFolder: OpenDataFolder,
        ShowDiagnostics: () => System.Windows.MessageBox.Show(_board!.Diagnostics(), "M-DeskCal 진단"),
        Ai: new AiHost(() => _aiStore!.Load(), s => _aiStore!.Save(s), (s, ct) => DeskCal.Ai.Llm.LlmClientFactory.TestAsync(s, ct),
                       () => _accuracy!.Summary(), () => _accuracy!.Reset(),
                       new AiPlusHost(() => _briefStore!.Load(), b => _briefStore!.Save(b), IndexStatus, BuildIndexAsync, () => _embedRepo!.Clear())),
        Git: new GitSyncHost(() => _gitSync!.Config, c => _gitSync!.Config = c, GitSyncFromSettings,
                             () => _gitSync!.Running ? "동기화하는 중… (처음에는 저장소를 받아오느라 시간이 걸립니다)" : _gitSync!.LastResult,
                             () => OpenFolder(_gitSync!.WorkDir),
                             cb => { _gitSync!.StateChanged += cb; return () => _gitSync.StateChanged -= cb; }));

    private static void OpenFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) { System.Windows.MessageBox.Show("아직 동기화하지 않아 폴더가 없습니다.", "M-DeskCal"); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "M-DeskCal"); }
    }

    private void OpenSettings(int tab = 0) => SettingsWindow.ShowOrActivate(MakeSettingsHost(), tab);

    /// <summary>"Type one sentence, get an event": the rules read it at once, the AI (if switched on) only helps with what they cannot.</summary>
    private void OpenQuickInput()
    {
        if (!EnsureUnlocked()) return;
        QuickInputWindow.ShowOrActivate(new QuickInputHost(
            _quickInput!, _batch!, _query!, _accuracy!, () => DateOnly.FromDateTime(DateTime.Today), () => _board!.Settings.FirstDayOfWeek,
            AfterImport, day => _board!.GoTo(day), day => _board!.NewEvent(day)));
    }

    /// <summary>A topic and a day become a row of review to-dos on growing intervals.</summary>
    private void OpenReviewPlan()
    {
        if (!EnsureUnlocked()) return;
        ReviewPlanWindow.ShowOrActivate(MakeReviewPlanHost());
    }

    private ReviewPlanHost MakeReviewPlanHost() => new(
        _mcpChanges!, _batch!, () => DateOnly.FromDateTime(DateTime.Today), IsNonWorkingDay,
        () => _countdowns!.List().Where(c => !c.Lunar && DeskCal.Core.CountdownCalc.TryParseDate(c.Date, out var d) && d >= DateOnly.FromDateTime(DateTime.Today))
            .Select(c => { DeskCal.Core.CountdownCalc.TryParseDate(c.Date, out var d); return (c.Title, d); }).OrderBy(x => x.d).ToList(),
        AfterImport, day => _board!.GoTo(day));

    private void UndoLastAi()
    {
        if (!EnsureUnlocked()) return;
        AiUndo.Run(null, _batch!, AfterImport);
    }

    /// <summary>
    /// The menu under the gear: the ten or so things used every day. Everything else lives in "캘린더 설정…".
    /// Built fresh each time so the check marks always match the current state.
    /// </summary>
    private void ShowQuickMenu()
    {
        var menu = BuildQuickMenu();
        menu.PlacementTarget = _board!.GearAnchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private WpfMenu BuildQuickMenu()
    {
        var board = _board!;
        var s = board.Settings;
        var menu = ModernUi.CreateModernContextMenu();

        WpfMenuItem Entry(string header, string? icon, Action click, bool? check = null, bool danger = false) =>
            ModernUi.MenuEntry(header, icon, click, check, danger);
        WpfMenuItem Toggle(string header, string icon, bool on, Func<BoardSettings, BoardSettings> flip) =>
            Entry(header, icon, () => UpdateSettings(flip), on);

        // Group 1: 일정 · 도구
        menu.Items.Add(ModernUi.MenuGroupHeader(ModernUi.Icons.GroupTools, "일정 · 도구", topDivider: false));
        menu.Items.Add(Entry("새 일정 (오늘)", ModernUi.Icons.CalendarPlus, () => OnHotkey(HotkeyAction.NewEvent)));
        menu.Items.Add(Entry("캘린더 채팅", ModernUi.Icons.Sparkles, () => OpenCalendarChat()));
        menu.Items.Add(Entry("복습 계획 만들기", ModernUi.Icons.RepeatPlan, OpenReviewPlan));
        menu.Items.Add(Entry("미완료 정리", ModernUi.Icons.Rollover, OpenRollover));
        menu.Items.Add(Entry("오늘 브리핑", ModernUi.Icons.Briefing, () => OpenBriefing()));

        // Group 2: 화면 표시
        menu.Items.Add(ModernUi.MenuGroupHeader(ModernUi.Icons.GroupDisplay, "화면 표시"));
        var modeMenu = ModernUi.MenuEntry("표시 모드", ModernUi.Icons.Layout, null);
        foreach (var (mode, name) in new[]
                 {
                     (DisplayMode.FixedMonth, "고정 월"), (DisplayMode.FloatingMonth, "변동 월 (주 단위)"), (DisplayMode.FullMonth, "전체 월 (5줄 고정)"),
                     (DisplayMode.SingleLine, "고정 단일 행"), (DisplayMode.SingleLineFloating, "변동 단일 행 (오늘부터)"),
                 })
            modeMenu.Items.Add(Entry(name, null, () => UpdateSettings(x => x with { Mode = mode }), s.Mode == mode));
        menu.Items.Add(modeMenu);

        menu.Items.Add(Toggle("음력 표시", ModernUi.Icons.Moon, s.ShowLunar, x => x with { ShowLunar = !x.ShowLunar }));
        menu.Items.Add(Toggle("공휴일 표시", ModernUi.Icons.Flag, s.ShowHolidays, x => x with { ShowHolidays = !x.ShowHolidays }));
        menu.Items.Add(Toggle("주 번호 표시", ModernUi.Icons.Hash, s.ShowWeekNumbers, x => x with { ShowWeekNumbers = !x.ShowWeekNumbers }));
        menu.Items.Add(Toggle("완료한 항목 표시", ModernUi.Icons.CheckSquare, s.ShowCompleted, x => x with { ShowCompleted = !x.ShowCompleted }));

        var opacityMenu = ModernUi.MenuEntry("배경 투명도", ModernUi.Icons.Droplet, null);
        foreach (int pct in new[] { 0, 20, 40, 60, 80, 100 })      // transparency: 0% = fully opaque, 100% = fully transparent (BackgroundOpacity is the opposite)
        {
            double opacity = 1 - pct / 100.0;
            opacityMenu.Items.Add(Entry(pct == 0 ? "완전 불투명" : pct == 100 ? "완전 투명" : $"{pct}%", null, () => UpdateSettings(x => x with { BackgroundOpacity = opacity }),
                Math.Abs(s.BackgroundOpacity - opacity) < 0.001));
        }
        menu.Items.Add(opacityMenu);

        // Group 3: 설정
        menu.Items.Add(ModernUi.MenuGroupHeader(ModernUi.Icons.GroupSettings, "설정"));
        menu.Items.Add(Entry("위치/크기 조정", ModernUi.Icons.MoveResize, () => SetEditMode(!EditItem.Checked), EditItem.Checked));
        menu.Items.Add(Entry(board.ContentHidden ? "콘텐츠 잠금 해제" : "콘텐츠 잠그기", ModernUi.Icons.Lock, () => { if (board.ContentHidden) EnsureUnlocked(); else SetLocked(true); }));
        menu.Items.Add(Entry("캘린더 설정", ModernUi.Icons.Gear, () => OpenSettings()));

        menu.Items.Add(ModernUi.MenuDivider());
        menu.Items.Add(Entry("캘린더 종료", ModernUi.Icons.Power, () => Shutdown(), danger: true));
        return menu;
    }

    // ---- tray menu -----------------------------------------------------------

    private Forms.ToolStripMenuItem EditItem = null!;

    private Forms.ContextMenuStrip BuildMenu()
    {
        var board = _board!;
        var menu = new Forms.ContextMenuStrip();
        var refresh = new List<Action>();   // re-sync check marks and dynamic labels each time the menu opens

        // 1. 검색 · 일정 탐색
        menu.Items.Add("캘린더 채팅…", null, (_, _) => OpenCalendarChat());
        menu.Items.Add("새 일정 (오늘)…", null, (_, _) => OnHotkey(HotkeyAction.NewEvent));
        menu.Items.Add("복습 계획 만들기…", null, (_, _) => OpenReviewPlan());
        menu.Items.Add("미완료 정리…", null, (_, _) => OpenRollover());
        menu.Items.Add("오늘 브리핑 · 지난 7일…", null, (_, _) => OpenBriefing());
        var undoAi = new Forms.ToolStripMenuItem("마지막 AI 추가 되돌리기…");
        undoAi.Click += (_, _) => UndoLastAi();
        menu.Items.Add(undoAi);
        refresh.Add(() =>
        {
            DeskCal.Ai.LastBatch? last = null;
            try { last = _batch!.Last(); } catch (Microsoft.Data.Sqlite.SqliteException) { }
            undoAi.Enabled = last is not null;
            undoAi.Text = last is null ? "마지막 AI 추가 되돌리기…" : $"마지막 추가 되돌리기… ({last.Entries.Count}건)";
        });
        menu.Items.Add("오늘로 이동", null, (_, _) => board.GoToday());
        menu.Items.Add(new Forms.ToolStripSeparator());

        // 2. 화면 · 잠금 · 데이터
        EditItem = new Forms.ToolStripMenuItem("위치/크기 조정") { CheckOnClick = true };
        EditItem.CheckedChanged += (_, _) => { board.SetEditMode(EditItem.Checked); _cards?.SetEditMode(EditItem.Checked); _ttWidget?.SetEditMode(EditItem.Checked); };
        menu.Items.Add(EditItem);

        var showItem = new Forms.ToolStripMenuItem();
        showItem.Click += (_, _) => ToggleBoard();
        menu.Items.Add(showItem);
        refresh.Add(() => showItem.Text = board.BoardVisible ? "달력 숨기기" : "달력 보이기");

        var lockMenu = new Forms.ToolStripMenuItem("콘텐츠 잠금");
        var lockToggle = new Forms.ToolStripMenuItem();
        lockToggle.Click += (_, _) => { if (board.ContentHidden) EnsureUnlocked(); else SetLocked(true); };
        var lockSet = new Forms.ToolStripMenuItem();
        lockSet.Click += (_, _) => { if (EnsureUnlocked()) LockUi.SetPassword(_lock!); };
        var lockClear = new Forms.ToolStripMenuItem("비밀번호 해제…");
        lockClear.Click += (_, _) => { if (EnsureUnlocked()) LockUi.RemovePassword(_lock!); };
        lockMenu.DropDownItems.AddRange([lockToggle, lockSet, lockClear]);
        menu.Items.Add(lockMenu);
        refresh.Add(() =>
        {
            lockToggle.Text = board.ContentHidden ? "잠금 해제…" : "지금 잠그기 (내용 숨김)";
            lockSet.Text = _lock!.HasPassword ? "비밀번호 변경…" : "비밀번호 설정…";
            lockClear.Enabled = _lock.HasPassword;
        });

        var exchangeMenu = new Forms.ToolStripMenuItem("가져오기 / 내보내기");
        exchangeMenu.DropDownItems.Add("내보내기… (JSON·CSV·ICS·Excel)", null, (_, _) =>
        {
            if (EnsureUnlocked()) ExchangeUi.Export(_exchange!, d => _days.Holidays.IsDayOff(d), d => _days.Get(d).HolidayName, board.Settings.FirstDayOfWeek);
        });
        exchangeMenu.DropDownItems.Add("가져오기…", null, (_, _) =>
        {
            if (EnsureUnlocked()) ExchangeUi.Import(_exchange!, _db!, Path.Combine(DataDir, "backup"), AfterImport);
        });
        menu.Items.Add(exchangeMenu);
        menu.Items.Add("인쇄… (현재 달)", null, (_, _) => PrintMonth());
        menu.Items.Add(new Forms.ToolStripSeparator());

        // 3. 디데이 · 시간표
        menu.Items.Add("디데이 관리…", null, (_, _) => OpenCountdowns());
        var ttMenu = new Forms.ToolStripMenuItem("시간표");
        ttMenu.DropDownItems.Add("시간표 편집…", null, (_, _) => OpenTimetable());
        var ttWidget = new Forms.ToolStripMenuItem("바탕화면에 표시");
        ttWidget.Click += (_, _) => { _settingsStore!.SetBool("timetable.widget", !_settingsStore.GetBool("timetable.widget", false)); UpdateCards(); };
        ttMenu.DropDownItems.Add(ttWidget);
        menu.Items.Add(ttMenu);
        refresh.Add(() => ttWidget.Checked = _settingsStore!.GetBool("timetable.widget", false));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // 4. 설정 · 연동
        menu.Items.Add("캘린더 설정…", null, (_, _) => OpenSettings());
        var syncMenu = new Forms.ToolStripMenuItem("외부 캘린더 동기화");
        syncMenu.DropDownItems.Add("동기화 설정…", null, (_, _) => OpenSync());
        syncMenu.DropDownItems.Add("지금 동기화", null, (_, _) => SyncNow());
        menu.Items.Add(syncMenu);
        var gitMenu = new Forms.ToolStripMenuItem("Git 저장소 동기화");
        gitMenu.DropDownItems.Add("동기화 설정…", null, (_, _) => OpenSettings(SettingsWindow.SyncTabIndex));
        gitMenu.DropDownItems.Add("지금 동기화", null, (_, _) => GitSyncNow());
        menu.Items.Add(gitMenu);

        var mcpItem = new Forms.ToolStripMenuItem("AI 연동…");
        mcpItem.Click += (_, _) => OpenMcp();
        menu.Items.Add(mcpItem);
        refresh.Add(() =>
        {
            int n = 0;
            try { n = _mcpAccess!.Enabled ? _mcpRepo!.CountWaiting() : 0; } catch (Microsoft.Data.Sqlite.SqliteException) { }
            mcpItem.Text = n > 0 ? $"AI 연동… (승인 대기 {n}건)" : "AI 연동…";
        });
        menu.Items.Add(new Forms.ToolStripSeparator());

        // 5. 정보 · 종료
        menu.Items.Add("제품 정보…", null, (_, _) => OpenSettings(SettingsWindow.AboutTabIndex));
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        menu.Opening += (_, _) => { foreach (var r in refresh) r(); };
        return menu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayTimer?.Stop();
        _syncTimer?.Stop();
        _gitTimer?.Stop();
        _maintTimer?.Stop();
        _mcpTimer?.Stop();
        _hotkeys?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _reminderService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
