using System.Drawing.Drawing2D;

namespace InsightTokenWatcher;

internal static class Gfx
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using var path = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
    {
        using var path = Round(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, path);
    }

    /// <summary>Rounded progress bar: track plus fill proportional to <paramref name="fraction"/>.</summary>
    public static void Bar(Graphics g, RectangleF r, double fraction, Color fill, Color track)
    {
        var radius = r.Height / 2f;
        FillRound(g, track, r, radius);
        var f = (float)Math.Clamp(fraction, 0, 1);
        if (f <= 0.001f) return;
        var w = Math.Max(r.Height, r.Width * f);
        FillRound(g, fill, new RectangleF(r.X, r.Y, w, r.Height), radius);
    }

    public const TextFormatFlags TextBase =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
        TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix;

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0) =>
        TextRenderer.DrawText(g, s, f, r, c, TextBase | extra);

    public static int Measure(Graphics g, string s, Font f) =>
        TextRenderer.MeasureText(g, s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
}
