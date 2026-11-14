using System.Windows.Media;

namespace DeskCal.App;

/// <summary>Turns the "#RRGGBB" strings kept in the database and settings into WPF colors and brushes.</summary>
internal static class UiColors
{
    /// <summary>The color, or null when the text is empty or not a color.</summary>
    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return null; }
    }

    public static Color Parse(string? hex, Color fallback) => TryParse(hex) ?? fallback;

    public static SolidColorBrush? TryBrush(string? hex) => TryParse(hex) is { } c ? new SolidColorBrush(c) : null;

    /// <summary>A frozen (shareable, cheaper to draw) brush.</summary>
    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
