using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace InsightTokenWatcher.Linux;

/// <summary>Tray badge and window icon, drawn with Skia (no icon files needed).
/// Same states as the Windows version: lime ring = all fine, coloured disc with the number
/// of affected accounts = warn/crit, "!" for ten or more, grey ring = no data yet.</summary>
internal static class IconFactory
{
    public static WindowIcon WindowIcon(int size = 128) =>
        new(ToStream(draw: canvas => DrawBadge(canvas, size, Kind.Ring, 0)));

    public static WindowIcon TrayIcon(int crit, int errors, int warn, bool anyData, int size = 32)
    {
        var kind = crit + errors > 0 ? Kind.Crit : warn > 0 ? Kind.Warn : anyData ? Kind.Ring : Kind.None;
        var count = crit + errors > 0 ? crit + errors : warn;
        return new WindowIcon(ToStream(draw: canvas => DrawBadge(canvas, size, kind, count)));
    }

    /// <summary>Raster badge for the render harness / docs.</summary>
    internal static SKBitmap BadgePng(int crit, int errors, int warn, bool anyData, int size)
    {
        var kind = crit + errors > 0 ? Kind.Crit : warn > 0 ? Kind.Warn : anyData ? Kind.Ring : Kind.None;
        var count = crit + errors > 0 ? crit + errors : warn;
        var bmp = new SKBitmap(size, size);
        using var c = new SKCanvas(bmp);
        DrawBadge(c, size, kind, count);
        return bmp;
    }

    private enum Kind { Ring, Crit, Warn, None }

    private static MemoryStream ToStream(Action<SKCanvas> draw)
    {
        using var bmp = new SKBitmap(256, 256);
        using var canvas = new SKCanvas(bmp);
        draw(canvas);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static void DrawBadge(SKCanvas c, int size, Kind kind, int count)
    {
        var s = size;
        var fg = kind switch
        {
            Kind.Crit => new SKColor(240, 82, 82),
            Kind.Warn => new SKColor(245, 181, 46),
            _ => new SKColor(168, 225, 12)
        };
        var neutral = new SKColor(128, 134, 146);

        using var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.14f, Color = neutral
        };
        using var disc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = fg };
        using var text = new SKPaint
        {
            IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.Default,
            TextSize = s * 0.80f,
            Color = kind == Kind.Warn ? SKColors.Black : SKColors.White
        };

        if (kind is Kind.Ring or Kind.None)
        {
            ring.Color = kind == Kind.Ring ? fg : neutral;
            c.DrawArc(new SKRect(s * 0.12f, s * 0.12f, s * 0.88f, s * 0.88f), -90, kind == Kind.Ring ? 250 : 360, false, ring);
            return;
        }

        c.DrawCircle(s / 2f, s / 2f, s * 0.46f, disc);
        var label = count > 9 ? "!" : count.ToString();
        // Segoe-style optical centering: digits sit slightly high in their line box
        c.DrawText(label, s / 2f, s * 0.72f, text);
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
