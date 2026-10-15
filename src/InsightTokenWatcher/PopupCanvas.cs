using System.Drawing.Drawing2D;
using System.Text;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher;

/// <summary>
/// The whole popup as ONE owner-drawn surface: fixed header with column titles, scrollable
/// account table, fixed footer. No child controls → no handle churn, no AutoScroll drift,
/// smooth custom scrolling. Columns: account | short window (5 h) | long window (week).
/// Balance accounts use the same two cells (amount | runway). Details live in a tooltip.
/// </summary>
internal sealed class PopupCanvas : Control
{
    public const float LogicalWidth = 480;

    private const float TitleH = 56, ColumnsH = 24, HeaderH = TitleH + ColumnsH, FooterH = 30;
    private const float RowBase = 52, VerdictH = 20, ExtraH = 20;
    private const float PadX = 16, NameW = 150, CellGap = 16;
    private const float BodyPadTop = 2, BodyPadBottom = 6, ButtonSize = 30;

    private const string ShortColumn = "5 h";
    private const string LongColumn = "Woche";

    private enum Zone { None, Row, Refresh, Settings, Pin, Scrollbar }
    private enum RowKind { Data, Loading, Error }
    private enum RowState { Ok, Warn, Crit, Error, Stale, Pending }

    private sealed class RowInfo
    {
        public required AccountView View;
        public RowLayout Layout = new();
        public AccountSnapshot? Display;
        public RowKind Kind;
        public RowState State;
        public bool Stale;
        public string StatusText = "";
        public Color StatusColor;
        public bool HasVerdict;
        public int Y, H;
    }

