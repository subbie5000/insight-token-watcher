using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher;

/// <summary>
/// Tray icon, legible at 16 px:
///  • all fine → brand gauge ring (lime arc),
///  • something tight/critical → solid disc in amber/red with the number of affected accounts,
///  • no data yet → grey ring.
/// (A segmented ring per account was tried and read as a cog wheel at 16 px.)
/// </summary>
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Bitmap Render(int crit, int warn, bool anyData, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        if (crit > 0 || warn > 0)
        {
            var color = crit > 0 ? Theme.Crit : Theme.Warn;
            var count = crit > 0 ? crit : warn;
            var disc = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using (var b = new SolidBrush(color))
                g.FillEllipse(b, disc);
            var text = count > 9 ? "!" : count.ToString();
            var fontPx = size * 0.80f;
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(crit > 0 ? Color.White : Color.FromArgb(28, 22, 6));
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
            };
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // Segoe UI sits slightly high in its line box; nudge down for optical centering
            g.DrawString(text, font, fg, new RectangleF(0, size * 0.06f, size, size), fmt);
            return bmp;
        }

        var stroke = Math.Max(2f, size * 0.19f);
        var inset = stroke / 2f + 0.5f;
        var ring = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var bg = new SolidBrush(Color.FromArgb(235, 20, 22, 27)))
            g.FillEllipse(bg, RectangleF.Inflate(ring, stroke / 2f, stroke / 2f));
        using (var track = new Pen(Color.FromArgb(70, 76, 88), stroke))
            g.DrawEllipse(track, ring);
        using var arc = new Pen(anyData ? Theme.Ok : Color.FromArgb(140, 146, 158), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawArc(arc, ring, -90, anyData ? 250 : 90);
        return bmp;
    }

    /// <summary>Creates an icon; call <see cref="Destroy"/> on the handle once it has been replaced.</summary>
    public static Icon Create(int crit, int warn, bool anyData, int size, out IntPtr handle)
    {
        using var bmp = Render(crit, warn, anyData, size);
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero) DestroyIcon(handle);
    }
}
