using System.Drawing.Drawing2D;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Owner-drawn card per account: status, one bar per window, burn forecast, sparkline.</summary>
internal sealed class AccountCardControl : Control
{
    private readonly AccountView _view;
    private readonly TrayAppContext _ctx;
    private readonly float _s = Theme.DpiScale;
    private bool _hover;

    private int Sc(int v) => (int)Math.Round(v * _s);

    public AccountCardControl(AccountView view, TrayAppContext ctx)
    {
        _view = view;
        _ctx = ctx;
        Height = ComputeHeight();
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
    }

    private int ComputeHeight()
    {
        var h = Sc(12) + Sc(30); // pad + header
        var snap = _view.Snapshot;
        if (snap is null || !snap.Ok || snap.Windows.Count == 0)
        {
            h += Sc(46);
        }
        else
        {
            foreach (var w in snap.Windows)
                h += Sc(w.IsBalance || w.IsInfoOnly ? 40 : 58);
        }
        return h + Sc(36) + Sc(12); // sparkline + pad
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _ctx.Engine.ForceRefresh(_view.Config.Id); // click = re-poll this account immediately
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var w = Width;
        var y = Sc(12);

        var health = _view.Health;
        if (_view.Snapshot?.Ok == false) health = Health.Crit;

        // card frame (crit gets a tinted border)
        var borderColor = health == Health.Crit ? Color.FromArgb(120, Theme.Crit)
            : _hover ? Theme.HoverBorder : Theme.CardBorder;
        using (var path = Rounded(new Rectangle(0, 0, w - 1, Height - 1), Sc(10)))
        using (var bg = new SolidBrush(Theme.Card))
        using (var pen = new Pen(borderColor, health == Health.Crit ? 1.6f : 1f))
        {
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }

        // header: provider dot + name + status pill
        var dotColor = Theme.ProviderColor(_view.Config.Provider);
        using (var dot = new SolidBrush(dotColor))
        using (var glow = new SolidBrush(Color.FromArgb(45, dotColor)))
        {
            g.FillEllipse(glow, Sc(11), y + Sc(2), Sc(16), Sc(16));
            g.FillEllipse(dot, Sc(14), y + Sc(5), Sc(10), Sc(10));
        }

        var name = _view.Config.Name + (_view.Polling ? "  …" : "");
        using (var nameBrush = new SolidBrush(Theme.Text))
            g.DrawString(name, Theme.NameFont, nameBrush, Sc(36), y + Sc(3));

        var pillText = _view.Snapshot?.Ok == false ? "FEHLER" : Theme.HealthLabel(health);
        var pillW = (int)g.MeasureString(pillText, Theme.SmallFont).Width + Sc(18);
        DrawPill(g, pillText, Theme.ForHealth(health), w - Sc(12) - pillW, y + Sc(3), pillW, Sc(19));
        y += Sc(30);

        var snap = _view.Snapshot;
        if (snap is null)
        {
            using var b = new SolidBrush(Theme.Muted);
            g.DrawString("Noch keine Daten – Abfrage läuft…", Theme.SmallFont, b, Sc(14), y + Sc(10));
            y += Sc(46);
        }
        else if (!snap.Ok)
        {
            using var b = new SolidBrush(Theme.Crit);
            g.DrawString("⚠ " + (snap.Error ?? "Unbekannter Fehler"), Theme.SmallFont, b,
                new RectangleF(Sc(14), y + Sc(6), w - Sc(28), Sc(38)));
            y += Sc(46);
        }
        else
        {
            foreach (var win in snap.Windows)
            {
                _view.Evals.TryGetValue(win.Key, out var ev);
                y += DrawWindow(g, win, ev, w, y);
            }
        }

        DrawSparkline(g, new Rectangle(Sc(14), y + Sc(4), w - Sc(28), Sc(30)));
    }