    private readonly List<RowInfo> _rows = new();
    private IReadOnlyList<AccountView> _views = Array.Empty<AccountView>();
    private int _contentH;
    private float _scroll, _scrollTarget;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 550 };
    private readonly ToolTip _tip;
    private string _tipText = "";
    private Zone _hover = Zone.None, _pressed = Zone.None;
    private int _hoverRow = -1, _pressedRow = -1;
    private bool _dragging;
    private int _dragStartY;
    private float _dragStartScroll;
    private Point _lastMouse;

    public bool Pinned { get; set; }
    public bool IsDragging => _dragging;
    public bool DrawOwnBorder { get; set; } = true;
    public DateTime Stamp { get; private set; } = DateTime.Now;

    public event Action? RefreshAllClicked;
    public event Action? SettingsClicked;
    public event Action? PinClicked;
    public event Action<Guid>? RowClicked;

    private static int S(float v) => Theme.S(v);

    public PopupCanvas()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        _anim.Tick += (_, _) => AnimateScroll();
        _tipTimer.Tick += (_, _) => ShowTip();
        _tip = new ToolTip { OwnerDraw = true, ShowAlways = true, UseAnimation = false, UseFading = false };
        _tip.Popup += OnTipPopup;
        _tip.Draw += OnTipDraw;
    }

    // ================================================================== data

    public int DesiredHeight => S(HeaderH) + _contentH + S(FooterH);
    public float ScrollOffset => _scroll;
    public int RowCount => _rows.Count;
    public int ContentHeight => _contentH;

    public void SetData(IReadOnlyList<AccountView> views)
    {
        _views = views;
        Stamp = DateTime.Now;
        BuildRows();
        var max = MaxScroll(BodyRect(ClientSize).Height);
        _scroll = Math.Clamp(_scroll, 0, max);
        _scrollTarget = Math.Clamp(_scrollTarget, 0, max);
        if (_hoverRow >= _rows.Count) _hoverRow = -1;
        Invalidate();
    }

    /// <summary>Jump to a scroll offset (render harness).</summary>
    public void ScrollTo(float y)
    {
        var max = MaxScroll(BodyRect(ClientSize).Height);
        _scroll = _scrollTarget = Math.Clamp(y, 0, max);
        Invalidate();
    }

    /// <summary>Simulate hovering a row (render harness).</summary>
    public void SimulateHover(int row)
    {
        _hover = row >= 0 ? Zone.Row : Zone.None;
        _hoverRow = row;
    }

    public string TooltipFor(int row) => row >= 0 && row < _rows.Count ? TooltipText(_rows[row]) : "";

    private void BuildRows()
    {
        _rows.Clear();
        var y = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var v in _views)
        {
            var row = new RowInfo { View = v, Display = v.Display };
            if (row.Display is null)
            {
                row.Kind = v.Snapshot is { Ok: false } ? RowKind.Error : RowKind.Loading;
            }
            else
            {
                row.Kind = RowKind.Data;
                row.Layout = WindowLayout.Classify(row.Display.Windows);
                row.Stale = v.IsStale;
            }
            DescribeStatus(v, row);
            row.HasVerdict = row.Kind == RowKind.Data && VerdictFor(row, now) is not null;

            var h = S(RowBase);
            if (row.Kind == RowKind.Data)
            {
                h += row.Layout.Extras.Count * S(ExtraH);
                if (row.HasVerdict) h += S(VerdictH);
                if (row.Layout.Extras.Count > 0 || row.HasVerdict) h += S(4);
            }
            row.Y = y;
            row.H = h;
            y += h;
            _rows.Add(row);
        }
        _contentH = _rows.Count == 0 ? S(84) : y + S(BodyPadTop) + S(BodyPadBottom);
    }

    private static void DescribeStatus(AccountView v, RowInfo row)
    {
        row.State = row.Kind switch
        {
            RowKind.Error => RowState.Error,
            RowKind.Loading => RowState.Pending,
            _ when row.Stale => v.Failure == FailureKind.AuthExpired ||
                                (v.Failure == FailureKind.Network && v.Health != Health.Crit)
                ? RowState.Stale
                : RowState.Error,
            _ => v.Health switch
            {
                Health.Crit => RowState.Crit,
                Health.Warn => RowState.Warn,
                Health.Ok => RowState.Ok,
                _ => RowState.Pending
            }
        };
        (row.StatusText, row.StatusColor) = row.State switch
        {
            RowState.Ok => ("OK", Theme.Ok),
            RowState.Warn => ("KNAPP", Theme.Warn),
            RowState.Crit => ("KRITISCH", Theme.Crit),
            RowState.Error => ("FEHLER", Theme.Crit),
            RowState.Stale => v.Failure == FailureKind.AuthExpired
                ? ("Token abgelaufen", Theme.Muted)
                : ("offline", Theme.Warn),
            _ => ("warte …", Theme.Muted)
        };
    }

    /// <summary>
    /// Warning line, computed from the same "now" and the same rounding as the cells, so the
    /// reset countdowns in cell and warning line can never disagree.
    /// </summary>
    private static (string text, Color color)? VerdictFor(RowInfo row, DateTimeOffset now)
    {
        var v = row.View;
        var display = row.Display;
        if (row.Kind != RowKind.Data || display is null) return null;

        if (row.Stale)
        {
            var at = v.LastGood!.AtUtc.ToLocalTime();
            return v.Failure switch
            {
                FailureKind.AuthExpired => ($"Token abgelaufen – letzte Werte von {at:HH:mm}", Theme.Muted),
                FailureKind.Network => ($"Keine Verbindung – letzte Werte von {at:HH:mm}", Theme.Warn),
                _ => ($"Abfrage fehlgeschlagen ({ShortError(v.Snapshot?.Error)}) – Werte von {at:HH:mm}", Theme.Crit)
            };
        }

        QuotaWindow? worstWin = null;
        WindowEvaluation? worst = null;
        foreach (var w in display.Windows)
        {
            if (!v.Evals.TryGetValue(w.Key, out var ev) || ev.Health is not (Health.Warn or Health.Crit)) continue;
            if (worst is null || ev.Health > worst.Health)
            {
                worst = ev;
                worstWin = w;
            }
        }
        if (worst is null || worstWin is null) return null;
        var text = LiveVerdict(worstWin, worst, display.AtUtc, now);
        var label = worstWin.IsBalance ? "Guthaben" : WindowLayout.ShortLabel(worstWin);
        return ($"{label}: {text}", Theme.ForHealth(worst.Health));
    }

    private static string LiveVerdict(QuotaWindow w, WindowEvaluation ev, DateTimeOffset snapAt, DateTimeOffset now)
    {
        var elapsed = now - snapAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        TimeSpan? ttr = w.ResetAtUtc is { } r ? r - now : null;
        TimeSpan? tte = ev.TimeToEmpty is { } t ? (t - elapsed < TimeSpan.Zero ? TimeSpan.Zero : t - elapsed) : null;

        if (w.IsBalance)
            return tte is null ? ev.Verdict : $"reicht nur noch {Formatting.Span(tte)}";
        if (w.UsedFraction is >= 0.999)
            return ttr is { } left && left > TimeSpan.Zero ? $"Limit erreicht – Reset in {Formatting.Span(left)}" : "Limit erreicht";
        if (tte is null) return ev.Verdict;
        if (ttr is null || ttr <= TimeSpan.Zero) return $"noch {Formatting.Span(tte)} bei aktuellem Verbrauch";
        return tte < ttr
            ? $"leer in {Formatting.Span(tte)} – Reset erst in {Formatting.Span(ttr)}!"
            : $"reicht knapp – leer in {Formatting.Span(tte)}, Reset in {Formatting.Span(ttr)}";
    }

    private static string ShortError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unbekannt";
        var e = error;
        var brace = e.IndexOf('{');
        if (brace > 0) e = e[..brace];
        e = e.Trim().TrimEnd('.', ':', '–', '-').Trim();
        return e.Length > 70 ? e[..70] + "…" : e;
    }

    // ================================================================== geometry

    private Rectangle BodyRect(Size sz) =>
        new(0, S(HeaderH), sz.Width, Math.Max(0, sz.Height - S(HeaderH) - S(FooterH)));

    private float MaxScroll(int bodyHeight) => Math.Max(0, _contentH - bodyHeight);

    private static int NameX => S(PadX) + S(12);
    private static int NameWidth => S(NameW) - S(16);
    private static int CellsX => S(PadX) + S(NameW);
    private static int CellsWidth(int w) => w - CellsX - S(PadX) - S(6);
    private static int CellWidth(int w) => (CellsWidth(w) - S(CellGap)) / 2;

    private (Rectangle pin, Rectangle refresh, Rectangle settings) HeaderButtons(int w)
    {
        var bs = S(ButtonSize);
        var y = S(13);
        var settings = new Rectangle(w - S(PadX) - bs + S(4), y, bs, bs);
        var refresh = new Rectangle(settings.X - bs - S(2), y, bs, bs);
        var pin = new Rectangle(refresh.X - bs - S(2), y, bs, bs);
        return (pin, refresh, settings);
    }

    private (Rectangle thumb, int trackH, int thumbH, float max) ThumbGeometry(Rectangle body)
    {
        var max = MaxScroll(body.Height);
        var trackH = body.Height - S(8);
        var thumbH = Math.Max(S(28), (int)(trackH * (float)body.Height / Math.Max(1, _contentH)));
        var thumbY = body.Top + S(4) + (int)((trackH - thumbH) * (max <= 0 ? 0 : _scroll / max));
        var active = _hover == Zone.Scrollbar || _dragging;
        var tw = active ? S(6) : S(4);
        return (new Rectangle(body.Right - S(4) - tw, thumbY, tw, thumbH), trackH, thumbH, max);
    }

    private Zone HitTest(Point p, out int row)
    {
        row = -1;
        var (pin, refresh, settings) = HeaderButtons(ClientSize.Width);
        if (pin.Contains(p)) return Zone.Pin;
        if (refresh.Contains(p)) return Zone.Refresh;
        if (settings.Contains(p)) return Zone.Settings;

        var body = BodyRect(ClientSize);
        if (!body.Contains(p)) return Zone.None;
        if (MaxScroll(body.Height) > 0 && p.X >= body.Right - S(14)) return Zone.Scrollbar;

        var cy = p.Y - body.Top + (int)Math.Round(_scroll) - S(BodyPadTop);
        for (var i = 0; i < _rows.Count; i++)
            if (cy >= _rows[i].Y && cy < _rows[i].Y + _rows[i].H)
            {
                row = i;
                return Zone.Row;
            }
        return Zone.None;
    }

    // ================================================================== painting

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, ClientSize);

    /// <summary>Paints the complete popup into any Graphics (screen or offscreen bitmap).</summary>
    public void Render(Graphics g, Size sz)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var w = sz.Width;
        var now = DateTimeOffset.UtcNow;
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillRectangle(bg, 0, 0, w, sz.Height);

        var body = BodyRect(sz);
        var state = g.Save();
        g.SetClip(body);
        if (_rows.Count == 0)
            Gfx.Text(g, "Keine Konten – Zahnrad oben rechts öffnet die Einstellungen", Theme.FSmall, body,
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var top = body.Top + S(BodyPadTop) - (int)Math.Round(_scroll);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var y = top + row.Y;
            if (y > body.Bottom || y + row.H < body.Top) continue;
            DrawRow(g, row, y, w, now, i == _hoverRow && _hover == Zone.Row, i == _rows.Count - 1);
        }
        g.Restore(state);

        DrawHeader(g, w, _scroll > 0.5f);
        DrawFooter(g, w, sz.Height);
        DrawScrollbar(g, body);
        if (DrawOwnBorder)
            Gfx.DrawRound(g, Theme.CardBorder, new RectangleF(0.5f, 0.5f, w - 1, sz.Height - 1), S(10));
    }

    private void DrawHeader(Graphics g, int w, bool shadow)
    {
        var hh = S(HeaderH);
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillRectangle(bg, 0, 0, w, hh);

        // brand mark (same gauge as the healthy tray icon)
        var mark = new RectangleF(S(PadX) + S(1), S(13), S(16), S(16));
        using (var track = new Pen(Theme.Track, S(2.6f)))
            g.DrawEllipse(track, mark);
        using (var arc = new Pen(Theme.Ok, S(2.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, mark, -90, 250);

        Gfx.Text(g, "Insight Token Watcher", Theme.FTitle, new Rectangle(S(PadX) + S(26), S(9), S(230), S(22)), Theme.Text);
        DrawSummary(g, S(PadX) + S(26), S(32));

        var (pin, refresh, settings) = HeaderButtons(w);
        DrawButton(g, pin, Pinned ? Theme.GlyphPinned : Theme.GlyphPin, "P", _hover == Zone.Pin,
            Pinned ? Theme.Ok : Theme.Muted);
        DrawButton(g, refresh, Theme.GlyphRefresh, "R", _hover == Zone.Refresh, Theme.Muted);
        DrawButton(g, settings, Theme.GlyphSettings, "S", _hover == Zone.Settings, Theme.Muted);

        // column titles (sticky)
        var cy = S(TitleH);
        using (var pen = new Pen(Theme.Divider))
            g.DrawLine(pen, S(PadX), cy, w - S(PadX), cy);
        var ty = cy + S(5);
        var cw = CellWidth(w);
        Gfx.Text(g, "KONTO", Theme.FCaps, new Rectangle(NameX, ty, NameWidth, S(15)), Theme.Faint);
        Gfx.Text(g, "5 STUNDEN", Theme.FCaps, new Rectangle(CellsX, ty, cw, S(15)), Theme.Faint);
        Gfx.Text(g, "WOCHE", Theme.FCaps, new Rectangle(CellsX + cw + S(CellGap), ty, cw, S(15)), Theme.Faint);

        using (var pen = new Pen(Theme.Divider))
            g.DrawLine(pen, 0, hh - 1, w, hh - 1);
        if (shadow)
        {
            var sr = new Rectangle(0, hh, w, S(10));
            using var lg = new LinearGradientBrush(sr, Color.FromArgb(120, 0, 0, 0), Color.FromArgb(0, 0, 0, 0),
                LinearGradientMode.Vertical);
            g.FillRectangle(lg, sr);
        }
    }

    private void DrawSummary(Graphics g, int x, int y)
    {
        int crit = 0, errors = 0, warn = 0, stale = 0, ok = 0;
        foreach (var r in _rows)
            switch (r.State)
            {
                case RowState.Crit: crit++; break;
                case RowState.Error: errors++; break;
                case RowState.Warn: warn++; break;
                case RowState.Stale: stale++; break;
                case RowState.Ok: ok++; break;
            }

        var parts = new List<(string text, Color color)> { ($"{_rows.Count} Konten", Theme.Muted) };
        if (crit > 0) parts.Add(($"{crit} kritisch", Theme.Crit));
        if (errors > 0) parts.Add(($"{errors} Fehler", Theme.Crit));
        if (warn > 0) parts.Add(($"{warn} knapp", Theme.Warn));
        if (stale > 0) parts.Add(($"{stale} veraltet", Theme.Muted));
        if (ok > 0) parts.Add(($"{ok} OK", Theme.Ok));

        var cx = x;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                Gfx.Text(g, "·", Theme.FSmall, new Rectangle(cx + S(5), y, S(10), S(16)), Theme.Faint);
                cx += S(14);
            }
            var tw = Gfx.Measure(g, parts[i].text, Theme.FSmall);
            Gfx.Text(g, parts[i].text, Theme.FSmall, new Rectangle(cx, y, tw + S(2), S(16)), parts[i].color);
            cx += tw;
        }
    }

    private static void DrawButton(Graphics g, Rectangle r, string glyph, string fallback, bool hover, Color color)
    {
        if (hover) Gfx.FillRound(g, Theme.ButtonHover, r, S(7));
        var text = Theme.IconFamily is null ? fallback : glyph;
        Gfx.Text(g, text, Theme.FIcon, r, hover ? Theme.Text : color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawFooter(Graphics g, int w, int h)
    {
        var fh = S(FooterH);
        var top = h - fh;
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillRectangle(bg, 0, top, w, fh);
        using (var pen = new Pen(Theme.Divider))
            g.DrawLine(pen, 0, top, w, top);

        var area = new Rectangle(S(PadX), top, w - 2 * S(PadX), fh);
        Gfx.Text(g, $"Stand {Stamp:HH:mm:ss}", Theme.FTiny, area, Theme.Faint, TextFormatFlags.VerticalCenter);
        var hint = Pinned ? "Angeheftet – Pin oder Tray-Icon zum Lösen" : "Klick = neu messen · Maus drüber = Details";
        Gfx.Text(g, hint, Theme.FTiny, area, Pinned ? Theme.Ok : Theme.Faint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    private void DrawScrollbar(Graphics g, Rectangle body)
    {
        if (MaxScroll(body.Height) <= 0) return;
        var (thumb, _, _, _) = ThumbGeometry(body);
        var active = _hover == Zone.Scrollbar || _dragging;
        Gfx.FillRound(g, Color.FromArgb(active ? 120 : 64, 205, 210, 220), thumb, thumb.Width / 2f);
    }

    private void DrawRow(Graphics g, RowInfo row, int y, int w, DateTimeOffset now, bool hover, bool last)
    {
        var v = row.View;
        var bgRect = new RectangleF(S(6), y + S(2), w - S(12), row.H - S(4));
        var tint = row.State switch
        {
            RowState.Crit or RowState.Error => Color.FromArgb(20, Theme.Crit),
            RowState.Warn => Color.FromArgb(16, Theme.Warn),
            _ => Color.Empty
        };
        if (!tint.IsEmpty) Gfx.FillRound(g, tint, bgRect, S(8));
        if (hover) Gfx.FillRound(g, Color.FromArgb(16, 255, 255, 255), bgRect, S(8));

        // provider stripe
        var stripeColor = row.Stale || row.State == RowState.Pending ? Theme.Stale : Theme.ProviderColor(v.Config.Provider);
        Gfx.FillRound(g, stripeColor, new RectangleF(S(PadX), y + S(11), S(3), S(RowBase) - S(22)), S(1.5f));

        // name + status
        Gfx.Text(g, v.Config.Name, Theme.FName, new Rectangle(NameX, y + S(9), NameWidth, S(19)),
            row.Stale ? Theme.Muted : Theme.Text);
        var sy = y + S(30);
        Gfx.Text(g, row.StatusText, Theme.FSmall, new Rectangle(NameX, sy, NameWidth, S(16)), row.StatusColor);
        if (v.Polling)
        {
            var dx = Math.Min(NameX + Gfx.Measure(g, row.StatusText, Theme.FSmall) + S(7), NameX + NameWidth - S(4));
            Gfx.FillRound(g, Theme.Muted, new RectangleF(dx, sy + S(6), S(4), S(4)), S(2));
        }

        var cw = CellWidth(w);
        var cellsW = CellsWidth(w);
        switch (row.Kind)
        {
            case RowKind.Loading:
                Gfx.Text(g, "Erste Abfrage läuft …", Theme.FSmall, new Rectangle(CellsX, y + S(18), cellsW, S(16)), Theme.Muted);
                break;
            case RowKind.Error:
                TextRenderer.DrawText(g, ShortError(v.Snapshot?.Error), Theme.FSmall,
                    new Rectangle(CellsX, y + S(10), cellsW, S(36)), Theme.Crit,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
                break;
            default:
                if (row.Layout.Span is { } span)
                    DrawSpanCells(g, row, span, y, cw, now);
                else
                {
                    DrawQuotaCell(g, row, row.Layout.Short, ShortColumn, CellsX, y, cw, now);
                    DrawQuotaCell(g, row, row.Layout.Long, LongColumn, CellsX + cw + S(CellGap), y, cw, now);
                }
                break;
        }

        var ey = y + S(RowBase) - S(2);
        if (row.Kind == RowKind.Data)
        {
            foreach (var extra in row.Layout.Extras)
            {
                DrawExtra(g, row, extra, ey, w, now);
                ey += S(ExtraH);
            }
            if (VerdictFor(row, now) is { } verdict)
                DrawVerdict(g, verdict.text, verdict.color, ey, w);
        }

        if (!last)
            using (var pen = new Pen(Theme.Divider))
                g.DrawLine(pen, S(PadX), y + row.H - 1, w - S(PadX), y + row.H - 1);
    }

    private void DrawVerdict(Graphics g, string text, Color color, int y, int w)
    {
        var x = NameX;
        if (Theme.IconFamily is not null)
        {
            Gfx.Text(g, Theme.GlyphWarning, Theme.FIconSmall, new Rectangle(x, y + S(2), S(14), S(16)), color,
                TextFormatFlags.VerticalCenter);
            x += S(18);
        }
        Gfx.Text(g, text, Theme.FSmall, new Rectangle(x, y + S(2), w - x - S(PadX) - S(6), S(16)), color);
    }

    /// <summary>Value left, reset countdown right, bar below. Shows a label only if the window
    /// differs from what the column header promises.</summary>
    private void DrawQuotaCell(Graphics g, RowInfo row, QuotaWindow? win, string column, int x, int y, int cw,
        DateTimeOffset now)
    {
        if (win is null)
        {
            Gfx.Text(g, "—", Theme.FSmall, new Rectangle(x, y + S(11), cw, S(16)), Theme.Faint);
            return;
        }
        row.View.Evals.TryGetValue(win.Key, out var ev);
        var health = ev?.Health ?? Health.Unknown;
        var color = row.Stale ? Theme.Stale : Theme.ForHealth(health);
        var valueColor = row.Stale ? Theme.Stale : health is Health.Warn or Health.Crit ? color : Theme.Text;

        var value = Formatting.Percent(win.UsedFraction);
        var vw = Gfx.Measure(g, value, Theme.FValue);
        Gfx.Text(g, value, Theme.FValue, new Rectangle(x, y + S(9), vw + S(4), S(19)), valueColor);

        // right: countdown with clock glyph
        var right = x + cw;
        string countdown;
        if (win.ResetAtUtc is { } reset && reset > now)
            countdown = Formatting.Span(reset - now);
        else if (win.Remaining is not null && win.Total is not null)
            countdown = $"noch {Formatting.Count(win.Remaining)}/{Formatting.Count(win.Total)}";
        else
            countdown = "";
        if (countdown.Length > 0)
        {
            var tw = Gfx.Measure(g, countdown, Theme.FTiny);
            Gfx.Text(g, countdown, Theme.FTiny, new Rectangle(right - tw - S(1), y + S(12), tw + S(2), S(15)), Theme.Muted);
            right -= tw + S(4);
            if (win.ResetAtUtc is not null && Theme.IconFamily is not null)
            {
                var iw = S(13);
                Gfx.Text(g, Theme.GlyphClock, Theme.FIconSmall, new Rectangle(right - iw, y + S(12), iw, S(15)),
                    Theme.Faint, TextFormatFlags.VerticalCenter);
                right -= iw + S(2);
            }
        }

        // label only when this window is not what the column says (e.g. "Requests", "Sonnet")
        var label = WindowLayout.ShortLabel(win);
        if (!string.Equals(label, column, StringComparison.OrdinalIgnoreCase))
        {
            var lx = x + vw + S(8);
            if (right - lx > S(16))
                Gfx.Text(g, label, Theme.FTiny, new Rectangle(lx, y + S(12), right - lx - S(4), S(15)), Theme.Faint);
        }

        Gfx.Bar(g, new RectangleF(x, y + S(34), cw, S(5)), win.UsedFraction ?? 0, color, Theme.Track);
    }

    /// <summary>Balance/info accounts reuse the two cells: amount | runway.</summary>
    private void DrawSpanCells(Graphics g, RowInfo row, QuotaWindow span, int y, int cw, DateTimeOffset now)
    {
        row.View.Evals.TryGetValue(span.Key, out var ev);
        var x1 = CellsX;
        var x2 = CellsX + cw + S(CellGap);
        var textColor = row.Stale ? Theme.Stale : Theme.Text;

        // cell 1: amount
        var amount = span.Remaining is null ? "–" : Formatting.Money(span.Remaining, span.Unit);
        Gfx.Text(g, amount, Theme.FValue, new Rectangle(x1, y + S(9), cw, S(19)), textColor);
        Gfx.Text(g, span.IsInfoOnly ? span.Label : "Guthaben", Theme.FTiny, new Rectangle(x1, y + S(12), cw, S(15)),
            Theme.Faint, TextFormatFlags.Right);
        if (!string.IsNullOrEmpty(span.Note))
            Gfx.Text(g, span.Note, Theme.FTiny, new Rectangle(x1, y + S(30), cw + S(CellGap) + cw, S(15)), Theme.Faint);

        if (span.IsInfoOnly)
        {
            Gfx.Text(g, "—", Theme.FSmall, new Rectangle(x2, y + S(11), cw, S(16)), Theme.Faint);
            return;
        }

        // cell 2: runway
        string runway;
        Color runwayColor;
        if (ev is { HasRate: true, TimeToEmpty: { } tte })
        {
            var left = tte - (now - (row.Display?.AtUtc ?? now));
            runway = "~" + Formatting.Span(left < TimeSpan.Zero ? TimeSpan.Zero : left);
            runwayColor = row.Stale ? Theme.Stale : ev.Health is Health.Warn or Health.Crit ? Theme.ForHealth(ev.Health) : Theme.Text;
        }
        else
        {
            runway = "—";
            runwayColor = Theme.Faint;
        }
        Gfx.Text(g, runway, Theme.FValue, new Rectangle(x2, y + S(9), cw, S(19)), runwayColor);
        Gfx.Text(g, "Reichweite", Theme.FTiny, new Rectangle(x2, y + S(12), cw, S(15)), Theme.Faint, TextFormatFlags.Right);
        if (string.IsNullOrEmpty(span.Note))
            Gfx.Text(g, ev is { HasRate: true } ? "bei aktuellem Verbrauch" : "kein Verbrauch gemessen", Theme.FTiny,
                new Rectangle(x2, y + S(30), cw, S(15)), Theme.Faint);
    }

    /// <summary>Additional window: label under the name column, value across the cells.</summary>
    private void DrawExtra(Graphics g, RowInfo row, QuotaWindow win, int y, int w, DateTimeOffset now)
    {
        Gfx.Text(g, win.IsBalance ? "Guthaben" : WindowLayout.ShortLabel(win), Theme.FTiny,
            new Rectangle(NameX, y + S(2), NameWidth, S(15)), Theme.Muted);
        var x = CellsX;
        var width = CellsWidth(w);
        if (win.IsInfoOnly)
        {
            Gfx.Text(g, win.Note ?? "", Theme.FTiny, new Rectangle(x, y + S(2), width, S(15)), Theme.Faint);
        }
        else if (win.IsBalance)
        {
            Gfx.Text(g, Formatting.Money(win.Remaining, win.Unit), Theme.FTiny, new Rectangle(x, y + S(2), width, S(15)), Theme.Text);
        }
        else
        {
            row.View.Evals.TryGetValue(win.Key, out var ev);
            var color = row.Stale ? Theme.Stale : Theme.ForHealth(ev?.Health ?? Health.Unknown);
            Gfx.Bar(g, new RectangleF(x, y + S(8), S(70), S(4)), win.UsedFraction ?? 0, color, Theme.Track);
            var text = Formatting.Percent(win.UsedFraction);
            if (win.Remaining is not null && win.Total is not null)
                text += $" · noch {Formatting.Count(win.Remaining)} von {Formatting.Count(win.Total)}";
            if (win.ResetAtUtc is { } reset && reset > now)
                text += " · Reset in " + Formatting.Span(reset - now);
            Gfx.Text(g, text, Theme.FTiny, new Rectangle(x + S(78), y + S(2), width - S(78), S(15)), Theme.Muted);
        }
    }

    // ================================================================== tooltip

    private static string TooltipText(RowInfo row)
    {
        var v = row.View;
        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"{v.Config.Name}  ·  {ProviderInfo.DisplayName(v.Config.Provider)}");
        var snap = row.Display;
        if (snap is not null)
        {
            foreach (var w in snap.Windows)
            {
                v.Evals.TryGetValue(w.Key, out var ev);
                var value = w.IsBalance || w.IsInfoOnly
                    ? Formatting.Money(w.Remaining, w.Unit)
                    : Formatting.Percent(w.UsedFraction) + " verbraucht";
                if (!w.IsBalance && !w.IsInfoOnly && w.Remaining is not null && w.Total is not null)
                    value += $" (noch {Formatting.Count(w.Remaining)} von {Formatting.Count(w.Total)} {w.Unit})";
                sb.Append($"• {w.Label}: {value}");
                if (w.ResetAtUtc is { } r)
                    sb.Append($" · Reset {r.ToLocalTime():ddd dd.MM. HH:mm} (in {Formatting.Span(r - now)})");
                sb.AppendLine();
                if (ev is not null && !w.IsInfoOnly)
                    sb.AppendLine("    " + (ev.HasRate || ev.Health != Health.Ok
                        ? LiveVerdict(w, ev, snap.AtUtc, now)
                        : "kein Verbrauch gemessen"));
                if (!string.IsNullOrEmpty(w.Note))
                    sb.AppendLine($"    {w.Note}");
            }
        }
        if (v.Snapshot is { Ok: false } failed)
            sb.AppendLine($"Letzte Abfrage fehlgeschlagen: {ShortError(failed.Error)}");
        if (v.LastGood is { } good)
            sb.AppendLine($"Letzte gültige Werte: {good.AtUtc.ToLocalTime():HH:mm:ss}");
        sb.Append("Klick = jetzt neu messen");
        return sb.ToString();
    }

    private void ShowTip()
    {
        _tipTimer.Stop();
        if (_hover != Zone.Row || _hoverRow < 0 || _hoverRow >= _rows.Count || !IsHandleCreated) return;
        _tipText = TooltipText(_rows[_hoverRow]);
        _tip.Show(_tipText, this, new Point(_lastMouse.X + S(14), _lastMouse.Y + S(20)), 30000);
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        if (IsHandleCreated) _tip.Hide(this);
    }

    private void OnTipPopup(object? sender, PopupEventArgs e)
    {
        var sz = TextRenderer.MeasureText(_tipText, Theme.FSmall, new Size(S(460), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        e.ToolTipSize = new Size(sz.Width + S(24), sz.Height + S(18));
    }

    private void OnTipDraw(object? sender, DrawToolTipEventArgs e)
    {
        using (var bg = new SolidBrush(Theme.Card))
            e.Graphics.FillRectangle(bg, e.Bounds);
        using (var pen = new Pen(Theme.CardBorder))
            e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        var r = new Rectangle(S(12), S(9), e.Bounds.Width - S(24), e.Bounds.Height - S(18));
        TextRenderer.DrawText(e.Graphics, _tipText, Theme.FSmall, r, Theme.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    // ================================================================== input

    private void AnimateScroll()
    {
        var d = _scrollTarget - _scroll;
        if (Math.Abs(d) < 0.6f)
        {
            _scroll = _scrollTarget;
            _anim.Stop();
        }
        else
        {
            _scroll += d * 0.3f;
        }
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var max = MaxScroll(BodyRect(ClientSize).Height);
        if (max <= 0) return;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta / 120f * S(64), 0, max);
        HideTip();
        _anim.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitTest(e.Location, out _pressedRow);
        if (_pressed != Zone.Scrollbar) return;

        var body = BodyRect(ClientSize);
        var (thumb, trackH, thumbH, max) = ThumbGeometry(body);
        if (!thumb.Contains(e.Location))
        {
            var rel = (e.Y - body.Top - S(4) - thumbH / 2f) / Math.Max(1f, trackH - thumbH);
            _scroll = _scrollTarget = Math.Clamp(rel * max, 0, max);
        }
        _dragging = true;
        _dragStartY = e.Y;
        _dragStartScroll = _scroll;
        Capture = true;
        HideTip();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _lastMouse = e.Location;
        if (_dragging)
        {
            var body = BodyRect(ClientSize);
            var (_, trackH, thumbH, max) = ThumbGeometry(body);
            var ratio = max / Math.Max(1f, trackH - thumbH);
            _scroll = _scrollTarget = Math.Clamp(_dragStartScroll + (e.Y - _dragStartY) * ratio, 0, max);
            Invalidate();
            return;
        }

        var zone = HitTest(e.Location, out var row);
        if (zone == _hover && row == _hoverRow) return;
        _hover = zone;
        _hoverRow = zone == Zone.Row ? row : -1;
        Cursor = zone is Zone.Row or Zone.Refresh or Zone.Settings or Zone.Pin ? Cursors.Hand : Cursors.Default;
        HideTip();
        if (_hoverRow >= 0) _tipTimer.Start();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        var zone = HitTest(e.Location, out var row);
        if (zone != _pressed) return;
        _pressed = Zone.None;
        switch (zone)
        {
            case Zone.Refresh: RefreshAllClicked?.Invoke(); break;
            case Zone.Settings: SettingsClicked?.Invoke(); break;
            case Zone.Pin: PinClicked?.Invoke(); break;
            case Zone.Row when row == _pressedRow && row >= 0 && row < _rows.Count:
                RowClicked?.Invoke(_rows[row].View.Config.Id);
                break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ResetHover();
    }

    public void ResetHover()
    {
        _hover = Zone.None;
        _hoverRow = -1;
        HideTip();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _tipTimer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
