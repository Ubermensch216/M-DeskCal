namespace DeskCal.Core;

public static class TextExtensions
{
    /// <summary>The text cut to at most <paramref name="max"/> characters, ending in "…" when something was cut.</summary>
    public static string Ellipsize(this string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
