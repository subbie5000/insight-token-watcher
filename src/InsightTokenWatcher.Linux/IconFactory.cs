using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace InsightTokenWatcher.Linux;

/// <summary>Tray gauge and app icon, drawn with Skia (no icon files needed). Same states as the
/// Windows version: ring = mean weekly usage of the Anthropic accounts with the percent inside,
/// corner dot (red/amber) for problem accounts, grey ring = no data yet. The launcher/window
/// icon is the brand mark (same ring, fixed 250° arc, no number).</summary>
internal static class IconFactory
{
    public static WindowIcon WindowIcon(int size = 128) =>
        new(ToStream(size, canvas => DrawBrand(canvas, size)));

    public static WindowIcon TrayIcon(int crit, int errors, int warn, bool anyData, int size = 32,
        double? weekFraction = null) =>
        new(TrayIconPng(crit, errors, warn, anyData, size, weekFraction));

    /// <summary>The PNG the tray receives (also inspected by the render harness).</summary>
    internal static MemoryStream TrayIconPng(int crit, int errors, int warn, bool anyData, int size = 32,
        double? weekFraction = null) =>
        ToStream(size, canvas => DrawGauge(canvas, size, crit + errors, warn, anyData, weekFraction));

    /// <summary>Raster badge for the render harness / docs.</summary>
    internal static SKBitmap BadgePng(int crit, int errors, int warn, bool anyData, int size,
        double? weekFraction = null)
    {
        var bmp = new SKBitmap(size, size);
        using var c = new SKCanvas(bmp);
        DrawGauge(c, size, crit + errors, warn, anyData, weekFraction);
        return bmp;
    }

    /// <summary>Launcher/menu icon: the brand ring (lime arc), never a live percentage.</summary>
    internal static SKBitmap AppIconPng(int size)
    {
        var bmp = new SKBitmap(size, size);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        DrawBrand(c, size);
        return bmp;
    }

    /// <summary>Renders a drawing laid out for <paramref name="size"/> px into a 256 px PNG. The
    /// canvas is scaled: before, a 32 px tray gauge sat in the top-left corner of a transparent
    /// 256 px bitmap, so the tray showed it at an eighth of its size.</summary>
    private static MemoryStream ToStream(int size, Action<SKCanvas> draw)
    {
        const int px = 256;
        using var bmp = new SKBitmap(px, px);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(px / (float)size);
        draw(canvas);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return ms;
    }


    /// <summary>Tray gauge (parity with the Windows IconFactory): weekly-usage ring with the
    /// percent in the middle; corner dot (bottom-right) for problems; grey ring without data.</summary>
    private static void DrawGauge(SKCanvas c, int size, int crit, int warn, bool anyData, double? weekFraction)
    {
        var s = (float)size;
        if (!anyData)
        {
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.15f, Color = new SKColor(140, 146, 158) };
            c.DrawCircle(s / 2f, s / 2f, s * 0.42f, ring);
            return;
        }

        var frac = Math.Clamp(weekFraction ?? 0, 0, 1);
        var blocked = frac >= 0.999;
        var trackC = new SKColor(70, 76, 88);
        var arcC = blocked ? new SKColor(240, 82, 82) : new SKColor(168, 225, 12);

