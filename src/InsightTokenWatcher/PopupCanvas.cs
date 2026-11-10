using System.Drawing.Drawing2D;
using System.Text;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher;

/// <summary>
/// The whole popup as ONE owner-drawn surface: fixed header with column titles, scrollable
/// list of account cards, fixed footer. No child controls → no handle churn, no AutoScroll
/// drift, smooth custom scrolling. Columns: account | short window (5 h) | long window (week)
/// and – when an account reports one – the model-scoped week (e.g. Fable). Balance accounts
/// use the first two cells (amount | runway). Details live in a tooltip.
/// </summary>
internal sealed class PopupCanvas : Control
{
    /// <summary>Logical width: wider once the model column is shown.</summary>
    public float LogicalWidth => _modelScope is null ? 480 : 560;

    private const float TitleH = 58, LegendH = 19, ColumnsH = 26, FooterH = 28;
    private const float RowBase = 48, RowGap = 4, VerdictH = 20, ExtraH = 20;
    private const float OuterX = 8, CardPadX = 12, NameW = 128, NameGap = 12, CellGap = 16, ModelW = 92;
    private const float BodyPadTop = 0, BodyPadBottom = 8, ButtonSize = 28;

    private const string ShortColumn = "5 h";
    private const string LongColumn = "Woche";

    private enum Zone { None, Row, Refresh, Settings, Pin, Scrollbar, Header, SortName, SortShort, SortLong, SortModel }
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
    private string? _modelScope;   // model family of the third column ("Fable"); null = no such column
    private bool _hasPace;         // a bar carries a pace tick → the legend row is shown

    /// <summary>Title, summary and – with pace ticks – the legend; the column titles follow.</summary>
    private float TitleBandH => TitleH + (_hasPace ? LegendH : 0);

