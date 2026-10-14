using System.Drawing.Drawing2D;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Tray icons drawn at runtime: dark disc with a status-colored gauge arc.</summary>
internal static class IconFactory
{
    private static readonly Dictionary<(Health, int), Icon> Cache = new();

    public static Icon Get(Health health, double? usedFraction)
    {
        var pct = (int)Math.Round(Math.Clamp(usedFraction ?? 0, 0, 1) * 10); // 0..10 steps
        var key = (health, pct);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var icon = Draw(health, usedFraction);
            Cache[key] = icon;
            return icon;
        }
    }

    private static Icon Draw(Health health, double? usedFraction)
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var col = Theme.ForHealth(health);
            var rect = new RectangleF(3, 3, s - 6, s - 6);

            // dark disc
            using (var bg = new SolidBrush(Color.FromArgb(235, 22, 24, 29)))
                g.FillEllipse(bg, rect);

            // track ring
            using (var trackPen = new Pen(Color.FromArgb(70, 80, 90), 4f))
                g.DrawEllipse(trackPen, rect);

            // gauge arc (used fraction), from top, clockwise
            if (usedFraction is not null)
            {
                var sweep = (float)(Math.Clamp(usedFraction.Value, 0, 1) * 360.0);
                if (sweep > 0.5f)
                {
                    using var pen = new Pen(col, 4f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    g.DrawArc(pen, rect, -90, sweep);
                }
            }
            else
            {
                // no data yet: neutral ring segment
                using var pen = new Pen(Color.FromArgb(120, Theme.Unknown), 4f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawArc(pen, rect, -90, 270);
            }

            // center status dot
            using (var dot = new SolidBrush(col))
            using (var glow = new SolidBrush(Color.FromArgb(50, col)))
            {
                g.FillEllipse(glow, s / 2f - 7, s / 2f - 7, 14, 14);
                g.FillEllipse(dot, s / 2f - 4.5f, s / 2f - 4.5f, 9, 9);
            }
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
