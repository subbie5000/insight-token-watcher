using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using InsightTokenWatcher.Core;
using SkiaSharp;

namespace InsightTokenWatcher.Linux;

/// <summary>Owner-drawn popup surface – the design of the Windows PopupCanvas (one card per
/// account; columns 5 h | week | model limit), rendered with Avalonia/DrawingContext. One
/// control, no child controls, so scrolling cannot drift. Same logical sizes, same palette,
/// same status rules (all shared rules live in Core).</summary>
internal sealed class PopupCanvas : Control
{
    /// <summary>Logical width (DIP): wider once the model column is shown.</summary>
    public double LogicalWidth => _modelScope is null ? 480 : 560;

    private const double TitleH = 58, LegendH = 19, ColumnsH = 26, FooterH = 28;
    private const double RowBase = 48, RowGap = 4, LineH = 19, SectionH = 22;
    private const double OuterX = 8, CardPadX = 12, NameW = 128, NameGap = 12, CellGap = 16, ModelW = 92;
    private const double BodyPadTop = 0, BodyPadBottom = 8, ButtonSize = 28;
    private const string ShortColumn = "5 h";
    private const string LongColumn = "Woche";

    private enum Zone { None, Row, Refresh, Settings, Pin, Scrollbar, Header, SortName, SortShort, SortLong, SortModel }
    // Crit = runs dry before its reset (a forecast, the account still works);
    // Blocked = cannot be used right now (Usability.Blocked)
    private enum RowState { Ok, Warn, Crit, Blocked, Error, Stale, Pending }

    /// <summary>Text tones on a row's card: a tinted card takes the brighter ones (see Ui).</summary>
    private readonly record struct Tones(IBrush Faint, IBrush Old, IBrush Crit)
    {
        private static readonly Tones Card = new(Ui.FaintBrush, Ui.StaleBrush, Ui.CritBrush);
        private static readonly Tones Tint = new(Ui.FaintOnTintBrush, Ui.StaleOnTintBrush, Ui.CritOnTintBrush);

        public static Tones For(RowState state) => TintFor(state) is null ? Card : Tint;
    }

    /// <summary>Overlay on the card: red = cannot be used right now, amber = a forecast or an error
    /// (stronger the more urgent), none = nothing to report.</summary>
    private static IBrush? TintFor(RowState state) => state switch
    {
        RowState.Blocked => Ui.TintBlockedBrush,
        RowState.Crit or RowState.Error => Ui.TintStrongBrush,
        RowState.Warn => Ui.TintSoftBrush,
        _ => null
    };

    private readonly record struct Row(AccountView View, RowState State, double Y, double H, string Sub, IBrush SubBrush,
        RowLayout Layout, List<(string text, IBrush brush)> Notes);

    private readonly List<Row> _rows = new();
    private string? _modelScope;   // model family of the third column ("Fable"); null = no such column
    private bool _hasPace;         // a bar carries a pace tick → the legend row is shown
    private double _sectionY = -1; // caption above the blocked accounts (content coordinates); -1 = none

    /// <summary>Title, summary and – with pace ticks – the legend; the column titles follow.</summary>
    private double TitleBandH => TitleH + (_hasPace ? LegendH : 0);

    /// <summary>Height of the band the popup is dragged by (DIP).</summary>
    public double TitleBandHeight => TitleBandH;
    private double HeaderH => TitleBandH + ColumnsH;
    private int _contentH = 84;
    private double _scroll;
    private double _scrollTarget;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _repaint = new() { Interval = TimeSpan.FromSeconds(20) };
    private Zone _hover = Zone.None, _pressed = Zone.None;
    private int _hoverRow = -1, _pressedRow = -1;
    private bool _dragging;        // scrollbar thumb
    private double _dragStartY;
    private double _dragStartScroll;
    private bool _moving;          // title-band drag: moves the whole popup (auto-pins)
    private bool _moveEngaged;
    private Point _moveOffset;
    private Point _lastMouse;
    private string _tipText = "";
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    public bool Pinned { get; set; }
    public bool IsDragging => _dragging || _moving;

    public event Action? RefreshAllClicked;
    public event Action? SettingsClicked;
    public event Action? PinClicked;
    public event Action<Guid>? RowClicked;

    /// <summary>Fired after the user dragged the popup by its title band; argument is the new screen position.</summary>
    public event Action<PixelPoint>? PopupMoved;

    /// <summary>Fired after a click on a column title changed the order (to persist it).</summary>
    public event Action<SortColumn, bool>? SortChanged;

    /// <summary>Current order (a click on a column title cycles ascending → descending → default).</summary>
    public SortColumn SortBy { get; private set; }
    public bool SortDescending { get; private set; }

    private IReadOnlyList<AccountView> _views = Array.Empty<AccountView>();

    public void SetSort(SortColumn column, bool descending)
    {
        SortBy = column;
        SortDescending = column != SortColumn.None && descending;
        SetData(_views);
    }

    private DateTimeOffset _now = DateTimeOffset.Now;
    public DateTimeOffset Now { get => _now; set { _now = value; InvalidateVisual(); } }

    public int DesiredHeight => (int)(HeaderH + _contentH + FooterH);

