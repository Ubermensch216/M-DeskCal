using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DeskCal.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _board;

    internal static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskCal");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "Local\\M-DeskCal-SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        Directory.CreateDirectory(DataDir);

        _board = new MainWindow();
        _board.Show();
        _board.AttachToDesktop();

        var menu = new Forms.ContextMenuStrip();
        var edit = new Forms.ToolStripMenuItem("편집 모드 (위치/크기 조정)") { CheckOnClick = true };
        edit.CheckedChanged += (_, _) => _board.SetEditMode(edit.Checked);
        menu.Items.Add(edit);
        menu.Items.Add("진단 정보 보기", null, (_, _) =>
            System.Windows.MessageBox.Show(_board.Diagnostics(), "M-DeskCal 진단"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "M-DeskCal",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
