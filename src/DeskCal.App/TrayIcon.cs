using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DeskCal.App;

/// <summary>Draws the tray icon (a small calendar page) at runtime, so no image asset has to ship with the program.</summary>
internal static class TrayIcon
{
    public static Icon Create(int size = 32)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float s = size / 32f;

            using var page = RoundedRect(new RectangleF(2 * s, 4 * s, 28 * s, 26 * s), 5 * s);
            using (var fill = new SolidBrush(Color.FromArgb(0xF4, 0xF7, 0xFB))) g.FillPath(fill, page);

            using var head = RoundedRect(new RectangleF(2 * s, 4 * s, 28 * s, 9 * s), 5 * s);
            using (var blue = new SolidBrush(Color.FromArgb(0x3B, 0x82, 0xF6)))
            {
                g.FillPath(blue, head);
                g.FillRectangle(blue, 2 * s, 9 * s, 28 * s, 4 * s);   // square off the header's lower corners
            }

            using var ring = new SolidBrush(Color.FromArgb(0x1E, 0x29, 0x3B));
            g.FillRectangle(ring, 9 * s, 1 * s, 3 * s, 6 * s);
            g.FillRectangle(ring, 20 * s, 1 * s, 3 * s, 6 * s);

            using var font = new Font("Segoe UI", 12.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(DateTime.Today.Day.ToString(), font, ring, new RectangleF(2 * s, 13 * s, 28 * s, 17 * s), fmt);
        }

        // Icon.FromHandle does not own the HICON; clone it, then free the original.
        nint hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
