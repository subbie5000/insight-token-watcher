using System.Drawing.Drawing2D;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Owner-drawn card per account: status, one bar per window, burn forecast, sparkline.</summary>
internal sealed class AccountCardControl : Control
{
    private const int HeaderH = 26;
    private const int WindowH = 52;
    private const int SparkH = 30;
    private const int Pad = 10;

    private readonly AccountView _view;
    private readonly TrayAppContext _ctx;

    public AccountCardControl(AccountView view, TrayAppContext ctx)
    {
        _view = view;
        _ctx = ctx;
        var windows = view.Snapshot?.Windows.Count ?? 0;
        Height = Pad + HeaderH + Math.Max(1, windows) * WindowH + SparkH + Pad;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _ctx.Engine.ForceRefresh(_view.Config.Id); // click = re-poll this account immediately
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var w = Width;
        var y = Pad;

        // Rahmen
        using (var path = Rounded(new Rectangle(0, 0, w - 1, Height - 1), 10))
        using (var pen = new Pen(Theme.CardBorder))
        {
            g.FillPath(new SolidBrush(Theme.Card), path);
            g.DrawPath(pen, path);
        }

        // Header: Provider-Punkt + Name + Status-Pille
        var dotColor = Theme.ProviderColor(_view.Config.Provider);
        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, Pad + 2, y + 6, 10, 10);

        var name = _view.Config.Name + (_view.Polling ? "  …" : "");
        g.DrawString(name, Theme.NameFont, new SolidBrush(Theme.Text), Pad + 18, y + 2);

        var health = _view.Health;
        if (_view.Snapshot?.Ok == false) health = Health.Crit;
        var pillText = _view.Snapshot?.Ok == false ? "FEHLER" : Theme.HealthLabel(health);
        DrawPill(g, pillText, Theme.ForHealth(health), w - Pad - 86, y + 2, 86, 18);
        y += HeaderH;

        if (_view.Snapshot is null)
        {
            g.DrawString("Noch keine Daten – Abfrage läuft…", Theme.SmallFont,
                new SolidBrush(Theme.Muted), Pad + 4, y + 8);
            y += WindowH;
        }
        else if (_view.Snapshot.Ok == false)
        {
            var err = _view.Snapshot.Error ?? "Unbekannter Fehler";
            g.DrawString(err, Theme.SmallFont, new SolidBrush(Theme.Crit),
                new RectangleF(Pad + 4, y + 4, w - 2 * Pad - 8, WindowH - 8));
            y += WindowH;
        }
        else
        {
            foreach (var win in _view.Snapshot.Windows)
            {
                _view.Evals.TryGetValue(win.Key, out var ev);
                y += DrawWindow(g, win, ev, w, y);
            }
        }

