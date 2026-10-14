using System.Drawing.Drawing2D;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Tray icons drawn at runtime: dark chip with fill level + status color.</summary>
internal static class IconFactory
{
    private static readonly Dictionary<(Health, int), Icon> Cache = new();

    public static Icon Get(Health health, double? usedFraction)
    {
        var pct = (int)Math.Round(Math.Clamp(usedFraction ?? 0, 0, 1) * 10); // 0..10 Stufen
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

            var rect = new Rectangle(1, 1, s - 2, s - 2);
            using var bgPath = Rounded(rect, 7);
            using (var bg = new SolidBrush(Theme.Card))
                g.FillPath(bg, bgPath);

            // fill level from the bottom
            if (usedFraction is not null)
            {
                var frac = Math.Clamp(usedFraction.Value, 0, 1);
                var fillH = (int)Math.Round((s - 8) * frac);
                if (fillH > 0)
                {
                    using var clip = new GraphicsPath();
                    var fillRect = new Rectangle(4, s - 4 - fillH, s - 8, fillH);
                    using var fillBrush = new SolidBrush(Color.FromArgb(110, Theme.ForHealth(health)));
                    g.SetClip(bgPath);
                    g.FillRectangle(fillBrush, fillRect);
                    g.ResetClip();
                }
            }

            using (var pen = new Pen(Theme.ForHealth(health), 2.5f))
                g.DrawPath(pen, bgPath);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
