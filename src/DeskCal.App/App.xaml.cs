using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using DeskCal.Core;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "Local\\M-DeskCal-SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        Directory.CreateDirectory(DataDir);

        // --db=<path> (dev aid) uses another database file, e.g. a scratch one for snapshots.
        var dbArg = e.Args.FirstOrDefault(a => a.StartsWith("--db="));
        var db = new DeskCalDb(dbArg is null ? Path.Combine(DataDir, "deskcal.db") : dbArg["--db=".Length..]);
        db.Migrate();
        var settings = new SettingsStore(db);
        _boardStore = new BoardSettingsStore(settings);
        var items = new ItemRepository(db, new SystemClock());

        _board = new MainWindow();
        _board.SetItems(items);
        _board.ApplySettings(_boardStore.Load());

        // Restore the saved board rectangle (physical pixels), ignoring implausible values.
        int w = settings.GetInt("board.w", 640), h = settings.GetInt("board.h", 480);
        if (w >= 320 && h >= 240)
            _board.SetRect(new(settings.GetInt("board.x", 200), settings.GetInt("board.y", 150), w, h));
        _board.RectCommitted += r =>
        {
            settings.SetInt("board.x", r.X); settings.SetInt("board.y", r.Y);
            settings.SetInt("board.w", r.Width); settings.SetInt("board.h", r.Height);
        };

        var posArg = e.Args.FirstOrDefault(a => a.StartsWith("--pos="));
        if (posArg is not null && posArg[6..].Split(',') is [var px, var py] &&
            int.TryParse(px, out int x) && int.TryParse(py, out int y))
            _board.SetPosition(x, y); // dev aid: physical screen pixels

        var snapArg = e.Args.FirstOrDefault(a => a.StartsWith("--snapshot="));
        if (snapArg is not null)
        {
            // Dev aid: render every display mode to PNGs offscreen, then exit. Never touches settings/DB.
            var dir = snapArg["--snapshot=".Length..];
            Directory.CreateDirectory(dir);
            _board.Left = -20000; _board.Top = -20000;
            _board.Show();

            if (e.Args.Contains("--seed"))
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                string D(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");
                items.ReplaceDay(D(0), ["기획서 검토", "점심 약속 12:00", "운동", "장보기", "책 반납"]);
                items.ReplaceDay(D(2), ["치과 예약", "세금 신고"]);
                items.ReplaceDay(D(-3), ["보고서 제출", "팀 회식"]);
                items.ReplaceDay(D(5), ["월간 회의 준비"]);
                var day2 = items.GetByDay(D(2));
                items.SetDone(day2[1].Id, true);
                items.SetTextColor(day2[0].Id, "#FFE066");
                items.SetColor(items.GetByDay(D(0))[0].Id, "#74C0FC");
                items.SetDone(items.GetByDay(D(-3))[0].Id, true);
            }

            foreach (var mode in Enum.GetValues<DisplayMode>())
            {
                _board.ApplySettings(new BoardSettings { Mode = mode, ShowWeekNumbers = true, FirstDayOfWeek = DayOfWeek.Monday, MaxRows = 5 });
                _board.SaveSnapshot(Path.Combine(dir, $"{(int)mode}-{mode}.png"));
            }
            _board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth, Bullet = BulletStyle.Number });
            _board.SaveSnapshot(Path.Combine(dir, "m3-numbered.png"));
            _board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth, Bullet = BulletStyle.None, ShowCompleted = false });
            _board.SaveSnapshot(Path.Combine(dir, "m3-hide-completed.png"));
            _board.ApplySettings(new BoardSettings { Mode = DisplayMode.FixedMonth });
            _board.OpenEditor(DateOnly.FromDateTime(DateTime.Today));
            _board.SaveSnapshot(Path.Combine(dir, "m3-editor.png"));

            // Self-test of the editor wiring: type new text, commit, and check what reached the database.
            if (e.Args.Contains("--seed"))
            {
                var todayKey = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
                var before = items.GetByDay(todayKey);
                _board.EditorText = "기획서 검토\r\n새 항목\r\n\r\n운동   ";
                _board.CommitEditor();
                var after = items.GetByDay(todayKey);
                bool ok = after.Select(i => i.Content).SequenceEqual(["기획서 검토", "새 항목", "운동"])
                          && after[0].Id == before[0].Id && after[0].BgColor == "#74C0FC"      // kept line keeps identity + colour
                          && after[2].Id == before[2].Id                                         // "운동" kept although lines were removed below
                          && after[1].Id == before[1].Id;                                        // edited line stays the same item
                File.WriteAllText(Path.Combine(dir, "selftest.txt"),
                    $"{(ok ? "PASS" : "FAIL")}: {string.Join(" | ", after.Select(i => i.Content))}  deleted-others={items.GetByDay(todayKey).Count}");
                _board.SaveSnapshot(Path.Combine(dir, "m3-after-edit.png"));
            }
            Shutdown();
            return;
        }

        _board.Show();
        if (!e.Args.Contains("--plain")) _board.AttachToDesktop(); // --plain: stay a normal window (debug)

        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "M-DeskCal",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        if (e.Args.Contains("--edit")) EditItem.Checked = true; // dev aid: start in edit mode
    }

    // ---- tray menu -----------------------------------------------------------

    private Forms.ToolStripMenuItem EditItem = null!;

    private Forms.ContextMenuStrip BuildMenu()
    {
        var board = _board!;
        var menu = new Forms.ContextMenuStrip();
        var refresh = new List<Action>();   // re-sync check marks each time the menu opens

        EditItem = new Forms.ToolStripMenuItem("편집 모드 (위치/크기 조정)") { CheckOnClick = true };
        EditItem.CheckedChanged += (_, _) => board.SetEditMode(EditItem.Checked);
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
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