    private int DrawWindow(Graphics g, QuotaWindow win, WindowEvaluation? ev, int w, int y)
    {
        var x = Sc(14);
        var barW = w - 2 * x;

        // Row 1: label + values on the right
        using (var b = new SolidBrush(Theme.Text))
            g.DrawString(win.Label, Theme.BodyFont, b, x, y);
        string right;
        if (win.IsInfoOnly)
            right = win.Remaining is not null ? Formatting.Money(win.Remaining, win.Unit) : "";
        else if (win.IsBalance)
            right = Formatting.Money(win.Remaining, win.Unit);
        else
            right = $"{Formatting.Percent(win.UsedFraction)} verbraucht";
        if (!win.IsBalance && !win.IsInfoOnly && win.Remaining is not null && win.Total is not null)
            right += $"  ·  noch {Formatting.Count(win.Remaining)} von {Formatting.Count(win.Total)} {win.Unit}";
        var rs = g.MeasureString(right, Theme.NumFont);
        using (var b = new SolidBrush(Theme.Text))
            g.DrawString(right, Theme.NumFont, b, x + barW - rs.Width, y);
        y += Sc(20);

        // Row 2: bar (only for quota windows with utilization)
        if (!win.IsBalance && !win.IsInfoOnly)
        {
            var barRect = new RectangleF(x, y + Sc(3), barW, Sc(9));
            using (var trackPath = RoundedF(barRect, Sc(4)))
            using (var track = new SolidBrush(Theme.Track))
                g.FillPath(track, trackPath);
            if (win.UsedFraction is not null)
            {
                var frac = (float)Math.Clamp(win.UsedFraction.Value, 0, 1);
                if (frac > 0)
                {
                    var fillRect = new RectangleF(x, y + Sc(3), Math.Max(Sc(9), barW * frac), Sc(9));
                    var col = Theme.ForHealth(ev?.Health ?? Health.Unknown);
                    using var fillPath = RoundedF(fillRect, Sc(4));
                    using var grad = new LinearGradientBrush(fillRect,
                        Color.FromArgb(235, col), Color.FromArgb(160, col), LinearGradientMode.Horizontal);
                    g.FillPath(grad, fillPath);
                }
            }
            y += Sc(20);
        }
        else
        {
            y += Sc(2);
        }

        // Row 3: forecast / reset with status glyph
        var verdict = ev?.Verdict ?? "";
        if (win.ResetAtUtc is not null)
        {
            var ttr = win.ResetAtUtc.Value - DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(verdict) || ttr > TimeSpan.Zero)
                verdict = string.IsNullOrEmpty(verdict) ? $"Reset in {Formatting.Span(ttr)}" : verdict;
        }
        if (!string.IsNullOrEmpty(win.Note))
            verdict = string.IsNullOrEmpty(verdict) ? win.Note : verdict + "  ·  " + win.Note;
        var h = ev?.Health ?? Health.Unknown;
        var glyph = h switch { Health.Ok => "✓ ", Health.Warn => "! ", Health.Crit => "✗ ", _ => "" };
        if (h == Health.Ok && !string.IsNullOrEmpty(verdict) && verdict.StartsWith("Kein Verbrauch")) glyph = "";
        using (var b = new SolidBrush(ev is null ? Theme.Muted : Theme.ForHealth(h)))
            g.DrawString(glyph + verdict, Theme.SmallFont, b, x, y);
        y += Sc(18);
        return Sc(win.IsBalance || win.IsInfoOnly ? 40 : 58);
    }

    private void DrawSparkline(Graphics g, Rectangle rect)
    {
        var pts = _view.PrimarySeries;
        using (var trackPen = new Pen(Theme.Track))
            g.DrawLine(trackPen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
        if (pts.Count < 2)
        {
            using var b = new SolidBrush(Theme.Muted);
            g.DrawString("Verlauf (60 min) – sammelt Daten…", Theme.SmallFont, b, rect.Left, rect.Top);
            return;
        }

        var balance = _view.Snapshot?.Windows.FirstOrDefault()?.IsBalance == true;
        var values = pts.Select(p => balance ? p.Remaining ?? 0 : p.UsedFraction).ToList();
        var max = values.Max();
        var min = values.Min();
        var span = Math.Max(1e-9, max - min);

        var n = values.Count;
        var step = (float)rect.Width / Math.Max(1, n - 1);
        var col = Theme.ProviderColor(_view.Config.Provider);
        var points = new PointF[n];
        for (var i = 0; i < n; i++)
            points[i] = new PointF(rect.Left + i * step,
                rect.Bottom - 2 - (float)((values[i] - min) / span) * (rect.Height - 8));

        // area fill under the line
        using (var area = new GraphicsPath())
        {
            area.AddLines(points);
            area.AddLine(points[^1], new PointF(rect.Right, rect.Bottom));
            area.AddLine(new PointF(rect.Right, rect.Bottom), new PointF(rect.Left, rect.Bottom));
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(28, col));
            g.FillPath(fill, area);
        }
        using (var pen = new Pen(col, 1.6f) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, points);

        var caption = balance
            ? $"Verlauf Guthaben: {Formatting.Money(min, "USD")}–{Formatting.Money(max, "USD")}"
            : $"Verlauf: {Formatting.Percent(min)}–{Formatting.Percent(max)}";
        using (var b = new SolidBrush(Theme.Muted))
            g.DrawString(caption, Theme.SmallFont, b, rect.Left, rect.Top - 2);
    }

    private static void DrawPill(Graphics g, string text, Color color, int x, int y, int w, int h)
    {
        using var path = Rounded(new Rectangle(x, y, w, h), h / 2);
        using var brush = new SolidBrush(Color.FromArgb(38, color));
        using var pen = new Pen(Color.FromArgb(200, color));
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
        var size = g.MeasureString(text, Theme.SmallFont);
        using var tb = new SolidBrush(color);
        g.DrawString(text, Theme.SmallFont, tb, x + (w - size.Width) / 2, y + (h - size.Height) / 2 - 1);
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(2, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static GraphicsPath RoundedF(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(2f, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
