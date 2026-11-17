using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>Loads/saves <see cref="AppPreferences"/> as "app.*" keys in the settings table.</summary>
public sealed class AppPreferencesStore(SettingsStore store)
{
    private const string Prefix = "app.";

    private static string Key(HotkeyAction a) => Prefix + a switch
    {
        HotkeyAction.BringToFront => "hotkey_front",
        HotkeyAction.ToggleVisible => "hotkey_toggle",
        HotkeyAction.NewEvent => "hotkey_new_event",
        HotkeyAction.Search => "hotkey_search",
        HotkeyAction.QuickInput => "hotkey_quick_input",
        _ => "hotkey_content",
    };

    public AppPreferences Load()
    {
        var p = new AppPreferences { BoardVisible = store.GetBool(Prefix + "board_visible", true) };
        if (store.GetString(Prefix + "hotkeys") == "0")
        {
            foreach (var a in Enum.GetValues<HotkeyAction>()) p = p.With(a, Hotkey.None);
            return p;
        }
        foreach (var a in Enum.GetValues<HotkeyAction>())
        {
            string? raw = store.GetString(Key(a));
            if (raw is null) continue;
            if (raw.Length == 0) { p = p.With(a, Hotkey.None); continue; }
            if (Hotkey.TryParse(raw, out var h)) p = p.With(a, h);
        }
        // A hand-edited database could hold two actions on one combination: fall back to the defaults then.
        return p.Validate() is null ? p : new AppPreferences { BoardVisible = p.BoardVisible };
    }

    public void Save(AppPreferences p)
    {
        store.Remove(Prefix + "hotkeys");
        store.SetBool(Prefix + "board_visible", p.BoardVisible);
        foreach (var a in Enum.GetValues<HotkeyAction>())
            store.SetString(Key(a), p.Get(a).IsEmpty ? "" : p.Get(a).Format());
    }
}