    /// <summary>Height of the band the popup is dragged by, in pixels.</summary>
    public int TitleBandHeight => S(TitleBandH);
    private float HeaderH => TitleBandH + ColumnsH;
    private int _contentH;
    private float _scroll, _scrollTarget;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 550 };
    private readonly ToolTip _tip;
    private string _tipText = "";
    private Zone _hover = Zone.None, _pressed = Zone.None;
    private bool _moving;          // title-band drag: moves the whole popup (auto-pins)
    private bool _moveEngaged;     // past the drag threshold
    private Point _moveOffset;     // mouse position within the form at drag start
    private int _hoverRow = -1, _pressedRow = -1;
    private bool _dragging;
    private int _dragStartY;
    private float _dragStartScroll;
    private Point _lastMouse;

    public bool Pinned { get; set; }
    public bool IsDragging => _dragging || _moving;

    /// <summary>Fired after the user dragged the popup by its title band; argument is the new form location.</summary>
    public event Action<Point>? PopupMoved;
    public bool DrawOwnBorder { get; set; } = true;
    public DateTime Stamp { get; private set; } = DateTime.Now;

    public event Action? RefreshAllClicked;
    public event Action? SettingsClicked;
    public event Action? PinClicked;
    public event Action<Guid>? RowClicked;

    /// <summary>Fired after a click on a column title changed the order (to persist it).</summary>
    public event Action<SortColumn, bool>? SortChanged;

    /// <summary>Current order (a click on a column title cycles ascending → descending → default).</summary>
    public SortColumn SortBy { get; private set; }
    public bool SortDescending { get; private set; }

    public void SetSort(SortColumn column, bool descending)
    {
        SortBy = column;
        SortDescending = column != SortColumn.None && descending;
        BuildRows();
        SyncHoverAfterReorder(null);
        Invalidate();
    }

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
        var hovered = HoveredAccount();
        _views = views;
        Stamp = DateTime.Now;
        BuildRows();
        var max = MaxScroll(BodyRect(ClientSize).Height);
        _scroll = Math.Clamp(_scroll, 0, max);
        _scrollTarget = Math.Clamp(_scrollTarget, 0, max);
        if (_hoverRow >= _rows.Count) _hoverRow = -1;
        SyncHoverAfterReorder(hovered);
        Invalidate();
    }

    private Guid? HoveredAccount() =>
        _hover == Zone.Row && _hoverRow >= 0 && _hoverRow < _rows.Count ? _rows[_hoverRow].View.Config.Id : null;

    /// <summary>A sorted order can move another account under the resting mouse: re-hit-test, and
    /// drop the tooltip if it would now describe a different account.</summary>
    private void SyncHoverAfterReorder(Guid? before)
    {
        before ??= HoveredAccount();
        if (_hover != Zone.Row || !IsHandleCreated) return;
        _hover = HitTest(_lastMouse, out var row);
        _hoverRow = _hover == Zone.Row ? row : -1;
        if (HoveredAccount() != before) HideTip();
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

    /// <summary>Current row order (render harness).</summary>
    internal IEnumerable<string> RowNames() => _rows.Select(r => r.View.Config.Name);

    private void BuildRows()
    {
        _rows.Clear();
        var y = 0;
        var now = DateTimeOffset.UtcNow;
        _modelScope = WindowLayout.ColumnScope(_views);
        foreach (var v in AccountSort.Apply(_views, SortBy, SortDescending, now))
        {
            var row = new RowInfo { View = v, Display = v.Display };
            if (row.Display is null)
            {
                row.Kind = v.Snapshot is { Ok: false } ? RowKind.Error : RowKind.Loading;
            }
            else
            {
                row.Kind = RowKind.Data;
                row.Layout = WindowLayout.Classify(row.Display.Windows, _modelScope);
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
            y += h + S(RowGap);
            _rows.Add(row);
        }
        _contentH = _rows.Count == 0 ? S(84) : y - S(RowGap) + S(BodyPadTop) + S(BodyPadBottom);
        _hasPace = _rows.Any(r => r.Kind == RowKind.Data &&
            new[] { r.Layout.Short, r.Layout.Long, r.Layout.Model }.Any(w => w is not null &&
                WindowLayout.ElapsedFraction(w, now) is not null));
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
        // a healthy row stays quiet: what kind of account it is, in grey. Only exceptions get a
        // coloured word – and a model limit names itself ("FABLE LEER") instead of "KNAPP".
        (row.StatusText, row.StatusColor) = row.State switch
        {
            RowState.Ok => (WindowLayout.Subline(v.Config, row.Layout), Theme.PFaint),
            RowState.Warn => (WindowLayout.ModelLimitBehind(v) is { } limit
                ? WindowLayout.ModelLimitStatus(limit)
                : "KNAPP", Theme.PWarn),
            RowState.Crit => ("KRITISCH", Theme.PCrit),
            RowState.Error => ("FEHLER", Theme.PCrit),
            RowState.Stale => v.Failure == FailureKind.AuthExpired
                ? ("Token abgelaufen", Theme.PMuted)
                : ("offline", Theme.PWarn),
            _ => ("warte …", Theme.PMuted)
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
                FailureKind.AuthExpired => ($"Token abgelaufen – letzte Werte von {at:HH:mm}", Theme.PMuted),
                FailureKind.Network => ($"Keine Verbindung – letzte Werte von {at:HH:mm}", Theme.PWarn),
                _ => ($"Abfrage fehlgeschlagen ({ShortError(v.Snapshot?.Error)}) – Werte von {at:HH:mm}", Theme.PCrit)
            };
        }

        // The line explains the row's status, which follows the general windows. A model limit
        // (Fable) only gets the line when no general window has something to say.
        QuotaWindow? worstWin = null;
        WindowEvaluation? worst = null;
        foreach (var scoped in new[] { false, true })
        {
            foreach (var w in display.Windows)
            {
                if (w.Scope is not null != scoped) continue;
                if (!v.Evals.TryGetValue(w.Key, out var ev) || ev.Health is not (Health.Warn or Health.Crit)) continue;
                if (worst is null || ev.Health > worst.Health)
                {
                    worst = ev;
                    worstWin = w;
                }
            }
            if (worst is not null) break;
        }
        if (worst is null || worstWin is null) return null;
        var text = LiveVerdict(worstWin, worst, display.AtUtc, now);
        var label = worstWin.IsBalance ? "Guthaben" : WindowLayout.ShortLabel(worstWin);
        return ($"{label}: {text}", Theme.PForHealth(worst.Health));
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
        if (tte < ttr) return $"leer in {Formatting.Span(tte)} – Reset erst in {Formatting.Span(ttr)}!";
        // the tooltip shows this line for healthy windows too – "reicht knapp" for a window that
        // lasts ten times longer than its reset was wrong
        return ev.Health == Health.Warn
            ? $"reicht knapp – leer in {Formatting.Span(tte)}, Reset in {Formatting.Span(ttr)}"
            : $"reicht bis zum Reset – leer erst in {Formatting.Span(tte)}, Reset in {Formatting.Span(ttr)}";
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

    private static int NameX => S(OuterX) + S(CardPadX);
    private static int NameWidth => S(NameW);
    private static int CellsX => NameX + S(NameW) + S(NameGap);
    private static int CellsWidth(int w) => w - CellsX - S(OuterX) - S(CardPadX);

    /// <summary>Column x positions and widths: two equal cells, plus the narrower model cell.</summary>
    private (int x1, int x2, int x3, int cw, int mw) Columns(int w)
    {
        var mw = _modelScope is null ? 0 : S(ModelW);
        var gaps = _modelScope is null ? S(CellGap) : 2 * S(CellGap);
        var cw = (CellsWidth(w) - gaps - mw) / 2;
        var x2 = CellsX + cw + S(CellGap);
        return (CellsX, x2, x2 + cw + S(CellGap), cw, mw);
    }

    private (Rectangle pin, Rectangle refresh, Rectangle settings) HeaderButtons(int w)
    {
        var bs = S(ButtonSize);
        var y = S(9); // on the title line – the summary below gets the full width
        var settings = new Rectangle(w - S(OuterX) - S(4) - bs, y, bs, bs);
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
        // in the gutter right of the cards
        return (new Rectangle(body.Right - S(2) - tw, thumbY, tw, thumbH), trackH, thumbH, max);
    }

    private Zone HitTest(Point p, out int row)
    {
        row = -1;
        var (pin, refresh, settings) = HeaderButtons(ClientSize.Width);
        if (pin.Contains(p)) return Zone.Pin;
        if (refresh.Contains(p)) return Zone.Refresh;
        if (settings.Contains(p)) return Zone.Settings;

        var body = BodyRect(ClientSize);
        if (p.Y >= S(TitleBandH) && p.Y < S(HeaderH)) // column titles: click sorts
        {
            var (x1, x2, x3, _, mw) = Columns(ClientSize.Width);
            var half = S(CellGap) / 2;
            if (p.X < x1 - half) return Zone.SortName;
            if (p.X < x2 - half) return Zone.SortShort;
            return mw == 0 || p.X < x3 - half ? Zone.SortLong : Zone.SortModel;
        }
        if (!body.Contains(p)) return p.Y < S(HeaderH) ? Zone.Header : Zone.None;
        if (MaxScroll(body.Height) > 0 && p.X >= body.Right - S(OuterX)) return Zone.Scrollbar;

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

    // overlays on a card: amber tint for problem rows (the base amber on purpose – the tint
    // must not change with the glass palette), a white veil for the hovered row
    private static readonly Color TintStrong = Color.FromArgb(30, Theme.Warn);
    private static readonly Color TintSoft = Color.FromArgb(20, Theme.Warn);
    private static readonly Color HoverVeil = Color.FromArgb(14, 255, 255, 255);
    private static readonly Color GlassEdge = Color.FromArgb(22, 255, 255, 255); // light rim of a card on glass

    private bool _glass;
    private GlassSurface? _surface;

    private static Color Over(Color veil, Color under) => Color.FromArgb(
        under.R + (veil.R - under.R) * veil.A / 255, under.G + (veil.G - under.G) * veil.A / 255,
        under.B + (veil.B - under.B) * veil.A / 255);

    /// <summary>Every surface colour the popup paints, with its opacity on glass.</summary>
    private static GlassSurface NewSurface()
    {
        var soft = Over(TintSoft, Theme.Card);
        var strong = Over(TintStrong, Theme.Card);
        return new GlassSurface(new[]
        {
            // neutral: shadow → pane → card → hovered card / button → bar track
            new (Color, float)[]
            {
                (Color.Black, Glass.PaneAlpha), (Theme.Bg, Glass.PaneAlpha), (Theme.Card, Glass.CardAlpha),
                (Over(HoverVeil, Theme.Card), Glass.CardAlpha), (Theme.TrackOnCard, Glass.CardAlpha)
            },
            // warm: pane → tinted cards → hovered tinted card
            new (Color, float)[]
            {
                (Theme.Bg, Glass.PaneAlpha), (soft, Glass.TintSoftAlpha), (strong, Glass.TintStrongAlpha),
                (Over(HoverVeil, strong), Glass.TintStrongAlpha)
            }
        });
    }

    /// <summary>Translucent surfaces over the DWM backdrop (set by the form once the backdrop is on).</summary>
    public bool GlassActive
    {
        get => _glass;
        set
        {
            _glass = value;
            Theme.GlassPalette = value;
            // the glass path brings its own buffer and must reach the window DC unaltered
            SetStyle(ControlStyles.OptimizedDoubleBuffer, !value);
            BuildRows(); // status colours are part of the rows
            Invalidate();
        }
    }

    /// <summary>The opacities changed (brighter or darker desktop behind the popup): new alpha table.</summary>
    public void GlassAlphasChanged()
    {
        _surface?.Dispose();
        _surface = null;
        if (_glass) Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!_glass) base.OnPaintBackground(e); // glass: an opaque erase would flash before the frame
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_glass || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            Render(e.Graphics, ClientSize);
            return;
        }
        _surface ??= NewSurface();
        using (var g = _surface.Begin(ClientSize))
            Render(g, ClientSize);
        var hdc = e.Graphics.GetHdc();
        try
        {
            _surface.Present(hdc);
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    /// <summary>The glass frame as premultiplied ARGB (render harness: composes it over a test backdrop).</summary>
    /// <summary>Opacity per surface as the alpha table really assigns it (render harness).</summary>
    internal string GlassAlphaReport()
    {
        _surface ??= NewSurface();
        var items = new (string name, Color color, float want)[]
        {
            ("pane", Theme.Bg, Glass.PaneAlpha), ("card", Theme.Card, Glass.CardAlpha),
            ("card-hover", Over(HoverVeil, Theme.Card), Glass.CardAlpha),
            ("tint-soft", Over(TintSoft, Theme.Card), Glass.TintSoftAlpha),
            ("tint-strong", Over(TintStrong, Theme.Card), Glass.TintStrongAlpha),
            ("track", Theme.TrackOnCard, Glass.CardAlpha), ("text", Theme.Text, 1f), ("faint", Theme.PFaint, 1f),
            ("stale", Theme.PStale, 1f), ("crit", Theme.PCrit, 1f), ("warn", Theme.PWarn, 1f), ("ok", Theme.Ok, 1f)
        };
        var wrong = items.Where(i => Math.Abs(_surface.AlphaOf(i.color) - i.want * 255) > 4).Select(i => i.name).ToList();
        return string.Join(" ", items.Select(i => $"{i.name}={_surface.AlphaOf(i.color)}")) +
               $" wrong=[{string.Join(", ", wrong)}] (expect none)";
    }

    internal int[] RenderGlassFrame(Size size)
    {
        _surface ??= NewSurface();
        using (var g = _surface.Begin(size))
            Render(g, size);
        return _surface.Frame();
    }

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
                Theme.PMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var top = body.Top + S(BodyPadTop) - (int)Math.Round(_scroll);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var y = top + row.Y;
            if (y > body.Bottom || y + row.H < body.Top) continue;
            DrawRow(g, row, y, w, now, i == _hoverRow && _hover == Zone.Row);
        }
        g.Restore(state);

        DrawHeader(g, w, _scroll > 0.5f);
        DrawFooter(g, w, sz.Height);
        DrawScrollbar(g, body);
        if (_glass) // light catching the top edge of the pane
        {
            using var rim = new Pen(Color.FromArgb(36, 255, 255, 255));
            g.DrawLine(rim, S(12), 1.5f, w - S(12), 1.5f);
        }
        if (DrawOwnBorder)
            Gfx.DrawRound(g, Theme.CardBorder, new RectangleF(0.5f, 0.5f, w - 1, sz.Height - 1), S(10));
    }

    private void DrawHeader(Graphics g, int w, bool shadow)
    {
        var hh = S(HeaderH);
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillRectangle(bg, 0, 0, w, hh);

        // brand mark (the tray gauge without a number)
        var mark = new RectangleF(NameX + S(1), S(14), S(17), S(17));
        using (var track = new Pen(Theme.TrackOnCard, S(2.8f)))
            g.DrawEllipse(track, mark);
        using (var arc = new Pen(Theme.Ok, S(2.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, mark, -90, 250);

        var (pin, refresh, settings) = HeaderButtons(w);
        var textX = NameX + S(28);
        Gfx.Text(g, "Insight Token Watcher", Theme.FTitle, new Rectangle(textX, S(11), pin.Left - textX - S(8), S(22)), Theme.Text);
        DrawSummary(g, textX, S(38), w - NameX);

        DrawButton(g, pin, Pinned ? Theme.GlyphPinned : Theme.GlyphPin, "P", _hover == Zone.Pin,
            Pinned ? Theme.Ok : Theme.PMuted);
        DrawButton(g, refresh, Theme.GlyphRefresh, "R", _hover == Zone.Refresh, Theme.PMuted);
        DrawButton(g, settings, Theme.GlyphSettings, "S", _hover == Zone.Settings, Theme.PMuted);

        // column titles (sticky) – a click sorts by that column
        if (_hasPace) DrawPaceLegend(g, textX, S(TitleH) - S(2), w - NameX);

        var ty = S(TitleBandH) + S(3);
        var (x1, x2, x3, cw, mw) = Columns(w);
        DrawColumnTitle(g, "KONTO", SortColumn.Name, Zone.SortName, new Rectangle(NameX, ty, NameWidth, S(15)));
        DrawColumnTitle(g, "5 STUNDEN", SortColumn.Short, Zone.SortShort, new Rectangle(x1, ty, cw, S(15)));
        DrawColumnTitle(g, "WOCHE", SortColumn.Long, Zone.SortLong, new Rectangle(x2, ty, cw, S(15)));
        if (mw > 0)
            DrawColumnTitle(g, _modelScope!.ToUpperInvariant(), SortColumn.Model, Zone.SortModel,
                new Rectangle(x3, ty, mw, S(15)));

        if (shadow)
        {
            var sr = new Rectangle(0, hh, w, S(10));
            using var lg = new LinearGradientBrush(sr, Color.FromArgb(120, 0, 0, 0), Color.FromArgb(0, 0, 0, 0),
                LinearGradientMode.Vertical);
            g.FillRectangle(lg, sr);
        }
    }

    /// <summary>Column title. The sort column is highlighted with a direction triangle (▲ ascending)
    /// and – if it fits – what the order means ("zuerst leer", "A–Z").</summary>
    private void DrawColumnTitle(Graphics g, string title, SortColumn column, Zone zone, Rectangle r)
    {
        var active = SortBy == column;
        Gfx.Text(g, title, Theme.FCaps, r, active ? Theme.Text : _hover == zone ? Theme.PMuted : Theme.PFaint);
        if (!active) return;

        var x = r.X + Gfx.Measure(g, title, Theme.FCaps) + S(6);
        float tw = S(8), th = S(5), mid = r.Y + S(7);
        PointF[] tri = SortDescending
            ? new[] { new PointF(x, mid - th / 2), new PointF(x + tw, mid - th / 2), new PointF(x + tw / 2, mid + th / 2) }
            : new[] { new PointF(x, mid + th / 2), new PointF(x + tw, mid + th / 2), new PointF(x + tw / 2, mid - th / 2) };
        using (var b = new SolidBrush(Theme.Ok))
            g.FillPolygon(b, tri);

        var meaning = SortMeaning(column, SortDescending);
        var sx = (int)(x + tw) + S(6);
        var sw = Gfx.Measure(g, meaning, Theme.FTiny);
        if (sx + sw <= r.Right)
            Gfx.Text(g, meaning, Theme.FTiny, new Rectangle(sx, r.Y, sw + S(2), r.Height), Theme.PMuted);
    }

    private static string SortMeaning(SortColumn column, bool descending) => column == SortColumn.Name
        ? descending ? "Z–A" : "A–Z"
        : descending ? "zuletzt leer" : "zuerst leer";

    /// <summary>"13 Konten" plus one entry per state that occurs: a coloured dot carries the
    /// state, the text stays grey – five coloured words read as a colour band. Entries that
    /// would not fit the width are left out, least urgent first.</summary>
    private void DrawSummary(Graphics g, int x, int y, int limit)
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

        var total = $"{_rows.Count} Konten";
        var cx = x;
        var tw = Gfx.Measure(g, total, Theme.FSmall);
        Gfx.Text(g, total, Theme.FSmall, new Rectangle(cx, y, tw + S(2), S(16)), Theme.PMuted);
        cx += tw;

        var parts = new List<(string text, Color dot)>();
        if (crit > 0) parts.Add(($"{crit} kritisch", Theme.PCrit));
        if (errors > 0) parts.Add(($"{errors} Fehler", Theme.PCrit));
        if (warn > 0) parts.Add(($"{warn} knapp", Theme.PWarn));
        if (stale > 0) parts.Add(($"{stale} veraltet", Theme.PStale));
        if (ok > 0) parts.Add(($"{ok} OK", Theme.Ok));
        foreach (var (text, dot) in parts)
        {
            tw = Gfx.Measure(g, text, Theme.FSmall);
            if (cx + S(22) + tw > limit) break;
            cx += S(12);
            using (var b = new SolidBrush(dot))
                g.FillEllipse(b, cx, y + S(5.5f), S(6), S(6));
            cx += S(10);
            Gfx.Text(g, text, Theme.FSmall, new Rectangle(cx, y, tw + S(2), S(16)), Theme.PMuted);
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

        var area = new Rectangle(NameX, top, w - 2 * NameX, fh);
        Gfx.Text(g, $"Stand {Stamp:HH:mm:ss}", Theme.FTiny, area, Theme.PFaint, TextFormatFlags.VerticalCenter);
        var sortHover = _hover is Zone.SortName or Zone.SortShort or Zone.SortLong or Zone.SortModel;
        var hint = _hover switch
        {
            Zone.SortName => "Klick sortiert: A–Z → Z–A → Standard",
            Zone.SortShort or Zone.SortLong or Zone.SortModel => "Klick sortiert: zuerst leer → zuletzt leer → Standard",
            _ when Pinned => "Angeheftet – Pin oder Tray-Icon zum Lösen",
            _ => "Titel ziehen = parken · Spaltenkopf = sortieren"
        };
        Gfx.Text(g, hint, Theme.FTiny, area, Pinned && !sortHover ? Theme.Ok : Theme.PFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    private void DrawScrollbar(Graphics g, Rectangle body)
    {
        if (MaxScroll(body.Height) <= 0) return;
        var (thumb, _, _, _) = ThumbGeometry(body);
        var active = _hover == Zone.Scrollbar || _dragging;
        Gfx.FillRound(g, Color.FromArgb(active ? 120 : 64, 205, 210, 220), thumb, thumb.Width / 2f);
    }

    /// <summary>One account = one card. Every row has the same shape; a problem only changes the
    /// tint (amber, Dominic 28.10.2026: also for critical/error – red stays for text and bars).</summary>
    private void DrawRow(Graphics g, RowInfo row, int y, int w, DateTimeOffset now, bool hover)
    {
        var v = row.View;
        var card = new RectangleF(S(OuterX), y, w - 2 * S(OuterX), row.H);
        Gfx.FillRound(g, Theme.Card, card, S(8));
        var tint = row.State switch
        {
            RowState.Crit or RowState.Error => TintStrong,
            RowState.Warn => TintSoft,
            _ => Color.Empty
        };
        if (!tint.IsEmpty) Gfx.FillRound(g, tint, card, S(8));
        if (hover) Gfx.FillRound(g, HoverVeil, card, S(8));
        if (_glass) Gfx.DrawRound(g, GlassEdge, RectangleF.Inflate(card, -0.5f, -0.5f), S(8));

        // name + second line (provider, or the state when something is wrong)
        Gfx.Text(g, v.Config.Name, Theme.FName, new Rectangle(NameX, y + S(7), NameWidth, S(18)),
            row.Stale ? Theme.PMuted : Theme.Text);
        var sy = y + S(26);
        Gfx.Text(g, row.StatusText, Theme.FTiny, new Rectangle(NameX, sy, NameWidth, S(15)), row.StatusColor);
        if (v.Polling)
        {
            var dx = Math.Min(NameX + Gfx.Measure(g, row.StatusText, Theme.FTiny) + S(7), NameX + NameWidth - S(4));
            Gfx.FillRound(g, Theme.PMuted, new RectangleF(dx, sy + S(6), S(4), S(4)), S(2));
        }

        var (x1, x2, x3, cw, mw) = Columns(w);
        var cellsW = CellsWidth(w);
        switch (row.Kind)
        {
            case RowKind.Loading:
                Gfx.Text(g, "Erste Abfrage läuft …", Theme.FSmall, new Rectangle(x1, y + S(16), cellsW, S(16)), Theme.PMuted);
                break;
            case RowKind.Error:
                TextRenderer.DrawText(g, ShortError(v.Snapshot?.Error), Theme.FSmall,
                    new Rectangle(x1, y + S(8), cellsW, S(34)), Theme.PCrit,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
                break;
            default:
                if (row.Layout.Span is { } span)
                    DrawSpanCells(g, row, span, y, x1, x2, cw, w - NameX - x2, now);
                else
                {
                    DrawQuotaCell(g, row, row.Layout.Short, ShortColumn, x1, y, cw, now);
                    DrawQuotaCell(g, row, row.Layout.Long, LongColumn, x2, y, cw, now);
                    // an account without a model limit (other provider, API key) leaves the cell empty
                    if (mw > 0 && row.Layout.Model is { } model)
                        DrawQuotaCell(g, row, model, _modelScope!, x3, y, mw, now, row.Layout.Long);
                }
                break;
        }

        var ey = y + S(RowBase) - S(4);
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
        Gfx.Text(g, text, Theme.FSmall, new Rectangle(x, y + S(2), w - x - NameX, S(16)), color);
    }

    /// <summary>Value left, reset countdown right, bar below. Shows a label only if the window
    /// differs from what the column header promises. <paramref name="sameResetAs"/>: the
    /// countdown is left out when it would repeat that window's (the Fable week resets with
    /// the general week).</summary>
    private void DrawQuotaCell(Graphics g, RowInfo row, QuotaWindow? win, string column, int x, int y, int cw,
        DateTimeOffset now, QuotaWindow? sameResetAs = null)
    {
        if (win is null)
        {
            Gfx.Text(g, "—", Theme.FSmall, new Rectangle(x, y + S(10), cw, S(16)), Theme.PFaint);
            return;
        }
        // colour rule (23.10.2026): red = blocked right now (limit reached), amber = in use,
        // lime = unused (0 %) – forecast health does not drive the bar colour
        var frac0 = win.UsedFraction ?? 0;
        var color = row.Stale ? Theme.PStale : frac0 >= 0.999 ? Theme.PCrit : frac0 > 0 ? Theme.PWarn : Theme.Ok;
        var valueColor = row.Stale ? Theme.PStale : color == Theme.Ok ? Theme.Text : color;

        var value = Formatting.PercentShort(win.UsedFraction);
        var vw = Gfx.Measure(g, value, Theme.FValue);
        Gfx.Text(g, value, Theme.FValue, new Rectangle(x, y + S(5), vw + S(4), S(22)), valueColor);

        // right: countdown with clock glyph – only what fits next to the value
        var right = x + cw;
        var room = cw - vw - S(8);
        string countdown;
        if (win.ResetAtUtc is { } reset && reset > now)
            countdown = sameResetAs?.ResetAtUtc is { } other && Math.Abs((other - reset).TotalMinutes) < 5
                ? ""
                : Formatting.Span(reset - now);
        else if (win.Remaining is not null && win.Total is not null)
            countdown = $"noch {Formatting.Count(win.Remaining)}/{Formatting.Count(win.Total)}";
        else
            countdown = "";
        if (countdown.Length > 0)
        {
            var tw = Gfx.Measure(g, countdown, Theme.FTiny);
            if (tw <= room)
            {
                Gfx.Text(g, countdown, Theme.FTiny, new Rectangle(right - tw - S(1), y + S(12), tw + S(2), S(15)), Theme.PMuted);
                right -= tw + S(4);
                room -= tw + S(4);
                var iw = S(13);
                if (win.ResetAtUtc is not null && Theme.IconFamily is not null && room >= iw)
                {
                    Gfx.Text(g, Theme.GlyphClock, Theme.FIconSmall, new Rectangle(right - iw, y + S(12), iw, S(15)),
                        Theme.PFaint, TextFormatFlags.VerticalCenter);
                    right -= iw + S(2);
                }
            }
        }

        // label only when this window is not what the column says (e.g. "Requests")
        var label = WindowLayout.ShortLabel(win);
        if (!string.Equals(label, column, StringComparison.OrdinalIgnoreCase))
        {
            var lx = x + vw + S(8);
            if (right - lx > S(16))
                Gfx.Text(g, label, Theme.FTiny, new Rectangle(lx, y + S(12), right - lx - S(4), S(15)), Theme.PFaint);
        }

        var bar = new RectangleF(x, y + S(32), cw, S(6));
        Gfx.Bar(g, bar, win.UsedFraction ?? 0, color, Theme.TrackOnCard);
        if (WindowLayout.ElapsedFraction(win, now) is { } elapsed)
            DrawPaceTick(g, bar, elapsed, row.Stale ? Theme.PMuted : Theme.Text);
    }

    /// <summary>Pace tick: how much of the window's time has passed. A bar that ends left of it
    /// is below the pace of time. Drawn without anti-aliasing – a 2 px line must stay crisp.</summary>
    private static void DrawPaceTick(Graphics g, RectangleF bar, double elapsed, Color color)
    {
        var tw = Math.Max(2, S(2));
        var over = S(3);
        var x = (int)Math.Round(Math.Clamp(bar.X + bar.Width * elapsed - tw / 2f, bar.X, bar.Right - tw));
        var top = (int)bar.Y - over;
        var height = (int)bar.Height + 2 * over;
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var edge = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) // keeps it visible on a filled bar
            g.FillRectangle(edge, x - 1, top - 1, tw + 2, height + 2);
        using (var b = new SolidBrush(color))
            g.FillRectangle(b, x, top, tw, height);
        g.SmoothingMode = mode;
    }

    /// <summary>One line under the summary that explains the tick; the rule is added if it fits.</summary>
    private static void DrawPaceLegend(Graphics g, int x, int y, int limit)
    {
        var h = S(15);
        Gfx.FillRound(g, Theme.PWarn, new RectangleF(x, y + S(6), S(18), S(5)), S(2.5f));
        x += S(24);
        x += LegendText(g, "Verbrauch", x, y, h, Theme.PMuted) + S(14);
        DrawPaceTick(g, new RectangleF(x, y + S(5), S(2), S(6)), 0, Theme.Text);
        x += S(8);
        x += LegendText(g, "verstrichene Zeit im Fenster", x, y, h, Theme.PMuted) + S(14);
        foreach (var rule in new[] { "Balken vor dem Strich = im Plan", "vor dem Strich = im Plan" })
        {
            var rw = Gfx.Measure(g, rule, Theme.FTiny);
            if (x + rw > limit) continue;
            Gfx.Text(g, rule, Theme.FTiny, new Rectangle(limit - rw - S(1), y, rw + S(2), h), Theme.PFaint);
            break;
        }
    }

    private static int LegendText(Graphics g, string text, int x, int y, int h, Color color)
    {
        var tw = Gfx.Measure(g, text, Theme.FTiny);
        Gfx.Text(g, text, Theme.FTiny, new Rectangle(x, y, tw + S(2), h), color);
        return tw;
    }

    /// <summary>Balance/info accounts use the first two cells: amount | runway. The caption
    /// follows its value ("23,42 $ Guthaben", "~27 d 3 h Reichweite") – right-aligned it
    /// collided with wide values in the narrower cells of the three-column layout.</summary>
    private void DrawSpanCells(Graphics g, RowInfo row, QuotaWindow span, int y, int x1, int x2, int cw, int restWidth,
        DateTimeOffset now)
    {
        row.View.Evals.TryGetValue(span.Key, out var ev);
        var textColor = row.Stale ? Theme.PStale : Theme.Text;

        // cell 1: amount
        var amount = span.Remaining is null ? "–" : Formatting.Money(span.Remaining, span.Unit);
        ValueWithCaption(g, amount, textColor, span.IsInfoOnly ? span.Label : "Guthaben", x1, x2 - S(6), y);
        if (!string.IsNullOrEmpty(span.Note))
            Gfx.Text(g, span.Note, Theme.FTiny, new Rectangle(x1, y + S(27), x2 + cw - x1, S(15)), Theme.PFaint);

        if (span.IsInfoOnly)
        {
            Gfx.Text(g, "—", Theme.FSmall, new Rectangle(x2, y + S(10), cw, S(16)), Theme.PFaint);
            return;
        }

        // cell 2: runway
        string runway;
        Color runwayColor;
        if (ev is { HasRate: true, TimeToEmpty: { } tte })
        {
            var left = tte - (now - (row.Display?.AtUtc ?? now));
            runway = "~" + Formatting.Span(left < TimeSpan.Zero ? TimeSpan.Zero : left);
            runwayColor = row.Stale ? Theme.PStale : ev.Health is Health.Warn or Health.Crit ? Theme.PForHealth(ev.Health) : Theme.Text;
        }
        else
        {
            runway = "—";
            runwayColor = Theme.PFaint;
        }
        ValueWithCaption(g, runway, runwayColor, "Reichweite", x2, x2 + restWidth, y);
        if (string.IsNullOrEmpty(span.Note))
            Gfx.Text(g, ev is { HasRate: true } ? "bei aktuellem Verbrauch" : "kein Verbrauch", Theme.FTiny,
                new Rectangle(x2, y + S(27), restWidth, S(15)), Theme.PFaint);
    }

    /// <summary>Value in the value font, its caption small and grey right behind it (left out
    /// when it would pass <paramref name="limitRight"/>).</summary>
    private static void ValueWithCaption(Graphics g, string value, Color color, string caption, int x, int limitRight, int y)
    {
        var vw = Gfx.Measure(g, value, Theme.FValue);
        Gfx.Text(g, value, Theme.FValue, new Rectangle(x, y + S(5), vw + S(4), S(22)), color);
        var tw = Gfx.Measure(g, caption, Theme.FTiny);
        var cx = x + vw + S(7);
        if (cx + tw <= limitRight)
            Gfx.Text(g, caption, Theme.FTiny, new Rectangle(cx, y + S(12), tw + S(2), S(15)), Theme.PFaint);
    }

    /// <summary>Additional window: label under the name column, value across the cells.</summary>
    private void DrawExtra(Graphics g, RowInfo row, QuotaWindow win, int y, int w, DateTimeOffset now)
    {
        Gfx.Text(g, win.IsBalance ? "Guthaben" : WindowLayout.ShortLabel(win), Theme.FTiny,
            new Rectangle(NameX, y + S(2), NameWidth, S(15)), Theme.PMuted);
        var x = CellsX;
        var width = CellsWidth(w);
        if (win.IsInfoOnly)
        {
            Gfx.Text(g, win.Note ?? "", Theme.FTiny, new Rectangle(x, y + S(2), width, S(15)), Theme.PFaint);
        }
        else if (win.IsBalance)
        {
            Gfx.Text(g, Formatting.Money(win.Remaining, win.Unit), Theme.FTiny, new Rectangle(x, y + S(2), width, S(15)), Theme.Text);
        }
        else
        {
            // same colour rule as the cells
            var frac = win.UsedFraction ?? 0;
            var color = row.Stale ? Theme.PStale : frac >= 0.999 ? Theme.PCrit : frac > 0 ? Theme.PWarn : Theme.Ok;
            Gfx.Bar(g, new RectangleF(x, y + S(7), S(70), S(5)), win.UsedFraction ?? 0, color, Theme.TrackOnCard);
            var text = Formatting.Percent(win.UsedFraction);
            if (win.Remaining is not null && win.Total is not null)
                text += $" · noch {Formatting.Count(win.Remaining)} von {Formatting.Count(win.Total)}";
            if (win.ResetAtUtc is { } reset && reset > now)
                text += " · Reset in " + Formatting.Span(reset - now);
            Gfx.Text(g, text, Theme.FTiny, new Rectangle(x + S(78), y + S(2), width - S(78), S(15)), Theme.PMuted);
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
                if (WindowLayout.ElapsedFraction(w, now) is { } elapsed)
                    sb.Append($" · Fenster zu {elapsed * 100:0} % um");
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

        if (_pressed == Zone.Header)
        {
            // potential move of the whole popup; engages past the drag threshold
            _moving = true;
            _moveEngaged = false;
            _moveOffset = e.Location;
            Capture = true;
            HideTip();
            return;
        }
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
        if (_moving)
        {
            if (!_moveEngaged)
            {
                var sz = SystemInformation.DragSize;
                if (Math.Abs(e.X - _moveOffset.X) < sz.Width && Math.Abs(e.Y - _moveOffset.Y) < sz.Height)
                    return;
                // dragging parks the popup: auto-pin so it survives the mouse leaving
                _moveEngaged = true;
                Pinned = true;
                Invalidate();
            }
            var f = FindForm();
            if (f is not null)
            {
                var sp = Cursor.Position;
                f.Location = new Point(sp.X - _moveOffset.X, sp.Y - _moveOffset.Y);
            }
            Cursor = Cursors.SizeAll;
            return;
        }
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
        Cursor = zone switch
        {
            Zone.Row or Zone.Refresh or Zone.Settings or Zone.Pin or Zone.SortName or Zone.SortShort or Zone.SortLong
                or Zone.SortModel => Cursors.Hand,
            Zone.Header => Cursors.SizeAll,
            _ => Cursors.Default
        };
        HideTip();
        if (_hoverRow >= 0) _tipTimer.Start();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_moving)
        {
            _moving = false;
            Capture = false;
            _pressed = Zone.None;
            Cursor = Cursors.Default;
            if (_moveEngaged)
            {
                _moveEngaged = false;
                var f = FindForm();
                if (f is not null) PopupMoved?.Invoke(f.Location);
            }
            Invalidate();
            return;
        }
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
            case Zone.SortName: ClickSort(SortColumn.Name); break;
            case Zone.SortShort: ClickSort(SortColumn.Short); break;
            case Zone.SortLong: ClickSort(SortColumn.Long); break;
            case Zone.SortModel: ClickSort(SortColumn.Model); break;
            case Zone.Row when row == _pressedRow && row >= 0 && row < _rows.Count:
                RowClicked?.Invoke(_rows[row].View.Config.Id);
                break;
        }
    }

    private void ClickSort(SortColumn column)
    {
        var (c, d) = AccountSort.Next(SortBy, SortDescending, column);
        SetSort(c, d);
        SortChanged?.Invoke(c, d);
    }

    /// <summary>Click at a client point (render harness – exercises the real mouse path).</summary>
    internal void SimulateClick(Point p)
    {
        var args = new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0);
        OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
        OnMouseDown(args);
        OnMouseUp(args);
    }

    /// <summary>Centre of a column title (render harness).</summary>
    internal Point ColumnTitlePoint(SortColumn column)
    {
        var (x1, x2, x3, _, _) = Columns(ClientSize.Width);
        var y = S(TitleBandH) + S(ColumnsH) / 2;
        return column switch
        {
            SortColumn.Name => new Point(NameX + S(20), y),
            SortColumn.Short => new Point(x1 + S(20), y),
            SortColumn.Model => new Point(x3 + S(20), y),
            _ => new Point(x2 + S(20), y)
        };
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
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
