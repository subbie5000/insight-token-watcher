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

    public static Bitmap Render(int crit, int warn, bool anyData, int size, double? weekFraction = null)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        if (!anyData)
        {
            var stroke0 = Math.Max(2f, size * 0.19f);
            var inset0 = stroke0 / 2f + 0.5f;
            var ring0 = new RectangleF(inset0, inset0, size - 2 * inset0, size - 2 * inset0);
            using (var bg = new SolidBrush(Color.FromArgb(235, 20, 22, 27)))
                g.FillEllipse(bg, RectangleF.Inflate(ring0, stroke0 / 2f, stroke0 / 2f));
            using var pen0 = new Pen(Color.FromArgb(140, 146, 158), stroke0);
            g.DrawEllipse(pen0, ring0);
            return bmp;
        }

        // ring gauge with the weekly usage in the middle (Hermes iOS quota-ring look)
        var frac = Math.Clamp(weekFraction ?? 0, 0, 1);
        var blocked = frac >= 0.999;
        var stroke = Math.Max(2f, size * 0.15f);
        var inset = stroke / 2f + 0.5f;
        var ring = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var bg = new SolidBrush(Color.FromArgb(235, 20, 22, 27)))
            g.FillEllipse(bg, RectangleF.Inflate(ring, stroke / 2f, stroke / 2f));
        using (var track = new Pen(Color.FromArgb(70, 76, 88), stroke))
            g.DrawEllipse(track, ring);
        if (frac > 0.004) // no arc at 0 % – a round cap alone reads as "a bit used"
        {
            using var arc = new Pen(blocked ? Theme.Crit : Theme.Ok, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, ring, -90, (float)(frac * 360));
        }

        var pct = (int)Math.Round(frac * 100);
        var text = pct.ToString();
        var fontPx = size * (text.Length >= 3 ? 0.34f : text.Length == 2 ? (size < 20 ? 0.40f : 0.44f) : 0.52f);
        using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Color.White); // red on dark is illegible; the ring carries the state
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, font, fg, new RectangleF(0, size * 0.05f, size, size), fmt);

        // small corner dot for other problems, bottom-right (the arc starts at 12 o'clock –
        // a top-right dot would cover small fills)
        if (crit > 0 || warn > 0)
        {
            var dotColor = crit > 0 ? Theme.Crit : Theme.Warn;
            var d = Math.Max(6f, size * 0.40f);
            using var dot = new SolidBrush(dotColor);
            using var edge = new Pen(Color.FromArgb(255, 20, 22, 27), Math.Max(1f, size * 0.06f));
            var r = new RectangleF(size - d, size - d - 0.5f, d, d);
            g.FillEllipse(dot, r);
            g.DrawEllipse(edge, r);
        }
        return bmp;
    }

    /// <summary>Creates an icon; call <see cref="Destroy"/> on the handle once it has been replaced.</summary>
    public static Icon Create(int crit, int warn, bool anyData, int size, double? weekFraction, out IntPtr handle)
    {
        using var bmp = Render(crit, warn, anyData, size, weekFraction);
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero) DestroyIcon(handle);
    }
}
