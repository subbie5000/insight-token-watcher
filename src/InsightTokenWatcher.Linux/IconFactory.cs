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
    /// <summary>Header button glyphs, drawn as vectors (no icon font on Linux). They end up at
    /// about 19 px, so the shapes are simple and the strokes strong: thin, detached strokes
    /// read as "sun" instead of gear and "A" instead of pin at that size.</summary>
    public static void DrawGlyph(SKCanvas c, float x, float y, float size, Glyph glyph, SKColor color)
    {
        var sw = Math.Max(1.6f, size * 0.10f);
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = sw, Color = color,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        float X(float f) => x + size * f;
        float Y(float f) => y + size * f;

        switch (glyph)
        {
            case Glyph.Refresh:
            {
                // open circle with a solid arrowhead at its end
                var r = new SKRect(X(0.16f), Y(0.16f), X(0.84f), Y(0.84f));
                using var arc = new SKPath();
                arc.AddArc(r, -20, 285);
                using var ringStroke = stroke.Clone();
                ringStroke.StrokeWidth = sw * 1.2f; // a thin circle goes grainy at 19 px
                c.DrawPath(arc, ringStroke);
                // arc ends at -20° + 285° = 265° (just left of 12 o'clock), moving right
                var a = 265f * MathF.PI / 180f;
                var ex = X(0.5f) + size * 0.34f * MathF.Cos(a);
                var ey = Y(0.5f) + size * 0.34f * MathF.Sin(a);
                using var head = new SKPath();
                head.MoveTo(ex + size * 0.20f, ey + size * 0.02f);
                head.LineTo(ex - size * 0.04f, ey - size * 0.17f);
                head.LineTo(ex - size * 0.04f, ey + size * 0.19f);
                head.Close();
                c.DrawPath(head, fill);
                break;
            }
            case Glyph.Settings:
            {
                // gear: ring with six teeth that sit ON the ring, hole in the middle (eight
                // teeth crumble at this size)
                var cx = X(0.5f);
                var cy = Y(0.5f);
                using var tooth = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.18f, Color = color,
                    StrokeCap = SKStrokeCap.Butt
                };
                for (var i = 0; i < 6; i++)
                {
                    var a = i * MathF.PI / 3 + MathF.PI / 6;
                    c.DrawLine(cx + size * 0.27f * MathF.Cos(a), cy + size * 0.27f * MathF.Sin(a),
                        cx + size * 0.44f * MathF.Cos(a), cy + size * 0.44f * MathF.Sin(a), tooth);
                }
                using var ring = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.13f, Color = color
                };
                c.DrawCircle(cx, cy, size * 0.25f, ring);
                break;
            }
            case Glyph.Pin:
            case Glyph.Pinned:
            {
                // pushpin seen from the side: cap, body widening to a plate, needle
                using var body = new SKPath();
                body.MoveTo(X(0.36f), Y(0.14f));
                body.LineTo(X(0.64f), Y(0.14f));
                body.LineTo(X(0.60f), Y(0.44f));
                body.LineTo(X(0.76f), Y(0.60f));
                body.LineTo(X(0.24f), Y(0.60f));
                body.LineTo(X(0.40f), Y(0.44f));
                body.Close();
                if (glyph == Glyph.Pinned) c.DrawPath(body, fill);
                c.DrawPath(body, stroke);
                c.DrawLine(X(0.5f), Y(0.62f), X(0.5f), Y(0.90f), stroke);
                break;
            }
        }
    }

    public enum Glyph { Refresh, Settings, Pin, Pinned }
}
