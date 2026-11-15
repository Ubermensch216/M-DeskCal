using DeskCal.Core;

namespace DeskCal.Tests;

public class WindowBoundsTests
{
    private static readonly PixelRect Primary = new(0, 0, 2560, 1032);
    private static readonly PixelRect SecondaryAbove = new(151, -1080, 1920, 1032);
    private static readonly PixelRect[] Monitors = [SecondaryAbove, Primary];

    [Fact]
    public void KeepsValidPrimaryAndMultiMonitorRects()
    {
        var onSecondary = new PixelRect(616, -1080, 1464, 1047);
        Assert.Equal(onSecondary, WindowBounds.RestoreOrFallback(onSecondary, Monitors, Primary, new(200, 150, 640, 480)));

        var onPrimary = new PixelRect(200, 150, 800, 600);
        Assert.Equal(onPrimary, WindowBounds.RestoreOrFallback(onPrimary, Monitors, Primary, new(200, 150, 640, 480)));
    }

    [Fact]
    public void RecoversFromOffScreenNegativeCoordinates()
    {
        var offscreen = new PixelRect(-20000, -20000, 640, 480);
        var restored = WindowBounds.RestoreOrFallback(offscreen, Monitors, Primary, new(200, 150, 640, 480));
        Assert.Equal(new PixelRect(200, 150, 640, 480), restored);
    }

    [Fact]
    public void RecoversWhenSecondaryMonitorIsDisconnected()
    {
        var previouslyOnSecondary = new PixelRect(616, -1080, 1464, 1047);
        var restored = WindowBounds.RestoreOrFallback(previouslyOnSecondary, [Primary], Primary, new(200, 150, 640, 480));
        Assert.True(WindowBounds.IsVisibleOnAny(restored, [Primary]));
        Assert.Equal(new PixelRect(200, 0, 1464, 1032), restored);
    }

    [Fact]
    public void RestoresOrFallsBackWidgetPoints()
    {
        Assert.Equal((880, 150), WindowBounds.RestorePointOrFallback(880, 150, Monitors, Primary, 880, 150));
        Assert.Equal((880, 150), WindowBounds.RestorePointOrFallback(-20000, -20000, Monitors, Primary, 880, 150));
    }
}