        using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(20, 22, 27, 235) })
            c.DrawCircle(s / 2f, s / 2f, s * 0.5f, bg);
        using (var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.15f, Color = trackC })
            c.DrawCircle(s / 2f, s / 2f, s * 0.42f, track);
        if (frac > 0.004)
        {
            using var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.15f, Color = arcC, StrokeCap = SKStrokeCap.Round };
            using var path = new SKPath();
            var r = new SKRect(s * 0.08f, s * 0.08f, s * 0.92f, s * 0.92f);
            path.AddArc(r, -90, (float)(frac * 360));
            c.DrawPath(path, arc);
        }

        var pct = ((int)Math.Round(frac * 100)).ToString();
        var fontPx = s * (pct.Length >= 3 ? 0.34f : pct.Length == 2 ? (size < 20 ? 0.40f : 0.44f) : 0.52f);
        using (var text = new SKPaint
        {
            IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold),
            TextSize = fontPx, Color = SKColors.White
        })
            c.DrawText(pct, s / 2f, s * 0.5f + fontPx * 0.35f, text);

        if (crit > 0 || warn > 0)
        {
            var dotC = crit > 0 ? new SKColor(240, 82, 82) : new SKColor(245, 181, 46);
            var d = Math.Max(6f, s * 0.40f);
            using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = dotC };
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, s * 0.06f), Color = new SKColor(20, 22, 27) };
            c.DrawCircle(s - d / 2f, s - d / 2f, d / 2f, dot);
            c.DrawCircle(s - d / 2f, s - d / 2f, d / 2f, edge);
        }
    }

    /// <summary>Brand mark (launcher/window icon): dark disc, grey track, lime 250° arc – the
    /// tray gauge look without a number, readable on light and dark launchers.</summary>
    private static void DrawBrand(SKCanvas c, int size)
    {
        var s = (float)size;
        using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(20, 22, 27, 235) })
            c.DrawCircle(s / 2f, s / 2f, s * 0.5f, bg);
        using (var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.15f, Color = new SKColor(70, 76, 88) })
            c.DrawCircle(s / 2f, s / 2f, s * 0.42f, track);
        using var arc = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.15f,
            Color = new SKColor(168, 225, 12), StrokeCap = SKStrokeCap.Round
        };
        using var path = new SKPath();
        path.AddArc(new SKRect(s * 0.08f, s * 0.08f, s * 0.92f, s * 0.92f), -90, 250);
        c.DrawPath(path, arc);
    }

    /// <summary>Button glyphs as vector paths – no icon-font dependency on Linux.</summary>
    public static void DrawGlyph(SKCanvas c, float x, float y, float size, Glyph glyph, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.6f, size * 0.075f), Color = color };
        switch (glyph)
        {
            case Glyph.Refresh:
                // 270° arc + arrowhead
                var r = new SKRect(x + size * 0.15f, y + size * 0.15f, x + size * 0.85f, y + size * 0.85f);
                using (var arc = new SKPath())
                {
                    arc.AddArc(r, -30, 300);
                    c.DrawPath(arc, p);
                }
                var ax = x + size * 0.80f; var ay = y + size * 0.30f;
                using (var head = new SKPath())
                {
                    head.MoveTo(ax, ay); head.LineTo(ax - size * 0.16f, ay - size * 0.10f); head.LineTo(ax - size * 0.10f, ay - size * 0.20f);
                    head.Close();
                    c.DrawPath(head, new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color });
                }
                break;
            case Glyph.Settings:
                var cx = x + size / 2f; var cy = y + size / 2f;
                c.DrawCircle(cx, cy, size * 0.22f, p);
                for (var i = 0; i < 8; i++)
                {
                    var a = (float)(i * Math.PI / 4);
                    var r1 = size * 0.34f; var r2 = size * 0.45f;
                    c.DrawLine(cx + r1 * MathF.Cos(a), cy + r1 * MathF.Sin(a), cx + r2 * MathF.Cos(a), cy + r2 * MathF.Sin(a), p);
                }
                break;
            case Glyph.Pin:
            case Glyph.Pinned:
                using (var pin = new SKPath())
                {
                    var pxc = x + size / 2f; var pyc = y + size * 0.52f;
                    pin.MoveTo(pxc, y + size * 0.14f);            // head top
                    pin.LineTo(pxc, y + size * 0.34f);            // needle down
                    pin.LineTo(x + size * 0.30f, y + size * 0.72f); // left arm
                    pin.LineTo(pxc, pyc + size * 0.24f);           // via body
                    pin.LineTo(x + size * 0.70f, y + size * 0.72f); // right arm
                    pin.Close();
                    c.DrawPath(pin, new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = p.StrokeWidth, Color = color, StrokeJoin = SKStrokeJoin.Round });
                }
                if (glyph == Glyph.Pinned)
                    c.DrawCircle(x + size / 2f, y + size * 0.22f, size * 0.10f,
                        new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color });
                break;
        }
    }

    public enum Glyph { Refresh, Settings, Pin, Pinned }
}
