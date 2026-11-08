using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeskCal.Data;

/// <summary>
/// "Start with Windows": one value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Per-user, needs no elevation,
/// and is exactly what Settings > Apps > Startup lists, so the user can also turn it off there.
/// The registry path is injectable so tests never touch the real Run key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration(string valueName = "M-DeskCal",
    string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    /// <summary>Flag put on the command line so the app knows it was started by Windows (restores the last visibility).</summary>
    public const string AutoStartFlag = "--autostart";

    /// <summary>"C:\path\app.exe" --autostart — the path is quoted because it may contain spaces.</summary>
    public static string BuildCommand(string exePath) => $"\"{exePath}\" {AutoStartFlag}";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string s && s.Length > 0;
    }

    /// <summary>True when enabled but pointing at another exe (the app was moved), so the entry should be refreshed.</summary>
    public bool IsStale(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string s && s.Length > 0 && !string.Equals(s, BuildCommand(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, BuildCommand(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
