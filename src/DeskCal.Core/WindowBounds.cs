namespace DeskCal.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Validates saved window positions against the currently attached monitors.</summary>
public static class WindowBounds
{
    /// <summary>True when at least <paramref name="minVisible"/> pixels of the rectangle intersect one of <paramref name="screens"/>.</summary>
    public static bool IsVisibleOnAny(PixelRect rect, IReadOnlyList<PixelRect> screens, int minVisible = 40)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || screens.Count == 0) return false;
        foreach (var s in screens)
        {
            int ix = Math.Min(rect.X + rect.Width, s.X + s.Width) - Math.Max(rect.X, s.X);
            int iy = Math.Min(rect.Y + rect.Height, s.Y + s.Height) - Math.Max(rect.Y, s.Y);
            if (ix >= minVisible && iy >= minVisible) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <paramref name="saved"/> when it is visible on one of <paramref name="screens"/>; otherwise falls back to
    /// <paramref name="fallback"/> clamped inside <paramref name="primary"/>.
    /// </summary>
    public static PixelRect RestoreOrFallback(PixelRect saved, IReadOnlyList<PixelRect> screens, PixelRect primary, PixelRect fallback)
    {
        if (IsVisibleOnAny(saved, screens)) return saved;
        int w = Math.Min(saved.Width >= 320 ? saved.Width : fallback.Width, Math.Max(1, primary.Width));
        int h = Math.Min(saved.Height >= 240 ? saved.Height : fallback.Height, Math.Max(1, primary.Height));
        int x = Math.Clamp(fallback.X, primary.X, primary.X + Math.Max(0, primary.Width - w));
        int y = Math.Clamp(fallback.Y, primary.Y, primary.Y + Math.Max(0, primary.Height - h));
        return new(x, y, w, h);
    }

    /// <summary>Restores a top-left widget position when it is visible on any screen; otherwise falls back inside <paramref name="primary"/>.</summary>
    public static (int X, int Y) RestorePointOrFallback(int x, int y, IReadOnlyList<PixelRect> screens, PixelRect primary, int fallbackX, int fallbackY)
    {
        if (IsVisibleOnAny(new(x, y, 160, 100), screens)) return (x, y);
        return (Math.Clamp(fallbackX, primary.X, primary.X + Math.Max(0, primary.Width - 160)),
                Math.Clamp(fallbackY, primary.Y, primary.Y + Math.Max(0, primary.Height - 100)));
    }
}