    public PopupCanvas()
    {
        ClipToBounds = true;
        _anim.Tick += (_, _) =>
        {
            var d = _scrollTarget - _scroll;
            _scroll += d * 0.35;
            if (Math.Abs(d) < 0.5) { _scroll = _scrollTarget; _anim.Stop(); }
            InvalidateVisual();
        };
        _repaint.Tick += (_, _) => InvalidateVisual();
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer.Stop();
            _tipText = TipTextFor(_hoverRow);
            InvalidateVisual();
        };
    }

    public void SetData(IReadOnlyList<AccountView> views)
    {
        _views = views;
        _now = DateTimeOffset.Now;
        var hovered = _hoverRow >= 0 && _hoverRow < _rows.Count ? _rows[_hoverRow].View.Config.Id : (Guid?)null;
        _rows.Clear();
        _sectionY = -1;
        _modelScope = WindowLayout.ColumnScope(views);
        var utcNow = DateTimeOffset.UtcNow;
        _hasPace = false;
        var y = 0.0;
        foreach (var v in AccountSort.Apply(views, SortBy, SortDescending, utcNow))
        {
            var layout = v.Display is { } snap ? WindowLayout.Classify(snap.Windows, _modelScope) : new RowLayout();
            _hasPace |= new[] { layout.Short, layout.Long, layout.Model }.Any(w =>
                w is not null && WindowLayout.ElapsedFraction(w, utcNow) is not null);
            var block = Usability.Blocked(v, utcNow);
            var (state, error) = Classify(v, block);
            var (sub, subBrush) = Status(v, state, layout);
            var tones = Tones.For(state);
            var notes = new List<(string text, IBrush brush)>();
            // old values raise no alarms (as on Windows) – the note below says why they are old.
            // Red only for what blocks the account right now; a forecast is amber.
            if (!v.IsStale)
                foreach (var line in WarnLines(v, block))
                    notes.Add((line.text, line.blocks ? tones.Crit : Ui.WarnBrush));
            // windows without a column of their own (e.g. a second model limit)
            foreach (var w in layout.Extras.Where(w => !w.IsBalance && !w.IsInfoOnly))
                notes.Add((ExtraLine(w, utcNow), Ui.MutedBrush));
            if (v.Display is { } d)
            {
                foreach (var w in d.Windows.Where(w => w.IsBalance && !string.IsNullOrEmpty(w.Note)))
                    notes.Add((w.Note!, tones.Faint));
                foreach (var w in d.Windows.Where(w => w.IsInfoOnly && !string.IsNullOrEmpty(w.Note)))
                    notes.Add((w.Note!, tones.Faint));
            }
            if (error is not null)
                notes.Add((error, !v.IsStale ? tones.Crit
                    : v.Failure == FailureKind.AuthExpired ? Ui.MutedBrush
                    : v.Failure == FailureKind.Network ? Ui.WarnBrush
                    : tones.Crit));
            // the sort puts the blocked accounts last: a caption sets them apart
            if (state == RowState.Blocked && _sectionY < 0)
            {
                _sectionY = y;
                y += SectionH;
            }
            var h = RowBase + (notes.Count > 0 ? notes.Count * LineH + 4 : 0);
            _rows.Add(new Row(v, state, y, h, sub, subBrush, layout, notes));
            y += h + RowGap;
        }
        _contentH = (int)(_rows.Count == 0 ? 84 : y - RowGap + BodyPadTop + BodyPadBottom);
        _scroll = Math.Min(_scroll, MaxScroll(BodyRect(Bounds.Size).Height));
        _scrollTarget = _scroll;
        // a sorted order can move another account under the resting mouse
        if (_hover == Zone.Row)
        {
            _hover = HitTest(_lastMouse, out var row);
            _hoverRow = _hover == Zone.Row ? row : -1;
            var now = _hoverRow >= 0 ? _rows[_hoverRow].View.Config.Id : (Guid?)null;
            if (now != hovered) { _tipText = ""; _tipTimer.Stop(); }
        }
        _repaint.Start();
        InvalidateVisual();
    }

    public void SetScrollForRender(double value)
    {
        _scroll = _scrollTarget = Math.Clamp(value, 0, MaxScroll(BodyRect(Bounds.Size).Height));
        InvalidateVisual();
    }

    // ------------------------------------------------------------ classification

    /// <summary>Row state as on Windows; <c>error</c> is the note that explains a failed poll.</summary>
    private static (RowState state, string? error) Classify(AccountView v, Block? block)
    {
        if (v.Snapshot is { Ok: false } && v.LastGood is null)
            return (RowState.Error, ShortError(v.Snapshot.Error));
        if (v.IsStale)
        {
            var at = v.LastGood!.AtUtc.ToLocalTime();
            var why = v.Failure switch
            {
                FailureKind.AuthExpired => $"Token abgelaufen – letzte Werte von {at:HH:mm}",
                FailureKind.Network => $"Keine Verbindung – letzte Werte von {at:HH:mm}",
                _ => $"Abfrage fehlgeschlagen ({ShortError(v.Snapshot?.Error)}) – Werte von {at:HH:mm}"
            };
            // an expired token gets rotated, a short outage passes – everything else is an error
            var waiting = v.Failure == FailureKind.AuthExpired ||
                          (v.Failure == FailureKind.Network && v.Health != Health.Crit);
            // a limit that was reached stays reached, even when the latest poll failed
            return (block is not null ? RowState.Blocked : waiting ? RowState.Stale : RowState.Error, why);
        }
        // no result yet: not "OK"
        if (v.Display is null)
            return (RowState.Pending, null);
        if (block is not null)
            return (RowState.Blocked, null);
        return (v.Health switch
        {
            Health.Crit => RowState.Crit,
            Health.Warn => RowState.Warn,
            _ => RowState.Ok
        }, null);
    }

    /// <summary>Second line of the row. A healthy row stays quiet (what kind of account it is, in
    /// grey); only exceptions get a coloured word, and a model limit names itself. Colour rule
    /// (10.11.2026): red means "cannot be used right now"; a forecast – tight, runs dry before
    /// its reset – is amber.</summary>
    private static (string text, IBrush brush) Status(AccountView v, RowState state, RowLayout layout) => state switch
    {
        RowState.Ok => (WindowLayout.Subline(v.Config, layout), Ui.FaintBrush),
        RowState.Warn => (WindowLayout.ModelLimitBehind(v) is { } limit
            ? WindowLayout.ModelLimitStatus(limit)
            : "KNAPP", Ui.WarnBrush),
        RowState.Crit => ("LÄUFT LEER", Ui.WarnBrush),
        RowState.Blocked => ("GESPERRT", Tones.For(state).Crit),
        RowState.Error => ("FEHLER", Tones.For(state).Crit),
        RowState.Stale => v.Failure == FailureKind.AuthExpired
            ? ("Token abgelaufen", Ui.MutedBrush)
            : ("offline", Ui.WarnBrush),
        _ => ("warte …", Ui.MutedBrush)
    };

    private static string ShortError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unbekannt";
        var e = error;
        var brace = e.IndexOf('{');
        if (brace > 0) e = e[..brace];
        e = e.Trim().TrimEnd('.', ':', '–', '-').Trim();
        return e.Length > 70 ? e[..70] + "…" : e;
    }

    /// <summary><c>blocks</c>: the line names a limit that keeps the account from working right now.</summary>
    private static List<(string text, bool blocks)> WarnLines(AccountView v, Block? block)
    {
        var lines = new List<(string text, bool crit, bool scoped, bool blocks)>();
        if (v.Display is null) return new List<(string, bool)>();
        var now = DateTimeOffset.Now;
        foreach (var w in v.Display.Windows.Where(w => !w.IsBalance && !w.IsInfoOnly))
        {
            v.Evals.TryGetValue(w.Key, out var ev);
            ev ??= new WindowEvaluation();
            var crit = ev.Health is Health.Crit || w.UsedFraction is >= 0.999;
            var warn = ev.Health is Health.Warn;
            if (!crit && !warn) continue;
            var blocks = block is not null && w.UsedFraction is >= 0.999 &&
                         (w.Scope is null || ReferenceEquals(w, block.Window));
            lines.Add(($"{WindowLayout.ShortLabel(w)}: {VerdictFor(w, ev, now)}", crit, w.Scope is not null, blocks));
        }
        // room for two lines: what blocks the account first, then the general windows (they
        // decide the status) before model limits, the most critical first
        return lines.OrderByDescending(l => l.blocks).ThenBy(l => l.scoped).ThenByDescending(l => l.crit).Take(2)
            .Select(l => (l.text, l.blocks)).ToList();
    }

    private static string VerdictFor(QuotaWindow w, WindowEvaluation ev, DateTimeOffset now)
    {
        var tte = ev.TimeToEmpty;
        TimeSpan? ttr = ev.TimeToReset ?? (w.ResetAtUtc is { } r ? (TimeSpan?)(r - now) : null);
        if (w.UsedFraction is >= 0.999)
            return ttr is { } t && t > TimeSpan.Zero
                ? $"Limit erreicht – Reset in {Formatting.Span(ttr)}"
                : "Limit erreicht";
        if (tte is null) return ev.Verdict;
        if (ttr is null || ttr <= TimeSpan.Zero) return $"noch {Formatting.Span(tte)} bei aktuellem Verbrauch";
        return tte < ttr
            ? $"leer in {Formatting.Span(tte)} – Reset erst in {Formatting.Span(ttr)}!"
            : $"reicht knapp – leer in {Formatting.Span(tte)}, Reset in {Formatting.Span(ttr)}";
    }

    private static string ExtraLine(QuotaWindow w, DateTimeOffset now)
    {
        var text = $"{WindowLayout.ShortLabel(w)}: {Formatting.Percent(w.UsedFraction)}";
        if (w.ResetAtUtc is { } reset && reset > now) text += " · Reset in " + Formatting.Span(reset - now);
        return text;
    }

    // ------------------------------------------------------------ geometry

    private Rect BodyRect(Size sz) =>
        new(0, HeaderH, sz.Width, Math.Max(0, sz.Height - HeaderH - FooterH));

    private double MaxScroll(double bodyHeight) => Math.Max(0, _contentH - bodyHeight);

    private const double NameX = OuterX + CardPadX;
    private const double CellsX = NameX + NameW + NameGap;
    private static double CellsWidth(double w) => w - CellsX - OuterX - CardPadX;

    /// <summary>Column x positions and widths: two equal cells, plus the narrower model cell.</summary>
    private (double x1, double x2, double x3, double cw, double mw) Columns(double w)
    {
        var mw = _modelScope is null ? 0 : ModelW;
        var gaps = _modelScope is null ? CellGap : 2 * CellGap;
        var cw = (CellsWidth(w) - gaps - mw) / 2;
        var x2 = CellsX + cw + CellGap;
        return (CellsX, x2, x2 + cw + CellGap, cw, mw);
    }

    private (Rect pin, Rect refresh, Rect settings) HeaderButtons(double w)
    {
        var y = 9.0; // on the title line – the summary below gets the full width
        var settings = new Rect(w - OuterX - 4 - ButtonSize, y, ButtonSize, ButtonSize);
        var refresh = new Rect(settings.X - ButtonSize - 2, y, ButtonSize, ButtonSize);
        var pin = new Rect(refresh.X - ButtonSize - 2, y, ButtonSize, ButtonSize);
        return (pin, refresh, settings);
    }

    private (Rect thumb, double trackH, double thumbH, double max) ThumbGeometry(Rect body)
    {
        var max = MaxScroll(body.Height);
        var trackH = body.Height - 8;
        var thumbH = Math.Max(28.0, trackH * (double)body.Height / Math.Max(1, _contentH));
        var thumbY = body.Top + 4 + (trackH - thumbH) * (max <= 0 ? 0 : _scroll / max);
        var tw = _hover == Zone.Scrollbar || _dragging ? 6.0 : 4.0;
        // in the gutter right of the cards
        return (new Rect(body.Right - 2 - tw, thumbY, tw, thumbH), trackH, thumbH, max);
    }

    private Zone HitTest(Point p, out int row)
    {
        row = -1;
        var (pin, refresh, settings) = HeaderButtons(Bounds.Width);
        if (pin.Contains(p)) return Zone.Pin;
        if (refresh.Contains(p)) return Zone.Refresh;
        if (settings.Contains(p)) return Zone.Settings;

        var body = BodyRect(Bounds.Size);
        if (p.Y >= TitleBandH && p.Y < HeaderH) // column titles: click sorts
        {
            var (x1, x2, x3, _, mw) = Columns(Bounds.Width);
            if (p.X < x1 - CellGap / 2) return Zone.SortName;
            if (p.X < x2 - CellGap / 2) return Zone.SortShort;
            return mw <= 0 || p.X < x3 - CellGap / 2 ? Zone.SortLong : Zone.SortModel;
        }
        if (!body.Contains(p)) return p.Y < HeaderH ? Zone.Header : Zone.None;
        if (MaxScroll(body.Height) > 0 && p.X >= body.Right - OuterX) return Zone.Scrollbar;

        var cy = p.Y - body.Top + _scroll - BodyPadTop;
        for (var i = 0; i < _rows.Count; i++)
            if (cy >= _rows[i].Y && cy < _rows[i].Y + _rows[i].H)
            {
                row = i;
                return Zone.Row;
            }
        return Zone.None;
    }

    // ------------------------------------------------------------ rendering

    public override void Render(DrawingContext g)
    {
        var w = Bounds.Width;
        // Paint the whole surface: Avalonia only delivers pointer events where a control has
        // drawn something. Without this fill, clicks on empty spots (free header space, between
        // the column titles) fell through to the window frame – dragging the popup by its
        // title only worked when the pointer happened to be on a glyph.
        g.DrawRectangle(Ui.BgBrush, null, new Rect(Bounds.Size));
        DrawHeader(g, w);
        DrawColumns(g, w);

        var body = BodyRect(Bounds.Size);
        using (g.PushClip(body))
        {
            var top = body.Top + BodyPadTop - _scroll;
            for (var i = 0; i < _rows.Count; i++)
            {
                var y = top + _rows[i].Y;
                if (y > body.Bottom || y + _rows[i].H < body.Top) continue;
                DrawRow(g, _rows[i], y, w, i == _hoverRow && _hover == Zone.Row);
            }
            if (_sectionY >= 0) DrawSection(g, top + _sectionY, w);
        }
        DrawScrollbar(g, body);
        DrawFooter(g, w);
        DrawTip(g);
    }

    private void DrawHeader(DrawingContext g, double w)
    {
        // brand mark (the tray gauge without a number): track + 250° arc from 12 o'clock
        var c = new Point(NameX + 9.5, 22.5);
        const double r = 8.5;
        g.DrawEllipse(null, Ui.BrandTrackPen, c, r, r);
        var end = (-90 + 250) * Math.PI / 180;
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end)), new Size(r, r), 0, true,
                SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        g.DrawGeometry(null, Ui.BrandArcPen, arc);

        var (pin, refresh, settings) = HeaderButtons(w);
        var textX = NameX + 28;
        Tl(g, "Insight Token Watcher", Ui.FTitle, Ui.TextBrush, pin.Left - textX - 8, 22, FontWeight.SemiBold,
            new Point(textX, 10));
        DrawSummary(g, textX, 38, w - NameX);
        if (_hasPace) DrawPaceLegend(g, textX, TitleH - 2, w - NameX);

        DrawButton(g, pin, Pinned ? IconFactory.Glyph.Pinned : IconFactory.Glyph.Pin, _hover == Zone.Pin,
            Pinned ? Ui.Ok : Ui.Muted);
        DrawButton(g, refresh, IconFactory.Glyph.Refresh, _hover == Zone.Refresh, Ui.Muted);
        DrawButton(g, settings, IconFactory.Glyph.Settings, _hover == Zone.Settings, Ui.Muted);
    }

    // Button glyphs are rasterised once per (glyph, colour) and cached: before this cache
    // every Render() rebuilt three bitmaps + PNG-encodes per frame (≈180/s).
    private static readonly Dictionary<(IconFactory.Glyph glyph, uint argb), Avalonia.Media.Imaging.Bitmap> _glyphCache = new();

    private static Avalonia.Media.Imaging.Bitmap GlyphBitmap(IconFactory.Glyph glyph, Color color)
    {
        var key = (glyph, (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B));
        if (_glyphCache.TryGetValue(key, out var cached)) return cached;
        using var bitmap = new SKBitmap(96, 96);
        using (var c = new SKCanvas(bitmap))
        {
            c.Clear(SKColors.Transparent);
            IconFactory.DrawGlyph(c, 12, 12, 72, glyph, new SKColor(color.R, color.G, color.B, color.A));
        }
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        var av = new Avalonia.Media.Imaging.Bitmap(ms);
        _glyphCache[key] = av;
        return av;
    }

    private void DrawButton(DrawingContext g, Rect r, IconFactory.Glyph glyph, bool hover, Color color)
    {
        if (hover) g.DrawRectangle(Ui.ButtonHoverBrush, null, r, 7, 7);
        g.DrawImage(GlyphBitmap(glyph, hover ? Ui.Text : color), r.Deflate(4.5));
    }

    /// <summary>Caption above the accounts that cannot be used right now. It sits closer to its
    /// cards than to the row above.</summary>
    private void DrawSection(DrawingContext g, double y, double w)
    {
        var tl = new TextLayout($"NICHT NUTZBAR · {_rows.Count(r => r.State == RowState.Blocked)}",
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), Ui.FCaps, Ui.FaintBrush);
        tl.Draw(g, new Point(NameX, y + 7));
        var ly = Math.Round(y + 7 + tl.Height / 2) + 0.5;
        g.DrawLine(Ui.SectionPen, new Point(NameX + tl.Width + 8, ly), new Point(w - OuterX, ly));
    }

    /// <summary>"13 Konten" plus one entry per state that occurs: a coloured dot carries the
    /// state, the text stays grey. Entries that would not fit the width are left out,
    /// least urgent first.</summary>
    private void DrawSummary(DrawingContext g, double x, double y, double limit)
    {
        int blocked = 0, crit = 0, errors = 0, warn = 0, stale = 0, ok = 0;
        foreach (var r in _rows)
            switch (r.State)
            {
                case RowState.Blocked: blocked++; break;
                case RowState.Crit: crit++; break;
                case RowState.Error: errors++; break;
                case RowState.Warn: warn++; break;
                case RowState.Stale: stale++; break;
                case RowState.Ok: ok++; break;
            }

        var cx = x;
        var total = Layout($"{_rows.Count} Konten", Ui.FSmall, Ui.MutedBrush);
        total.Draw(g, new Point(cx, y));
        cx += total.Width;

        var parts = new List<(string text, IBrush dot)>();
        if (blocked > 0) parts.Add(($"{blocked} gesperrt", Ui.CritBrush));
        if (errors > 0) parts.Add(($"{errors} Fehler", Ui.CritBrush));
        if (crit > 0) parts.Add((crit == 1 ? "1 läuft leer" : $"{crit} laufen leer", Ui.WarnBrush));
        if (warn > 0) parts.Add(($"{warn} knapp", Ui.WarnBrush));
        if (stale > 0) parts.Add(($"{stale} veraltet", Ui.StaleBrush));
        if (ok > 0) parts.Add(($"{ok} OK", Ui.OkBrush));
        foreach (var (text, dot) in parts)
        {
            var tl = Layout(text, Ui.FSmall, Ui.MutedBrush);
            if (cx + 22 + tl.Width > limit) break;
            cx += 12;
            g.DrawEllipse(dot, null, new Point(cx + 3, y + total.Height / 2 + 0.5), 3, 3);
            cx += 10;
            tl.Draw(g, new Point(cx, y));
            cx += tl.Width;
        }
    }

    private void DrawColumns(DrawingContext g, double w)
    {
        var ty = TitleBandH + 3;
        var (x1, x2, x3, cw, mw) = Columns(w);
        DrawColumnTitle(g, "KONTO", SortColumn.Name, Zone.SortName, new Rect(NameX, ty, NameW, 15));
        DrawColumnTitle(g, "5 STUNDEN", SortColumn.Short, Zone.SortShort, new Rect(x1, ty, cw, 15));
        DrawColumnTitle(g, "WOCHE", SortColumn.Long, Zone.SortLong, new Rect(x2, ty, cw, 15));
        if (mw > 0)
            DrawColumnTitle(g, _modelScope!.ToUpperInvariant(), SortColumn.Model, Zone.SortModel, new Rect(x3, ty, mw, 15));
    }

    /// <summary>Column title; the sort column gets a direction triangle (▲ ascending) and – if it
    /// fits – what the order means ("zuerst leer", "A–Z"). Same rule as Windows.</summary>
    private void DrawColumnTitle(DrawingContext g, string title, SortColumn column, Zone zone, Rect r)
    {
        var active = SortBy == column;
        var brush = active ? Ui.TextBrush : _hover == zone ? Ui.MutedBrush : Ui.FaintBrush;
        var tl = new TextLayout(title, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            Ui.FCaps, brush, maxWidth: r.Width, maxHeight: r.Height);
        tl.Draw(g, r.Position);
        if (!active) return;

        var x = r.X + tl.Width + 6;
        double tw = 8, th = 5, mid = r.Y + 7;
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            if (SortDescending)
            {
                ctx.BeginFigure(new Point(x, mid - th / 2), true);
                ctx.LineTo(new Point(x + tw, mid - th / 2));
                ctx.LineTo(new Point(x + tw / 2, mid + th / 2));
            }
            else
            {
                ctx.BeginFigure(new Point(x, mid + th / 2), true);
                ctx.LineTo(new Point(x + tw, mid + th / 2));
                ctx.LineTo(new Point(x + tw / 2, mid - th / 2));
            }
            ctx.EndFigure(true);
        }
        g.DrawGeometry(Ui.OkBrush, null, tri);

        var meaning = column == SortColumn.Name
            ? SortDescending ? "Z–A" : "A–Z"
            : SortDescending ? "zuletzt leer" : "zuerst leer";
        var ml = Layout(meaning, Ui.FTiny, Ui.MutedBrush);
        var sx = x + tw + 6;
        if (sx + ml.Width <= r.Right) ml.Draw(g, new Point(sx, r.Y));
    }

    /// <summary>One account = one card. Every row has the same shape; a problem only changes the
    /// tint: amber for forecasts and errors, red for an account that cannot be used right now.</summary>
    private void DrawRow(DrawingContext g, Row row, double top, double w, bool hover)
    {
        var v = row.View;
        var card = new Rect(OuterX, top, w - 2 * OuterX, row.H);
        g.DrawRectangle(Ui.CardBrush, null, card, 8, 8);
        if (TintFor(row.State) is { } tint) g.DrawRectangle(tint, null, card, 8, 8);
        if (hover) g.DrawRectangle(Ui.HoverOverlayBrush, null, card, 8, 8);

        // name + second line (provider, or the state when something is wrong)
        Tl(g, v.Config.Name, Ui.FName, v.IsStale ? Ui.MutedBrush : Ui.TextBrush, NameW, 18, FontWeight.SemiBold,
            new Point(NameX, top + 6));
        Tl(g, row.Sub, Ui.FTiny, row.SubBrush, NameW, 15, FontWeight.Normal, new Point(NameX, top + 26));

        var (x1, x2, x3, cw, mw) = Columns(w);
        var layout = row.Layout;
        if (v.Display is null)
        {
            // an error row explains itself in the note below
            if (row.State == RowState.Pending)
                Tl(g, "Erste Abfrage läuft …", Ui.FSmall, Ui.MutedBrush, CellsWidth(w), 16, FontWeight.Normal,
                    new Point(x1, top + 15));
        }
        else if (layout.Span is { } span)
        {
            DrawSpanCells(g, span, v, top, x1, x2, cw, w - NameX - x2, Tones.For(row.State));
        }
        else
        {
            var tones = Tones.For(row.State);
            DrawQuotaCell(g, layout.Short, v, x1, top, cw, ShortColumn, tones);
            DrawQuotaCell(g, layout.Long, v, x2, top, cw, LongColumn, tones);
            // an account without a model limit (other provider, API key) leaves the cell empty
            if (mw > 0 && layout.Model is { } model)
                DrawQuotaCell(g, model, v, x3, top, mw, _modelScope!, tones, layout.Long);
        }

        var y = top + RowBase - 2;
        foreach (var (text, brush) in row.Notes)
        {
            g.DrawEllipse(brush, null, new Point(NameX + 3, y + LineH / 2), 2.5, 2.5);
            Tl(g, text, Ui.FSmall, brush, w - 2 * NameX - 12, LineH, FontWeight.Normal, new Point(NameX + 12, y + 1));
            y += LineH;
        }
    }

    /// <summary>Value left, reset countdown right, bar below. <paramref name="sameResetAs"/>: the
    /// countdown is left out when it would repeat that window's (the Fable week resets with
    /// the general week).</summary>
    private void DrawQuotaCell(DrawingContext g, QuotaWindow? win, AccountView v, double x, double top, double cw,
        string column, Tones tones, QuotaWindow? sameResetAs = null)
    {
        if (win is null)
        {
            Tl(g, "—", Ui.FSmall, tones.Faint, cw, 16, FontWeight.Normal, new Point(x, top + 9));
            return;
        }
        // colour rule (23.10.2026): red = blocked right now (limit reached), amber = in use,
        // lime = unused (0 %); old values grey – forecast health does not drive the bar colour
        var frac = win.UsedFraction ?? 0;
        var barBrush = v.IsStale ? Ui.StaleBrush : frac >= 0.999 ? Ui.CritBrush : frac > 0 ? Ui.WarnBrush : Ui.OkBrush;
        var valueBrush = v.IsStale ? tones.Old : frac >= 0.999 ? tones.Crit : frac > 0 ? Ui.WarnBrush : Ui.TextBrush;

        var value = new TextLayout(Formatting.PercentShort(win.UsedFraction),
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), Ui.FValue, valueBrush);
        value.Draw(g, new Point(x, top + 4));

        // right: countdown – only if it fits next to the value
        var right = x + cw;
        string countdown;
        if (win.ResetAtUtc is { } reset && reset > _now)
            countdown = sameResetAs?.ResetAtUtc is { } other && Math.Abs((other - reset).TotalMinutes) < 5
                ? ""
                : Formatting.Span(reset - _now);
        else if (win.Remaining is not null && win.Total is not null)
            countdown = $"noch {Compact(win.Remaining.Value)}/{Compact(win.Total.Value)}";
        else
            countdown = "";
        if (countdown.Length > 0)
        {
            var cd = Layout(countdown, Ui.FTiny, Ui.MutedBrush);
            if (cd.Width <= cw - value.Width - 8)
            {
                cd.Draw(g, new Point(right - cd.Width, top + 10));
                right -= cd.Width + 6;
            }
        }

        // label only when this window is not what the column says (e.g. "Requests")
        var label = WindowLayout.ShortLabel(win);
        if (!label.Equals(column, StringComparison.OrdinalIgnoreCase))
        {
            var lx = x + value.Width + 8;
            if (right - lx > 16)
                Tl(g, label, Ui.FTiny, tones.Faint, right - lx - 4, 15, FontWeight.Normal, new Point(lx, top + 10));
        }

        var bar = new Rect(x, top + 32, cw, 6);
        g.DrawRectangle(Ui.TrackOnCardBrush, null, bar, 3, 3);
        if (frac > 0.001)
            g.DrawRectangle(barBrush, null, new Rect(x, top + 32, Math.Max(6, cw * Math.Min(1, frac)), 6), 3, 3);
        if (WindowLayout.ElapsedFraction(win, _now) is { } elapsed)
            DrawPaceTick(g, bar, elapsed, v.IsStale ? Ui.MutedBrush : Ui.TextBrush);
    }

    /// <summary>Pace tick: how much of the window's time has passed. A bar that ends left of it
    /// is below the pace of time.</summary>
    private static void DrawPaceTick(DrawingContext g, Rect bar, double elapsed, IBrush brush)
    {
        const double tw = 2, over = 3;
        var x = Math.Round(Math.Clamp(bar.X + bar.Width * elapsed - tw / 2, bar.X, bar.Right - tw));
        var r = new Rect(x, bar.Y - over, tw, bar.Height + 2 * over);
        g.DrawRectangle(PaceEdgeBrush, null, r.Inflate(1)); // keeps it visible on a filled bar
        g.DrawRectangle(brush, null, r);
    }

    private static readonly IBrush PaceEdgeBrush = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

    /// <summary>One line under the summary that explains the tick; the rule is added if it fits.</summary>
    private static void DrawPaceLegend(DrawingContext g, double x, double y, double limit)
    {
        g.DrawRectangle(Ui.WarnBrush, null, new Rect(x, y + 6, 18, 5), 2.5, 2.5);
        x += 24;
        x += LegendText(g, "Verbrauch", x, y) + 14;
        DrawPaceTick(g, new Rect(x, y + 5, 2, 6), 0, Ui.TextBrush);
        x += 8;
        x += LegendText(g, "verstrichene Zeit im Fenster", x, y) + 14;
        foreach (var rule in new[] { "Balken vor dem Strich = im Plan", "vor dem Strich = im Plan" })
        {
            var tl = Layout(rule, Ui.FTiny, Ui.FaintBrush);
            if (x + tl.Width > limit) continue;
            tl.Draw(g, new Point(limit - tl.Width, y));
            break;
        }
    }

    private static double LegendText(DrawingContext g, string text, double x, double y)
    {
        var tl = Layout(text, Ui.FTiny, Ui.MutedBrush);
        tl.Draw(g, new Point(x, y));
        return tl.Width;
    }

    /// <summary>Balance/info accounts use the first two cells: amount | runway. The caption
    /// follows its value ("23,42 $ Guthaben", "~27 d 3 h Reichweite").</summary>
    private void DrawSpanCells(DrawingContext g, QuotaWindow span, AccountView v, double top, double x1, double x2, double cw,
        double restWidth, Tones tones)
    {
        v.Evals.TryGetValue(span.Key, out var ev);
        var amount = span.Remaining is null ? "–" : Formatting.Money(span.Remaining, span.Unit);
        ValueWithCaption(g, amount, v.IsStale ? tones.Old : Ui.TextBrush, span.IsInfoOnly ? span.Label : "Guthaben",
            tones.Faint, x1, x2 - 6, top);

        if (span.IsInfoOnly)
        {
            Tl(g, "—", Ui.FSmall, tones.Faint, cw, 16, FontWeight.Normal, new Point(x2, top + 9));
            return;
        }

        // only a measured rate gives a runway, counted down live since the snapshot; amber when
        // the forecast is tight (< 2 d), grey when the values are old
        var runway = "—";
        var runwayBrush = tones.Faint;
        if (ev is { HasRate: true, TimeToEmpty: { } tte })
        {
            var left = tte - (DateTimeOffset.UtcNow - (v.Display?.AtUtc ?? DateTimeOffset.UtcNow));
            runway = "~" + Formatting.Span(left < TimeSpan.Zero ? TimeSpan.Zero : left);
            runwayBrush = v.IsStale ? tones.Old
                : ev.Health is Health.Warn or Health.Crit ? Ui.WarnBrush
                : Ui.TextBrush;
        }
        ValueWithCaption(g, runway, runwayBrush, "Reichweite", tones.Faint, x2, x2 + restWidth, top);
        // may run into the (empty) model cell – the text can be longer than one cell
        Tl(g, ev is { HasRate: true } ? "bei aktuellem Verbrauch" : "kein Verbrauch", Ui.FTiny, tones.Faint,
            restWidth, 15, FontWeight.Normal, new Point(x2, top + 27));
    }

    /// <summary>Value in the value font, its caption small and grey right behind it (left out
    /// when it would pass <paramref name="limitRight"/>).</summary>
    private static void ValueWithCaption(DrawingContext g, string value, IBrush brush, string caption, IBrush captionBrush,
        double x, double limitRight, double top)
    {
        var tl = new TextLayout(value, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            Ui.FValue, brush);
        tl.Draw(g, new Point(x, top + 4));
        var cap = Layout(caption, Ui.FTiny, captionBrush);
        var cx = x + tl.Width + 7;
        if (cx + cap.Width <= limitRight) cap.Draw(g, new Point(cx, top + 10));
    }

    /// <summary>Compact counts: 11 620 → "11,6k" (German decimals like everywhere else).</summary>
    private static string Compact(double v) => v >= 10_000
        ? (v / 1000).ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + "k"
        : Formatting.Count(v);

    private void DrawScrollbar(DrawingContext g, Rect body)
    {
        if (MaxScroll(body.Height) <= 0) return;
        var (thumb, _, _, _) = ThumbGeometry(body);
        g.DrawRectangle(_hover == Zone.Scrollbar || _dragging ? Ui.FaintBrush : Ui.StaleBrush, null, thumb, 3, 3);
    }

    private void DrawFooter(DrawingContext g, double w)
    {
        var top = Bounds.Height - FooterH;
        g.DrawRectangle(Ui.BgBrush, null, new Rect(0, top, w, FooterH));
        g.DrawLine(Ui.DividerPen, new Point(0, top + 0.5), new Point(w, top + 0.5));
        var area = new Rect(NameX, top + 7, w - 2 * NameX, FooterH - 8);
        Tl(g, $"Stand {_now:HH:mm:ss}", Ui.FTiny, Ui.FaintBrush, area.Width, area.Height,
            FontWeight.Normal, area.Position);
        var sortHover = _hover is Zone.SortName or Zone.SortShort or Zone.SortLong or Zone.SortModel;
        var hint = _hover switch
        {
            Zone.SortName => "Klick sortiert: A–Z → Z–A → Standard",
            Zone.SortShort or Zone.SortLong or Zone.SortModel => "Klick sortiert: zuerst leer → zuletzt leer → Standard",
            _ when Pinned => "Angeheftet – Pin oder Tray-Icon zum Lösen",
            _ => "Klick = neu messen · Spaltenkopf = sortieren"
        };
        var tl = new TextLayout(hint, new Typeface(FontFamily.Default), Ui.FTiny,
            Pinned && !sortHover ? Ui.OkBrush : Ui.FaintBrush,
            textAlignment: TextAlignment.Right, textTrimming: TextTrimming.CharacterEllipsis,
            maxWidth: area.Width, maxHeight: area.Height);
        tl.Draw(g, area.Position);
    }

    private void DrawTip(DrawingContext g)
    {
        if (string.IsNullOrEmpty(_tipText)) return;
        var tl = new TextLayout(_tipText, new Typeface(FontFamily.Default), Ui.FSmall, Ui.TextBrush,
            textWrapping: TextWrapping.Wrap, maxWidth: 340, maxHeight: 420);
        var wdt = tl.Width + 20;
        var hgt = tl.Height + 14;
        var x = Math.Max(8, Math.Min(_lastMouse.X + 14, Bounds.Width - wdt - 8));
        var y = _lastMouse.Y + 16;
        if (y + hgt > Bounds.Height - FooterH) y = Math.Max(8, _lastMouse.Y - hgt - 8);
        var box = new Rect(x, y, wdt, hgt);
        g.DrawRectangle(Ui.TipBrush, Ui.TipBorderPen, box, 6, 6);
        tl.Draw(g, new Point(x + 10, y + 7));
    }

    private static TextLayout Layout(string text, double size, IBrush brush) =>
        new(text, new Typeface(FontFamily.Default), size, brush);

    private static void Tl(DrawingContext g, string text, double size, IBrush brush, double maxW, double maxH,
        FontWeight weight, Point at)
    {
        var tl = new TextLayout(text, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush,
            textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Math.Max(1, maxW), maxHeight: maxH);
        tl.Draw(g, at);
    }

    /// <summary>Details on hover: every window with value and reset time (the cells leave out
    /// what would repeat), then why a poll failed and the warning lines.</summary>
    private string TipTextFor(int row)
    {
        if (row < 0 || row >= _rows.Count) return "";
        var v = _rows[row].View;
        var now = DateTimeOffset.UtcNow;
        var sb = new System.Text.StringBuilder();
        sb.Append(v.Config.Name).Append(" · ").Append(ProviderInfo.DisplayName(v.Config.Provider));
        if (v.Display is { } snap)
            foreach (var w in snap.Windows)
            {
                var value = w.IsInfoOnly && w.Remaining is null ? w.Note ?? "–"
                    : w.IsBalance || w.IsInfoOnly ? Formatting.Money(w.Remaining, w.Unit)
                    : Formatting.Percent(w.UsedFraction) + " verbraucht";
                if (!w.IsBalance && !w.IsInfoOnly && w.Remaining is not null && w.Total is not null)
                    value += $" (noch {Formatting.Count(w.Remaining)} von {Formatting.Count(w.Total)})";
                sb.Append("\n• ").Append(w.Label).Append(": ").Append(value);
                if (w.ResetAtUtc is { } r && r > now)
                    sb.Append("\n   Reset ").Append(r.ToLocalTime().ToString("ddd dd.MM. HH:mm", CultureInfo.GetCultureInfo("de-DE")))
                        .Append(" (in ").Append(Formatting.Span(r - now)).Append(')');
                if (WindowLayout.ElapsedFraction(w, now) is { } elapsed)
                    sb.Append($" · Fenster zu {elapsed * 100:0} % um");
                // a model limit is read less often than the rest: say so when it is older
                if (w.MeasuredAtUtc is { } read && snap.AtUtc - read > TimeSpan.FromSeconds(90))
                    sb.Append($" · Wert von {read.ToLocalTime():HH:mm}");
            }
        var block = Usability.Blocked(v, now);
        var (_, error) = Classify(v, block);
        if (error is not null) sb.Append("\n⚠ ").Append(error);
        if (!v.IsStale)
            foreach (var line in WarnLines(v, block))
                sb.Append("\n⚠ ").Append(line.text);
        sb.Append("\nKlick = jetzt neu messen");
        return sb.ToString();
    }

    public void ResetHover()
    {
        _hover = Zone.None;
        _hoverRow = -1;
        _tipText = "";
        _tipTimer.Stop();
        InvalidateVisual();
    }

    // ------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _pressed = HitTest(p, out _pressedRow);

        if (_pressed == Zone.Header)
        {
            _moving = true;
            _moveEngaged = false;
            _moveOffset = p;
            _tipText = "";
            return;
        }
        if (_pressed != Zone.Scrollbar) return;

        var body = BodyRect(Bounds.Size);
        var (thumb, trackH, thumbH, max) = ThumbGeometry(body);
        if (!thumb.Contains(p))
        {
            var rel = (p.Y - body.Top - 4 - thumbH / 2) / Math.Max(1, trackH - thumbH);
            _scroll = _scrollTarget = Math.Clamp(rel * max, 0, max);
        }
        _dragging = true;
        _dragStartY = p.Y;
        _dragStartScroll = _scroll;
        _tipText = "";
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _lastMouse = p;
        if (_moving)
        {
            if (!_moveEngaged)
            {
                if (Math.Abs(p.X - _moveOffset.X) < 4 && Math.Abs(p.Y - _moveOffset.Y) < 4) return;
                _moveEngaged = true;
                Pinned = true;
                InvalidateVisual();
            }
            if (VisualRoot is Window f)
            {
                var screen = f.PointToScreen(p);
                f.Position = new PixelPoint((int)(screen.X - _moveOffset.X), (int)(screen.Y - _moveOffset.Y));
            }
            return;
        }
        if (_dragging)
        {
            var body = BodyRect(Bounds.Size);
            var (_, trackH, thumbH, max) = ThumbGeometry(body);
            var ratio = max / Math.Max(1, trackH - thumbH);
            _scroll = _scrollTarget = Math.Clamp(_dragStartScroll + (p.Y - _dragStartY) * ratio, 0, max);
            InvalidateVisual();
            return;
        }

        var zone = HitTest(p, out var row);
        if (zone == _hover && row == _hoverRow) return;
        _hover = zone;
        _hoverRow = zone == Zone.Row ? row : -1;
        Cursor = zone switch
        {
            Zone.Row or Zone.Refresh or Zone.Settings or Zone.Pin or Zone.SortName or Zone.SortShort or Zone.SortLong
                or Zone.SortModel => new Cursor(StandardCursorType.Hand),
            Zone.Header => new Cursor(StandardCursorType.SizeAll),
            _ => new Cursor(StandardCursorType.Arrow)
        };
        _tipText = "";
        _tipTimer.Stop();
        if (_hoverRow >= 0) _tipTimer.Start();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_moving)
        {
            _moving = false;
            _pressed = Zone.None;
            Cursor = new Cursor(StandardCursorType.Arrow);
            if (_moveEngaged && VisualRoot is Window f)
            {
                _moveEngaged = false;
                PopupMoved?.Invoke(f.Position);
            }
            InvalidateVisual();
            return;
        }
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
            return;
        }
        // On release the button is no longer "pressed" (Avalonia reports the state after the
        // event) – an IsLeftButtonPressed check would return here on every click.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var zone = HitTest(e.GetPosition(this), out var row);
        var pressed = _pressed;
        _pressed = Zone.None;
        if (zone != pressed) return;
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

    /// <summary>Centre of a column title in canvas coordinates (render harness click test).</summary>
    internal Point ColumnTitlePoint(SortColumn column)
    {
        var (x1, x2, x3, _, _) = Columns(Bounds.Width);
        var y = TitleBandH + ColumnsH / 2;
        return column switch
        {
            SortColumn.Name => new Point(NameX + 20, y),
            SortColumn.Short => new Point(x1 + 20, y),
            SortColumn.Model => new Point(x3 + 20, y),
            _ => new Point(x2 + 20, y)
        };
    }

    /// <summary>Centre of the pin button in canvas coordinates (render harness click test).</summary>
    internal Point PinButtonPoint() => HeaderButtons(Bounds.Width).pin.Center;

    /// <summary>True when the content is taller than the body (render harness: a popup that
    /// shows everything must not have a scrollbar).</summary>
    internal bool IsScrollable => MaxScroll(BodyRect(Bounds.Size).Height) > 0;

    /// <summary>Current row order (render harness).</summary>
    internal IEnumerable<string> RowNames() => _rows.Select(r => r.View.Config.Name);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var max = MaxScroll(BodyRect(Bounds.Size).Height);
        if (max <= 0) return;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta.Y * 64, 0, max);
        _tipText = "";
        _anim.Start();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging && !_moving) ResetHover();
    }
}
