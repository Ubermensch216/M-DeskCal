using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DeskCal.Core;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskCal.App;

/// <summary>Registers the global shortcuts on a hidden message-only window and raises <see cref="Pressed"/> for each hit.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly nint HWND_MESSAGE = -3;

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];

    public event Action<HotkeyAction>? Pressed;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("M-DeskCal hotkeys") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(Hook);
    }

    /// <summary>Replace the registrations. Returns the actions whose combination Windows refused (usually: another program owns it).</summary>
    public IReadOnlyList<HotkeyAction> Apply(AppPreferences prefs)
    {
        UnregisterAll();
        var failed = new List<HotkeyAction>();
        if (!prefs.HotkeysEnabled) return failed;

        int id = 1;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var hk = prefs.Get(action);
            if (RegisterHotKey(_source.Handle, id, (uint)hk.Mods | MOD_NOREPEAT, (uint)hk.VirtualKey)) _registered[id] = action;
            else failed.Add(action);
            id++;
        }
        return failed;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);

    public static string ActionName(HotkeyAction a) => a switch
    {
        HotkeyAction.BringToFront => "달력 앞으로 불러오기",
        HotkeyAction.ToggleVisible => "달력 숨기기/보이기",
        _ => "오늘 일정 추가",
    };
}

/// <summary>Dialog for choosing the three shortcuts: click a box and press the combination.</summary>
internal static class HotkeyDialog
{
    /// <summary>Returns the edited preferences, or null when cancelled.</summary>
    public static AppPreferences? Edit(AppPreferences current)
    {
        var prefs = current;
        AppPreferences? result = null;
        var win = new Window
        {
            Title = "M-DeskCal 단축키", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic"), FontSize = 13,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "칸을 클릭한 뒤 원하는 조합을 누르세요. Ctrl·Alt·Win 중 하나는 반드시 포함해야 합니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        var status = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 18 };

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var a = action;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var reset = new Button { Content = "기본값", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(reset, Dock.Right);
            var label = new TextBlock { Text = HotkeyManager.ActionName(a), Width = 150, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            var box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Text = prefs.Get(a).Format(), Padding = new Thickness(6, 3, 6, 3), Cursor = System.Windows.Input.Cursors.Hand };

            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;

                var mods = HotkeyMods.None;
                var m = Keyboard.Modifiers;
                if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyMods.Ctrl;
                if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyMods.Alt;
                if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyMods.Shift;
                if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyMods.Win;

                var hk = new Hotkey(mods, KeyInterop.VirtualKeyFromKey(key));
                if (!Hotkey.TryParse(hk.Format(), out hk)) { status.Text = $"'{hk.Format()}' 은(는) 사용할 수 없는 조합입니다."; return; }
                prefs = prefs.With(a, hk);
                box.Text = hk.Format();
                status.Text = "";
            };
            reset.Click += (_, _) =>
            {
                prefs = prefs.With(a, new AppPreferences().Get(a));
                box.Text = prefs.Get(a).Format();
                status.Text = "";
            };
            row.Children.Add(reset); row.Children.Add(label); row.Children.Add(box);
            panel.Children.Add(row);
        }
        panel.Children.Add(status);

        var ok = new Button { Content = "저장", Padding = new Thickness(16, 5, 16, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (prefs.Validate() is { } problem) { status.Text = problem; return; }
            result = prefs; win.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.ShowDialog();
        return result;
    }
}
