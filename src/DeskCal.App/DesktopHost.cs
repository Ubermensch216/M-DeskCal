using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskCal.App;

/// <summary>
/// Keeps a WPF window "on the desktop": a normal top-level tool window pinned directly above the
/// desktop (Progman) in the Z-order, so every application window is above it, but it is above the
/// wallpaper and desktop icons. The window is never re-parented: re-parenting a WPF per-pixel-alpha
/// (layered) window under Progman/WorkerW leaves it permanently blank (verified on Windows 11 26200),
/// and a child of the desktop cannot receive mouse input anyway.
/// </summary>
internal static class DesktopHost
{
    public static string LastDiagnostics { get; private set; } = "";

    /// <summary>Pin the window just above the desktop. Rect is in physical screen pixels.</summary>
    public static bool Pin(Window window, RectInt screenRect)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = (ex & ~WS_EX_TOPMOST) | WS_EX_TOOLWINDOW; // no taskbar button / Alt+Tab entry
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex);

        bool ok = SetWindowPos(hwnd, ZOrderAboveDesktop(hwnd), screenRect.X, screenRect.Y,
                               screenRect.Width, screenRect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        LastDiagnostics = $"pin ok={ok} progman=0x{FindWindow("Progman", null):X} rect={screenRect}";
        return ok;
    }

    /// <summary>Re-assert the Z-order only (no move/resize); cheap, safe to call from a timer.</summary>
    public static void Repin(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var progman = FindWindow("Progman", null);
        if (progman == 0) return;
        // Already directly above Progman? Nothing to do.
        if (GetWindow(hwnd, GW_HWNDNEXT) == progman) return;
        SetWindowPos(hwnd, ZOrderAboveDesktop(hwnd), 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Edit mode: float above all windows so it can be moved and resized.</summary>
    public static void Float(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    /// <summary>Current window rect in physical screen pixels.</summary>
    public static RectInt GetScreenRect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        GetWindowRect(hwnd, out var r);
        return new RectInt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    // The handle to pass as "insert after" so that we end up directly above Progman.
    private static nint ZOrderAboveDesktop(nint self)
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) return HWND_BOTTOM;
        // Window currently directly above Progman; placing ourselves after it puts us between.
        var above = GetWindow(progman, GW_HWNDPREV);
        while (above == self && above != 0) above = GetWindow(above, GW_HWNDPREV);
        return above == 0 ? HWND_TOP : above;
    }

    // ---- P/Invoke ---------------------------------------------------------

    public readonly record struct RectInt(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    private static readonly nint HWND_TOP = 0, HWND_BOTTOM = 1, HWND_TOPMOST = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(nint hwnd, int index, long value);
}
