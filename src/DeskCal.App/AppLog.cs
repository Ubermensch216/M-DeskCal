using System.IO;

namespace DeskCal.App;

/// <summary>The diagnostic log, <c>%AppData%\DeskCal\m0.log</c>. Writing never throws.</summary>
internal static class AppLog
{
    public static void Write(string text)
    {
        try { File.AppendAllText(Path.Combine(App.DataDir, "m0.log"), $"[{DateTime.Now:s}] {text}\n"); } catch { }
    }
}
