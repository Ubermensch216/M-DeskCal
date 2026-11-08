using System.Runtime.Versioning;
using DeskCal.Core;
using DeskCal.Data;
using Microsoft.Win32;

namespace DeskCal.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+D", HotkeyMods.Ctrl | HotkeyMods.Alt, 'D')]
    [InlineData("alt + shift + f5", HotkeyMods.Alt | HotkeyMods.Shift, 0x74)]
    [InlineData("Win+Space", HotkeyMods.Win, 0x20)]
    [InlineData("control+7", HotkeyMods.Ctrl, '7')]
    [InlineData("Ctrl+Del", HotkeyMods.Ctrl, 0x2E)]
    [InlineData("Ctrl+PgDn", HotkeyMods.Ctrl, 0x22)]
    [InlineData("Ctrl+F24", HotkeyMods.Ctrl, 0x87)]
    public void Parses_valid_shortcuts(string text, HotkeyMods mods, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(new Hotkey(mods, vk), h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]               // no modifier
    [InlineData("Shift+D")]         // Shift alone would hijack typing
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]        // no key
    [InlineData("Ctrl+A+B")]        // two keys
    [InlineData("Ctrl+Ctrl+A")]     // repeated modifier
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl++A")]
    public void Rejects_invalid_shortcuts(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("alt+ctrl+d", "Ctrl+Alt+D")]
    [InlineData("Win+Shift+Ctrl+Alt+f12", "Ctrl+Alt+Shift+Win+F12")]
    [InlineData("ctrl+escape", "Ctrl+Esc")]
    [InlineData("ctrl+return", "Ctrl+Enter")]
    public void Format_is_canonical_and_round_trips(string text, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(canonical, h.Format());
        Assert.True(Hotkey.TryParse(h.Format(), out var again));
        Assert.Equal(h, again);
    }

    [Fact]
    public void Modifier_bits_match_the_win32_values_RegisterHotKey_expects() =>
        Assert.Equal([1, 2, 4, 8], new[] { (int)HotkeyMods.Alt, (int)HotkeyMods.Ctrl, (int)HotkeyMods.Shift, (int)HotkeyMods.Win });

    [Fact]
    public void Defaults_are_distinct_and_valid()
    {
        var p = new AppPreferences();
        Assert.Null(p.Validate());
        Assert.True(p.HotkeysEnabled);
        Assert.True(p.BoardVisible);
        foreach (var a in Enum.GetValues<HotkeyAction>()) Assert.True(Hotkey.TryParse(p.Get(a).Format(), out _));
    }

    [Fact]
    public void Duplicate_combinations_are_reported()
    {
        var p = new AppPreferences().With(HotkeyAction.NewEvent, new AppPreferences().BringToFront);
        Assert.NotNull(p.Validate());
    }
}

public class AppPreferencesStoreTests : IDisposable
{
    private readonly DbFixture _db = new();
    private readonly SettingsStore _settings;
    private readonly AppPreferencesStore _store;

    public AppPreferencesStoreTests()
    {
        _settings = new SettingsStore(_db.Db);
        _store = new AppPreferencesStore(_settings);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Round_trips_every_field()
    {
        var p = new AppPreferences { HotkeysEnabled = false, BoardVisible = false }
            .With(HotkeyAction.BringToFront, new Hotkey(HotkeyMods.Win | HotkeyMods.Shift, 'K'))
            .With(HotkeyAction.NewEvent, new Hotkey(HotkeyMods.Ctrl, 0x71));
        _store.Save(p);
        Assert.Equal(p, _store.Load());
    }

    [Fact]
    public void Garbage_and_conflicting_values_fall_back_to_defaults()
    {
        _settings.SetString("app.hotkey_front", "banana");
        _settings.SetString("app.hotkey_toggle", "Ctrl+Alt+E");   // same as the default of NewEvent
        _settings.SetString("app.hotkey_new_event", "Ctrl+Alt+E");
        var loaded = _store.Load();
        Assert.Null(loaded.Validate());
        Assert.Equal(new AppPreferences().ToggleVisible, loaded.ToggleVisible);
    }
}

[SupportedOSPlatform("windows")]
public class StartupRegistrationTests : IDisposable
{
    // A private key under HKCU so the real Run key is never touched.
    private const string Key = @"Software\M-DeskCal-Tests\Run";
    private readonly StartupRegistration _reg = new("M-DeskCal-Test", Key);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\M-DeskCal-Tests", throwOnMissingSubKey: false);

    [Fact]
    public void Command_quotes_the_path_and_carries_the_autostart_flag() =>
        Assert.Equal("\"C:\\Program Files\\M DeskCal\\DeskCal.App.exe\" --autostart",
            StartupRegistration.BuildCommand(@"C:\Program Files\M DeskCal\DeskCal.App.exe"));

    [Fact]
    public void Enable_disable_cycle()
    {
        Assert.False(_reg.IsEnabled());
        _reg.Enable(@"C:\a b\x.exe");
        Assert.True(_reg.IsEnabled());
        Assert.False(_reg.IsStale(@"C:\a b\x.exe"));
        _reg.Disable();
        Assert.False(_reg.IsEnabled());
        _reg.Disable();   // disabling twice is harmless
    }

    [Fact]
    public void Entry_for_another_location_is_stale_but_a_missing_entry_is_not()
    {
        Assert.False(_reg.IsStale(@"C:\x.exe"));
        _reg.Enable(@"C:\old\x.exe");
        Assert.True(_reg.IsStale(@"C:\new\x.exe"));
        _reg.Enable(@"C:\new\x.exe");
        Assert.False(_reg.IsStale(@"C:\new\x.exe"));
    }
}
