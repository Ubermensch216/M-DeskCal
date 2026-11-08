using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace DeskCal.App;

/// <summary>
/// Attaches a WPF window to the desktop layer (behind icons, above wallpaper) and
/// detaches it again for "edit mode". Supports the classic Progman/WorkerW layout
/// and the Windows 11 24H2+ layout where WorkerW is a child of Progman.
/// </summary>
internal static class DesktopHost
{
    public enum Layout { None, SiblingWorkerW, ProgmanChild }

    public static string LastDiagnostics { get; private set; } = "";

    // ---- public API -------------------------------------------------------

    /// <summary>Attach window to the desktop. Rect is in physical screen pixels.</summary>
    public static bool Attach(Window window, RectInt screenRect)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var log = new StringBuilder();

        var progman = FindWindow("Progman", null);
        log.AppendLine($"Progman=0x{progman:X}");
        if (progman == 0) { LastDiagnostics = log.ToString(); return false; }

        // Ask Progman to spawn a WorkerW behind the icons.
        SendMessageTimeout(progman, 0x052C, 0xD, 0x1, 0, 1000, out _);

        var (parent, insertAfter, layout) = FindParent(progman, log);
        log.AppendLine($"layout={layout} parent=0x{parent:X} insertAfter=0x{insertAfter:X}");
        if (parent == 0 || layout == Layout.None) { LastDiagnostics = log.ToString(); return false; }

        // WS_POPUP -> WS_CHILD
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        style = (style & ~WS_POPUP) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, style);

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = (ex & ~WS_EX_TOPMOST) | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex);

        SetParent(hwnd, parent);

        // Convert screen rect -> parent client coordinates.
        var pt = new POINT { X = screenRect.X, Y = screenRect.Y };
        ScreenToClient(parent, ref pt);
        bool ok = SetWindowPos(hwnd, insertAfter, pt.X, pt.Y, screenRect.Width, screenRect.Height,
                               SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        log.AppendLine($"SetWindowPos ok={ok} client=({pt.X},{pt.Y}) size={screenRect.Width}x{screenRect.Height}");
        LastDiagnostics = log.ToString();
        return ok;
    }

    /// <summary>Detach window from the desktop so it behaves like a normal top-level window.</summary>
    public static void Detach(Window window, RectInt screenRect)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetParent(hwnd, 0);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        style = (style & ~WS_CHILD) | WS_POPUP | WS_VISIBLE;
        SetWindowLongPtr(hwnd, GWL_STYLE, style);
        SetWindowPos(hwnd, HWND_TOPMOST, screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
                     SWP_SHOWWINDOW | SWP_FRAMECHANGED);
    }

    /// <summary>Current window rect in physical screen pixels.</summary>
    public static RectInt GetScreenRect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        GetWindowRect(hwnd, out var r);
        return new RectInt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    // ---- window discovery -------------------------------------------------

    private static (nint parent, nint insertAfter, Layout layout) FindParent(nint progman, StringBuilder log)
    {
        // Layout 1 (Win7..Win11 23H2): top-level WorkerW that owns SHELLDLL_DefView,
        // and the *next sibling* WorkerW is the wallpaper layer behind the icons.
        nint siblingWorkerW = 0;
        EnumWindows((top, _) =>
        {
            var defView = FindWindowEx(top, 0, "SHELLDLL_DefView", null);
            if (defView != 0)
            {
                siblingWorkerW = FindWindowEx(0, top, "WorkerW", null);
                log.AppendLine($"DefView owner=0x{top:X} next WorkerW=0x{siblingWorkerW:X}");
                return false;
            }
            return true;
        }, 0);
        if (siblingWorkerW != 0)
            return (siblingWorkerW, HWND_TOP, Layout.SiblingWorkerW);

        // Layout 2 (Win11 24H2+): SHELLDLL_DefView and WorkerW are children of Progman.
        var defViewChild = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        log.AppendLine($"Progman child DefView=0x{defViewChild:X}");
        if (defViewChild != 0)
            return (progman, defViewChild, Layout.ProgmanChild); // z-order: just behind the icon layer

        return (0, 0, Layout.None);
    }

    // ---- P/Invoke ---------------------------------------------------------

    public readonly record struct RectInt(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const long WS_POPUP = 0x80000000L, WS_CHILD = 0x40000000L, WS_VISIBLE = 0x10000000L;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20;
    private static readonly nint HWND_TOP = 0, HWND_TOPMOST = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetParent(nint child, nint newParent);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(nint hwnd, int index, long value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