        DrawSparkline(g, new Rectangle(Pad + 4, y + 2, w - 2 * Pad - 8, SparkH - 6));
    }

    private int DrawWindow(Graphics g, QuotaWindow win, WindowEvaluation? ev, int w, int y)
    {
        var barW = w - 2 * Pad - 8;
        var x = Pad + 4;

        // Row 1: label + values on the right
        g.DrawString(win.Label, Theme.BodyFont, new SolidBrush(Theme.Text), x, y);
        string right = win.IsBalance
            ? Formatting.Money(win.Remaining, win.Unit)
            : $"{Formatting.Percent(win.UsedFraction)} verbraucht";
        if (!win.IsBalance && win.Remaining is not null && win.Total is not null)
            right += $"  ·  noch {Formatting.Count(win.Remaining)} von {Formatting.Count(win.Total)} {win.Unit}";
        var rs = g.MeasureString(right, Theme.NumFont);
        g.DrawString(right, Theme.NumFont, new SolidBrush(Theme.Text), x + barW - rs.Width, y);
        y += 18;

        // Row 2: bar (only for quota windows with utilization)
        if (!win.IsBalance)
        {
            var barRect = new RectangleF(x, y + 3, barW, 8);
            using (var trackPath = RoundedF(barRect, 4))
                g.FillPath(new SolidBrush(Theme.Track), trackPath);
            if (win.UsedFraction is not null)
            {
                var frac = (float)Math.Clamp(win.UsedFraction.Value, 0, 1);
                if (frac > 0)
                {
                    var fillRect = new RectangleF(x, y + 3, Math.Max(8, barW * frac), 8);
                    using var fillPath = RoundedF(fillRect, 4);
                    using var brush = new SolidBrush(Theme.ForHealth(ev?.Health ?? Health.Unknown));
                    g.FillPath(brush, fillPath);
                }
            }
            y += 18;
        }
        else
        {
            y += 2;
        }

        // Row 3: forecast / reset
        var verdict = ev?.Verdict ?? "";
        if (win.ResetAtUtc is not null)
        {
            var ttr = win.ResetAtUtc.Value - DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(verdict) || ttr > TimeSpan.Zero)
                verdict = string.IsNullOrEmpty(verdict) ? $"Reset in {Formatting.Span(ttr)}" : verdict;
        }
        if (!string.IsNullOrEmpty(win.Note))
            verdict = string.IsNullOrEmpty(verdict) ? win.Note : verdict + "  ·  " + win.Note;
        var vc = ev is null ? Theme.Muted : Theme.ForHealth(ev.Health);
        g.DrawString(verdict, Theme.SmallFont, new SolidBrush(vc), x, y);
        y += 16;
        return win.IsBalance ? 38 : WindowH;
    }

    private void DrawSparkline(Graphics g, Rectangle rect)
    {
        var pts = _view.PrimarySeries;
        using var trackPen = new Pen(Theme.Track);
        g.DrawLine(trackPen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
        if (pts.Count < 2)
        {
            g.DrawString("Verlauf (60 min) – sammelt Daten…", Theme.SmallFont,
                new SolidBrush(Theme.Muted), rect.Left, rect.Top);
            return;
        }

        var balance = _view.Snapshot?.Windows.FirstOrDefault()?.IsBalance == true;
        var values = pts.Select(p => balance ? p.Remaining ?? 0 : p.UsedFraction).ToList();
        var max = values.Max();
        var min = values.Min();
        var span = Math.Max(1e-9, max - min);

        var n = values.Count;
        var step = (float)rect.Width / Math.Max(1, n - 1);
        using var pen = new Pen(Theme.ProviderColor(_view.Config.Provider), 1.6f);
        PointF? prev = null;
        for (var i = 0; i < n; i++)
        {
            var fx = rect.Left + i * step;
            var fy = rect.Bottom - 2 - (float)((values[i] - min) / span) * (rect.Height - 6);
            var cur = new PointF(fx, fy);
            if (prev is not null) g.DrawLine(pen, prev.Value, cur);
            prev = cur;
        }
        var caption = balance
            ? $"Verlauf Guthaben: {Formatting.Money(min, "USD")}–{Formatting.Money(max, "USD")}"
            : $"Verlauf: {Formatting.Percent(min)}–{Formatting.Percent(max)}";
        g.DrawString(caption, Theme.SmallFont, new SolidBrush(Theme.Muted), rect.Left, rect.Top - 2);
    }

    private static void DrawPill(Graphics g, string text, Color color, int x, int y, int w, int h)
    {
        using var path = Rounded(new Rectangle(x, y, w, h), 9);
        using var brush = new SolidBrush(Color.FromArgb(38, color));
        using var pen = new Pen(color);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
        var size = g.MeasureString(text, Theme.SmallFont);
        g.DrawString(text, Theme.SmallFont, new SolidBrush(color),
            x + (w - size.Width) / 2, y + (h - size.Height) / 2);
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

    private static GraphicsPath RoundedF(RectangleF r, float radius)
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
