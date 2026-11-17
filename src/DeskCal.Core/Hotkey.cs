using System.Text;

namespace DeskCal.Core;

/// <summary>Modifier bits; the values equal Win32 MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN so they can go straight to RegisterHotKey.</summary>
[Flags]
public enum HotkeyMods { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>A global shortcut: modifiers plus a Win32 virtual-key code. Parsed from / formatted as text such as "Ctrl+Alt+D".</summary>
public readonly record struct Hotkey(HotkeyMods Mods, int VirtualKey)
{
    public static Hotkey None => default;
    public bool IsEmpty => Mods == HotkeyMods.None || VirtualKey == 0;
    private static readonly (string Name, int Vk)[] NamedKeys =
    [
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B),
        ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Insert", 0x2D), ("Delete", 0x2E),
    ];

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = 0x0D, ["Escape"] = 0x1B, ["PgUp"] = 0x21, ["PgDn"] = 0x22, ["Ins"] = 0x2D, ["Del"] = 0x2E,
    };

    /// <summary>
    /// Parses "Ctrl+Alt+D", "alt + shift + F5", ... Rules: exactly one non-modifier key (A-Z, 0-9, F1-F24, or a named key),
    /// no repeated modifier, and at least one of Ctrl/Alt/Win (Shift alone would hijack ordinary typing).
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyMods.None;
        int? vk = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;

            HotkeyMods m = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyMods.Ctrl,
                "alt" => HotkeyMods.Alt,
                "shift" => HotkeyMods.Shift,
                "win" or "windows" or "super" => HotkeyMods.Win,
                _ => HotkeyMods.None,
            };
            if (m != HotkeyMods.None)
            {
                if ((mods & m) != 0) return false;
                mods |= m;
                continue;
            }

            if (vk is not null || KeyFromName(part) is not { } code) return false;
            vk = code;
        }

        if (vk is null || (mods & (HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Win)) == 0) return false;
        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }

    /// <summary>Canonical text form: modifiers in the order Ctrl, Alt, Shift, Win, then the key (empty string when unset).</summary>
    public string Format()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        if ((Mods & HotkeyMods.Ctrl) != 0) sb.Append("Ctrl+");
        if ((Mods & HotkeyMods.Alt) != 0) sb.Append("Alt+");
        if ((Mods & HotkeyMods.Shift) != 0) sb.Append("Shift+");
        if ((Mods & HotkeyMods.Win) != 0) sb.Append("Win+");
        return sb.Append(KeyName(VirtualKey)).ToString();
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        foreach (var (name, code) in NamedKeys) if (code == vk) return name;
        return "0x" + vk.ToString("X2");
    }

    private static int? KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24 && name.Length <= 3)
            return 0x6F + f;
        foreach (var (n, code) in NamedKeys) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return code;
        return Aliases.TryGetValue(name, out int a) ? a : null;
    }
}

public enum HotkeyAction { BringToFront, ToggleVisible, NewEvent, Search, ToggleContent, QuickInput }

/// <summary>Application-level (non-appearance) preferences: per-action global shortcuts and whether the board is shown.</summary>
public sealed record AppPreferences
{
    public Hotkey BringToFront { get; init; } = new(HotkeyMods.Ctrl | HotkeyMods.Alt, 'D');
    public Hotkey ToggleVisible { get; init; } = new(HotkeyMods.Ctrl | HotkeyMods.Alt, 'H');
    public Hotkey NewEvent { get; init; } = new(HotkeyMods.Ctrl | HotkeyMods.Alt, 'E');
    public Hotkey Search { get; init; } = new(HotkeyMods.Ctrl | HotkeyMods.Alt, 'F');
    public Hotkey ToggleContent { get; init; } = new(HotkeyMods.Ctrl | HotkeyMods.Alt, 'L');
    public Hotkey QuickInput { get; init; } = new(HotkeyMods.Alt | HotkeyMods.Shift, 'C');

    /// <summary>Whether the board was visible when the app last ran (restored only for auto-start launches).</summary>
    public bool BoardVisible { get; init; } = true;

    public Hotkey Get(HotkeyAction a) => a switch
    {
        HotkeyAction.BringToFront => BringToFront,
        HotkeyAction.ToggleVisible => ToggleVisible,
        HotkeyAction.NewEvent => NewEvent,
        HotkeyAction.Search => Search,
        HotkeyAction.QuickInput => QuickInput,
        _ => ToggleContent,
    };

    public AppPreferences With(HotkeyAction a, Hotkey h) => a switch
    {
        HotkeyAction.BringToFront => this with { BringToFront = h },
        HotkeyAction.ToggleVisible => this with { ToggleVisible = h },
        HotkeyAction.NewEvent => this with { NewEvent = h },
        HotkeyAction.Search => this with { Search = h },
        HotkeyAction.QuickInput => this with { QuickInput = h },
        _ => this with { ToggleContent = h },
    };

    /// <summary>The first problem with the shortcut set (two active actions on one combination), or null when it is fine.</summary>
    public string? Validate()
    {
        var all = Enum.GetValues<HotkeyAction>();
        for (int i = 0; i < all.Length; i++)
        {
            var hk = Get(all[i]);
            if (hk.IsEmpty) continue;
            for (int j = i + 1; j < all.Length; j++)
                if (hk == Get(all[j]))
                    return $"같은 단축키({hk.Format()})를 두 동작에 지정할 수 없습니다.";
        }
        return null;
    }
}
