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
        _ => "hotkey_new_event",
    };

    public AppPreferences Load()
    {
        var p = new AppPreferences { HotkeysEnabled = store.GetBool(Prefix + "hotkeys", true), BoardVisible = store.GetBool(Prefix + "board_visible", true) };
        foreach (var a in Enum.GetValues<HotkeyAction>())
            if (Hotkey.TryParse(store.GetString(Key(a)), out var h)) p = p.With(a, h);
        // A hand-edited database could hold two actions on one combination: fall back to the defaults then.
        return p.Validate() is null ? p : new AppPreferences { HotkeysEnabled = p.HotkeysEnabled, BoardVisible = p.BoardVisible };
    }

    public void Save(AppPreferences p)
    {
        store.SetBool(Prefix + "hotkeys", p.HotkeysEnabled);
        store.SetBool(Prefix + "board_visible", p.BoardVisible);
        foreach (var a in Enum.GetValues<HotkeyAction>()) store.SetString(Key(a), p.Get(a).Format());
    }
}
