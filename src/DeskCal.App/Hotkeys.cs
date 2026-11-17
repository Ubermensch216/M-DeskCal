using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DeskCal.Core;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
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

        int id = 1;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var hk = prefs.Get(action);
            if (hk.IsEmpty) continue;
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
        HotkeyAction.NewEvent => "오늘 일정 추가",
        HotkeyAction.Search => "검색 (캘린더 채팅)",
        HotkeyAction.QuickInput => "캘린더 채팅 열기",
        _ => "콘텐츠 잠금/해제",
    };
}

/// <summary>
/// Click-to-capture hotkey box modeled on deskPet_Bugi's ChatHotkey:
/// click to clear, press modifiers one by one or together (at least 2 modifiers + 1 key = 3 keys total),
/// Backspace removes the last modifier, Esc cancels.
/// </summary>
internal sealed class HotkeyCaptureBox : TextBox
{
    private Hotkey _value;
    private readonly List<HotkeyMods> _order = [];
    private HotkeyMods _pending;
    private bool _capturing;

    public Func<Hotkey, string?>? ValidateConflict { get; set; }
    public event Action<Hotkey>? HotkeyChanged;
    public event Action<string?>? Hint;

    public HotkeyCaptureBox(Hotkey initial)
    {
        _value = initial;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = System.Windows.Input.Cursors.Hand;
        Width = 158;
        MinHeight = 28;
        FontSize = 12;
        TextAlignment = TextAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Padding = new Thickness(8, 4, 8, 4);
        SetValue(initial);
    }

    public void SetValue(Hotkey hk)
    {
        _value = hk;
        _capturing = false;
        _pending = HotkeyMods.None;
        _order.Clear();
        if (hk.IsEmpty)
        {
            Foreground = ModernUi.MutedBrush;
            FontWeight = FontWeights.Normal;
            Text = "꺼짐 (클릭해 설정)";
        }
        else
        {
            Foreground = ModernUi.InkBrush;
            FontWeight = FontWeights.SemiBold;
            Text = hk.Format().Replace("+", " + ");
        }
    }

    private void Begin()
    {
        if (_capturing) return;
        _capturing = true;
        _pending = HotkeyMods.None;
        _order.Clear();
        ShowPending();
        Hint?.Invoke("키를 하나씩 누르세요. 보조키(Ctrl · Alt · Shift) 2개 이상 + 글쇠 1개 (Esc 취소 · Backspace 방금 키 취소 · Delete 끄기)");
    }

    private void Cancel()
    {
        if (!_capturing) return;
        SetValue(_value);
        Hint?.Invoke(null);
    }

    private void ShowPending()
    {
        Foreground = ModernUi.AccentBrush;
        FontWeight = FontWeights.SemiBold;
        if (_pending == HotkeyMods.None) { Text = "키를 누르세요…"; return; }
        var parts = new List<string>();
        if (_pending.HasFlag(HotkeyMods.Ctrl)) parts.Add("Ctrl");
        if (_pending.HasFlag(HotkeyMods.Alt)) parts.Add("Alt");
        if (_pending.HasFlag(HotkeyMods.Shift)) parts.Add("Shift");
        if (_pending.HasFlag(HotkeyMods.Win)) parts.Add("Win");
        Text = string.Join(" + ", parts) + " + ";
    }

    private static int ModCount(HotkeyMods m) =>
        (m.HasFlag(HotkeyMods.Ctrl) ? 1 : 0) + (m.HasFlag(HotkeyMods.Alt) ? 1 : 0)
        + (m.HasFlag(HotkeyMods.Shift) ? 1 : 0) + (m.HasFlag(HotkeyMods.Win) ? 1 : 0);

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        Focus();
        Begin();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Mouse.LeftButton != MouseButtonState.Pressed) Begin();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { Cancel(); return; }
        if (key == Key.Delete && _pending == HotkeyMods.None && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetValue(Hotkey.None);
            Hint?.Invoke(null);
            HotkeyChanged?.Invoke(Hotkey.None);
            return;
        }
        if (key == Key.Back)
        {
            if (_order.Count > 0)
            {
                var last = _order[^1];
                _order.RemoveAt(_order.Count - 1);
                _pending &= ~last;
                ShowPending();
            }
            return;
        }

        HotkeyMods mod = key switch
        {
            Key.LeftCtrl or Key.RightCtrl => HotkeyMods.Ctrl,
            Key.LeftAlt or Key.RightAlt => HotkeyMods.Alt,
            Key.LeftShift or Key.RightShift => HotkeyMods.Shift,
            Key.LWin or Key.RWin => HotkeyMods.Win,
            _ => HotkeyMods.None,
        };
        if (mod != HotkeyMods.None)
        {
            if (!_pending.HasFlag(mod)) { _pending |= mod; _order.Add(mod); ShowPending(); }
            return;
        }

        // Combine incrementally pressed modifiers with currently held modifiers
        var mods = _pending;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control)) mods |= HotkeyMods.Ctrl;
        if (held.HasFlag(ModifierKeys.Alt)) mods |= HotkeyMods.Alt;
        if (held.HasFlag(ModifierKeys.Shift)) mods |= HotkeyMods.Shift;
        if (held.HasFlag(ModifierKeys.Windows)) mods |= HotkeyMods.Win;

        if (key is Key.Tab or Key.None)
        {
            Hint?.Invoke("이 키는 단축키로 쓸 수 없습니다.");
            return;
        }
        if (ModCount(mods) < 2)
        {
            Hint?.Invoke("키를 3개 이상 눌러야 합니다 (보조키 2개 이상 + 글쇠 1개, 예: Ctrl + Alt + Q)");
            return;
        }

        var candidate = new Hotkey(mods, KeyInterop.VirtualKeyFromKey(key));
        if (!Hotkey.TryParse(candidate.Format(), out var parsed))
        {
            Hint?.Invoke($"'{candidate.Format()}' 은(는) 사용할 수 없는 조합입니다.");
            return;
        }
        if (ValidateConflict?.Invoke(parsed) is { } conflict)
        {
            Hint?.Invoke(conflict);
            return;
        }

        SetValue(parsed);
        Hint?.Invoke(null);
        HotkeyChanged?.Invoke(parsed);
    }
}
