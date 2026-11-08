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

/// <summary>Owner-drawn popup surface – direct port of the Windows PopupCanvas geometry
/// and zones, rendered with Avalonia/DrawingContext. One control, no child controls, so
/// scrolling cannot drift. Same 480 DIP logical width, same palette.</summary>
internal sealed class PopupCanvas : Control
{
    public const float LogicalWidth = 480;

    private const double TitleH = 56, ColumnsH = 24, HeaderH = TitleH + ColumnsH, FooterH = 30;
    private const double RowBase = 52, LineH = 19, SparkH = 26;
    private const double PadX = 16, NameW = 150, CellGap = 16;
    private const double BodyPadTop = 2, BodyPadBottom = 6, ButtonSize = 30;
    private const string ShortColumn = "5 h";
    private const string LongColumn = "Woche";

    private enum Zone { None, Row, Refresh, Settings, Pin, Scrollbar, Header, SortName, SortShort, SortLong }
    private enum RowState { Ok, Warn, Crit, Error, Stale, Pending }

    private readonly record struct Row(AccountView View, RowState State, double Y, double H, string Name, string Sub,
        List<(string text, IBrush brush)> Notes);

    private readonly List<Row> _rows = new();
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

    /// <summary>Current order (click on KONTO / 5 H / WOCHE cycles ascending → descending → default).</summary>
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
        var y = 0.0;
        foreach (var v in AccountSort.Apply(views, SortBy, SortDescending, DateTimeOffset.UtcNow))
        {
            var (state, name, sub, error) = Classify(v);
            var notes = new List<(string text, IBrush brush)>();
            // stale values raise no alarms (as on Windows) – the stale note explains them instead
            if (state != RowState.Stale)
                foreach (var line in WarnLines(v, state))
                    notes.Add((line.text, line.crit ? Ui.CritBrush : Ui.WarnBrush));
            if (v.Display is { } d)
            {
                foreach (var w in d.Windows.Where(w => w.IsBalance && !string.IsNullOrEmpty(w.Note)))
                    notes.Add((w.Note!, Ui.FaintBrush));
                foreach (var w in d.Windows.Where(w => w.IsInfoOnly && !string.IsNullOrEmpty(w.Note)))
                    notes.Add((w.Note!, Ui.FaintBrush));
            }
            if (error is not null)
                notes.Add((error, state != RowState.Stale ? Ui.CritBrush
                    : v.Failure == FailureKind.AuthExpired ? Ui.MutedBrush
                    : v.Failure == FailureKind.Network ? Ui.WarnBrush
                    : Ui.CritBrush));
            var h = RowBase + notes.Count * LineH;
            if (v.PrimarySeries.Count >= 2) h += SparkH;
            _rows.Add(new Row(v, state, y, h, name, sub, notes));
            y += h;
        }
        _contentH = (int)((_rows.Count == 0 ? 84 : y + BodyPadTop + BodyPadBottom));
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

    private static (RowState state, string name, string sub, string? error) Classify(AccountView v)
    {
        var providerSub = ProviderInfo.DisplayName(v.Config.Provider);
        if (v.Snapshot is { Ok: false } && v.LastGood is null)
            return (RowState.Error, v.Config.Name, providerSub, ShortError(v.Snapshot.Error));
        if (v.IsStale)
        {
            // same wording as Windows – before, every stale case (also plain network outages)
            // read "Token abgelaufen"
            var at = v.LastGood!.AtUtc.ToLocalTime();
            var why = v.Failure switch
            {
                FailureKind.AuthExpired => $"Token abgelaufen – letzte Werte von {at:HH:mm}",
                FailureKind.Network => $"Keine Verbindung – letzte Werte von {at:HH:mm}",
                _ => $"Abfrage fehlgeschlagen ({ShortError(v.Snapshot?.Error)}) – Werte von {at:HH:mm}"
            };
            return (RowState.Stale, v.Config.Name, providerSub, why);
        }
        // no result yet: not "OK" (the summary used to count waiting accounts as OK)
        if (v.Display is null)
            return (RowState.Pending, v.Config.Name, providerSub, null);
        var windows = v.Display?.Windows ?? new List<QuotaWindow>();
        var maxFrac = windows.Where(w => !w.IsInfoOnly).Select(w => w.UsedFraction ?? 0).DefaultIfEmpty(0).Max();
        var state = v.Health switch
        {
            Health.Crit => RowState.Crit,
            Health.Warn => RowState.Warn,
            _ => RowState.Ok
        };
        return (state, v.Config.Name, providerSub, null);
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

    private static List<(string text, bool crit)> WarnLines(AccountView v, RowState state)
    {
        var lines = new List<(string text, bool crit)>();
        if (v.Display is null) return lines;
        var now = DateTimeOffset.Now;
        var windows = v.Display.Windows.Where(w => !w.IsBalance && !w.IsInfoOnly).ToList();
        foreach (var w in windows)
        {
            v.Evals.TryGetValue(w.Key, out var ev);
            ev ??= new WindowEvaluation();
            var crit = ev.Health is Health.Crit || w.UsedFraction is >= 0.999;
            var warn = ev.Health is Health.Warn;
            if (!crit && !warn) continue;
            lines.Add((VerdictFor(w, ev, now), crit));
        }
        // no room for more than two lines – the most critical come first
        return lines.OrderByDescending(l => l.crit).Take(2).ToList();
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

    // ------------------------------------------------------------ geometry

    private Rect BodyRect(Size sz) =>
        new(0, HeaderH, sz.Width, Math.Max(0, sz.Height - HeaderH - FooterH));

    private double MaxScroll(double bodyHeight) => Math.Max(0, _contentH - bodyHeight);

    private static double NameX => PadX + 12;
    private static double NameWidth => NameW - 16;
    private static double CellsX => PadX + NameW;
    private static double CellsWidth(double w) => w - CellsX - PadX - 6;
    private static double CellWidth(double w) => (CellsWidth(w) - CellGap) / 2;

    private (Rect pin, Rect refresh, Rect settings) HeaderButtons(double w)
    {
        var y = 13.0;
        var settings = new Rect(w - PadX - ButtonSize + 4, y, ButtonSize, ButtonSize);
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
        return (new Rect(body.Right - 4 - tw, thumbY, tw, thumbH), trackH, thumbH, max);
    }

    private Zone HitTest(Point p, out int row)
    {
        row = -1;
        var (pin, refresh, settings) = HeaderButtons(Bounds.Width);
        if (pin.Contains(p)) return Zone.Pin;
        if (refresh.Contains(p)) return Zone.Refresh;
        if (settings.Contains(p)) return Zone.Settings;

        var body = BodyRect(Bounds.Size);
        if (p.Y >= TitleH && p.Y < HeaderH) // column titles: click sorts
        {
            if (p.X < CellsX - CellGap / 2) return Zone.SortName;
            return p.X < CellsX + CellWidth(Bounds.Width) + CellGap / 2 ? Zone.SortShort : Zone.SortLong;
        }
        if (!body.Contains(p)) return p.Y < HeaderH ? Zone.Header : Zone.None;
        if (MaxScroll(body.Height) > 0 && p.X >= body.Right - 14) return Zone.Scrollbar;

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
        DrawHeader(g, w);
        DrawColumns(g, w);

        var body = BodyRect(Bounds.Size);
        using (g.PushClip(body))
        {
            var top = body.Top + BodyPadTop - _scroll;
            for (var i = 0; i < _rows.Count; i++)
                DrawRow(g, _rows[i], i, top + _rows[i].Y, w, i == _hoverRow && _hover == Zone.Row);
        }
        DrawScrollbar(g, body);
        DrawFooter(g, w);
        DrawTip(g);
    }

    private void DrawHeader(DrawingContext g, double w)
    {
        var mark = new Rect(PadX + 1, 13, 16, 16);
        var arcGeo = new StreamGeometry();
        using (var ctx = arcGeo.Open())
        {
            ctx.BeginFigure(new Point(mark.Right, mark.Center.Y), false);
            ctx.ArcTo(new Point(mark.Center.X, mark.Bottom), new Size(mark.Width / 2, mark.Height / 2),
                0, false, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        g.DrawGeometry(null, new Pen(Ui.OkBrush, 2.6, null, PenLineCap.Round), arcGeo);
        Tl(g, "Insight Token Watcher", Ui.FTitle, Ui.TextBrush, 230, 22, FontWeight.SemiBold,
            new Point(PadX + 26, 9));
        DrawSummary(g, PadX + 26, 32);

        var (pin, refresh, settings) = HeaderButtons(w);
        DrawButton(g, pin, IconFactory.Glyph.Pin, _hover == Zone.Pin, Pinned ? Ui.Ok : Ui.Muted);
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
        if (hover) g.DrawRectangle(Ui.ButtonHoverBrush, null, r.Inflate(2), 8);
        g.DrawImage(GlyphBitmap(glyph, color), r.Deflate(5));
    }

    private void DrawSummary(DrawingContext g, double x, double y)
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

        var parts = new List<(string text, IBrush brush)> { ($"{_rows.Count} Konten", Ui.MutedBrush) };
        if (crit > 0) parts.Add(($"{crit} kritisch", Ui.CritBrush));
        if (errors > 0) parts.Add(($"{errors} Fehler", Ui.CritBrush));
        if (warn > 0) parts.Add(($"{warn} knapp", Ui.WarnBrush));
        if (stale > 0) parts.Add(($"{stale} veraltet", Ui.MutedBrush));
        if (ok > 0) parts.Add(($"{ok} OK", Ui.OkBrush));

        var cx = x;
        foreach (var (text, brush) in parts)
        {
            var tl = new TextLayout(text, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                Ui.FSmall, brush, maxWidth: 400, maxHeight: 20);
            tl.Draw(g, new Point(cx, y));
            cx += tl.Width + 14;
        }
    }

    private void DrawColumns(DrawingContext g, double w)
    {
        var ty = TitleH + 5;
        var cw = CellWidth(w);
        DrawColumnTitle(g, "KONTO", SortColumn.Name, Zone.SortName, new Rect(NameX, ty, NameWidth, 15));
        DrawColumnTitle(g, ShortColumn.ToUpperInvariant(), SortColumn.Short, Zone.SortShort, new Rect(CellsX, ty, cw, 15));
        DrawColumnTitle(g, LongColumn.ToUpperInvariant(), SortColumn.Long, Zone.SortLong,
            new Rect(CellsX + cw + CellGap, ty, cw, 15));
        g.DrawLine(new Pen(new SolidColorBrush(Ui.Divider), 1), new Point(PadX, HeaderH), new Point(w - PadX, HeaderH));
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
        var ml = new TextLayout(meaning, new Typeface(FontFamily.Default), Ui.FTiny, Ui.MutedBrush);
        var sx = x + tw + 6;
        if (sx + ml.Width <= r.Right) ml.Draw(g, new Point(sx, r.Y));
    }

    private void DrawRow(DrawingContext g, Row row, int idx, double top, double w, bool hover)
    {
        var v = row.View;
        var bodyRect = new Rect(0, top, w, row.H - 2);
        if (hover)
        {
            g.DrawRectangle(Ui.RowHoverBrush, null, bodyRect, 8);
            g.DrawRectangle(null, new Pen(new SolidColorBrush(Ui.HoverBorder), 1), bodyRect, 8);
        }

        // name
        var nameY = top + 9;
        g.DrawRectangle(new SolidColorBrush(Ui.ProviderColor(v.Config.Provider)), null,
            new Rect(PadX + 2, nameY + 3, 8, 8), 4);
        Tl(g, row.Name, Ui.FName, Ui.TextBrush, NameWidth, 18, FontWeight.SemiBold, new Point(NameX, nameY));
        Tl(g, row.Sub, Ui.FTiny, Ui.FaintBrush, NameWidth, 14, FontWeight.Normal, new Point(NameX, top + 31));

        // cells – same column semantics as Windows (WindowLayout): short window (≤ 1 day) left,
        // long window right, balances as amount | runway. Before, a weekly-only account showed
        // its week under "5 H" and info-only accounts got a bar with "– verbraucht".
        var cw = CellWidth(w);
        var cellY = top + 9;
        var layout = v.Display is { } snap ? WindowLayout.Classify(snap.Windows) : new RowLayout();
        QuotaWindow? cellA, cellB;
        if (layout.Span is { } span)
        {
            cellA = span;
            cellB = span.IsBalance ? span : null; // runway in the right column
        }
        else
        {
            cellA = layout.Short;
            cellB = layout.Long;
        }
        DrawCell(g, cellA, v, new Rect(CellsX, cellY, cw, 34), isRight: false);
        DrawCell(g, cellB, v, new Rect(CellsX + cw + CellGap, cellY, cw, 34), isRight: true);

        var y = top + RowBase;
        foreach (var (text, brush) in row.Notes)
        {
            Tl(g, text, Ui.FSmall, brush, w - 2 * PadX, LineH,
                FontWeight.Normal, new Point(PadX + 14, y));
            g.DrawEllipse(brush, null, new Point(PadX + 4, y + LineH / 2), 3, 3);
            y += LineH;
        }
        if (v.PrimarySeries.Count >= 2)
            DrawSparkline(g, v.PrimarySeries, new Rect(PadX, y + 3, w - 2 * PadX, SparkH - 6), Ui.ForHealth(v.Health));
    }

    private void DrawCell(DrawingContext g, QuotaWindow? win, AccountView v, Rect r, bool isRight)
    {
        if (win is null)
        {
            Tl(g, "–", Ui.FValue, Ui.FaintBrush, r.Width, r.Height, FontWeight.SemiBold, r.Position);
            return;
        }
        var x = r.X; var y = r.Y;
        if (win.IsInfoOnly)
        {
            Tl(g, win.Remaining is null ? "–" : Formatting.Money(win.Remaining, win.Unit), Ui.FValue, Ui.TextBrush,
                r.Width, 18, FontWeight.SemiBold, new Point(x, y));
            Tl(g, win.Label, Ui.FTiny, Ui.FaintBrush, r.Width, 13, FontWeight.Normal, new Point(x, y + 19));
            return;
        }
        if (win.IsBalance)
        {
            v.Evals.TryGetValue(win.Key, out var ev);
            if (isRight)
            {
                // as on Windows: only a measured rate gives a runway, counted down live since the
                // snapshot, coloured by the forecast (amber < 2 d, red < 12 h), grey when stale
                string runway = "–";
                IBrush runwayBrush = Ui.FaintBrush;
                if (ev is { HasRate: true, TimeToEmpty: { } tte })
                {
                    var left = tte - (DateTimeOffset.UtcNow - (v.Display?.AtUtc ?? DateTimeOffset.UtcNow));
                    runway = "~" + Formatting.Span(left < TimeSpan.Zero ? TimeSpan.Zero : left);
                    runwayBrush = v.IsStale ? Ui.StaleBrush
                        : ev.Health is Health.Warn or Health.Crit ? Ui.ForHealthBrush(ev.Health)
                        : Ui.TextBrush;
                }
                Tl(g, runway, Ui.FBody, runwayBrush, r.Width, 18, FontWeight.SemiBold, new Point(x, y));
                Tl(g, ev is { HasRate: true } ? "Reichweite" : "kein Verbrauch gemessen", Ui.FTiny, Ui.FaintBrush,
                    r.Width, 13, FontWeight.Normal, new Point(x, y + 19));
            }
            else
            {
                Tl(g, Formatting.Money(win.Remaining, win.Unit), Ui.FValue, v.IsStale ? Ui.StaleBrush : Ui.TextBrush,
                    r.Width, 18, FontWeight.SemiBold, new Point(x, y));
                Tl(g, "Guthaben", Ui.FTiny, Ui.FaintBrush, r.Width, 13, FontWeight.Normal, new Point(x, y + 19));
            }
            return;
        }
        v.Evals.TryGetValue(win.Key, out var evq);
        var frac = win.UsedFraction ?? 0;
        // colour rule (23.10.2026): red = blocked right now (limit reached), amber = in use,
        // lime = unused (0 %) – forecast health no longer drives the bar colour
        // stale (last good) values are grey, as on Windows – they are not current
        var color = v.IsStale ? Ui.Stale : frac >= 0.999 ? Ui.Crit : frac > 0 ? Ui.Warn : Ui.Ok;
        var hasTotal = win.Total is { } t && t > 0;
        var totalV = win.Total ?? 0;
        var remV = Math.Max(0, totalV - frac * totalV);
        // always "noch …" (without it "61.2k von 140k" read like the consumed amount), as
        // "noch X/Y" so large totals still fit the cell
        var label = hasTotal
            ? $"noch {Compact(remV)}/{Compact(totalV)}"
            : $"{Formatting.Percent(win.UsedFraction)} verbraucht";
        Tl(g, label, hasTotal ? Ui.FBody : Ui.FValue, new SolidColorBrush(color), r.Width, 18,
            FontWeight.SemiBold, new Point(x, y));
        var barY = y + 21;
        g.DrawRectangle(new SolidColorBrush(Ui.Track), null, new Rect(x, barY, r.Width, 5), 2.5);
        if (frac > 0.004)
            g.DrawRectangle(new SolidColorBrush(color), null,
                new Rect(x, barY, Math.Max(6, Math.Min(r.Width, r.Width * frac)), 5), 2.5);
        TimeSpan? reset = win.ResetAtUtc is { } r2 ? (TimeSpan?)(r2 - _now) : null;
        var sub = reset is { } rr && rr > TimeSpan.Zero ? $"Reset in {Formatting.Span(rr)}" : win.Label;
        Tl(g, sub, Ui.FTiny, Ui.FaintBrush, r.Width, 13, FontWeight.Normal, new Point(x, barY + 8));
    }

    /// <summary>Compact counts: 11 620 → "11,6k" (German decimals like everywhere else) so
    /// "noch X von Y" fits the cell.</summary>
    private static string Compact(double v) => v >= 10_000
        ? (v / 1000).ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + "k"
        : Formatting.Count(v);

    private static void DrawSparkline(DrawingContext g, IReadOnlyList<Sample> series, Rect r, Color color)
    {
        if (series.Count < 2) return;
        var last = series.Count > 60 ? series.Skip(series.Count - 60).ToList() : series.ToList();
        var min = last.Min(s => s.UsedFraction);
        var max = last.Max(s => s.UsedFraction);
        var span = Math.Max(0.001, max - min);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i < last.Count; i++)
            {
                var px = r.X + r.Width * i / (last.Count - 1);
                var py = r.Bottom - r.Height * ((last[i].UsedFraction - min) / span);
                if (i == 0) ctx.BeginFigure(new Point(px, py), false);
                else ctx.LineTo(new Point(px, py));
            }
            ctx.EndFigure(false);
        }
        g.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.2), geo);
    }

    private void DrawScrollbar(DrawingContext g, Rect body)
    {
        if (MaxScroll(body.Height) <= 0) return;
        var (thumb, _, _, _) = ThumbGeometry(body);
        g.DrawRectangle(Ui.TrackBrush, null, new Rect(thumb.X - 1, body.Top + 2, thumb.Width + 2, body.Height - 4), 3);
        g.DrawRectangle(_hover == Zone.Scrollbar || _dragging ? new SolidColorBrush(Ui.Faint) : new SolidColorBrush(Ui.Stale),
            null, thumb, 3);
    }

    private void DrawFooter(DrawingContext g, double w)
    {
        var top = Bounds.Height - FooterH + 4;
        g.DrawRectangle(Ui.BgBrush, null, new Rect(0, Bounds.Height - FooterH, w, FooterH));
        g.DrawLine(new Pen(new SolidColorBrush(Ui.Divider), 1), new Point(PadX, top - 4), new Point(w - PadX, top - 4));
        var area = new Rect(PadX, top, w - 2 * PadX, FooterH - 8);
        Tl(g, $"Stand {_now:HH:mm:ss}", Ui.FTiny, Ui.FaintBrush, area.Width, area.Height,
            FontWeight.Normal, area.Position);
        var hint = _hover switch
        {
            Zone.SortName => "Klick sortiert: A–Z → Z–A → Standard",
            Zone.SortShort or Zone.SortLong => "Klick sortiert: zuerst leer → zuletzt leer → Standard",
            _ when Pinned => "Angeheftet – Pin oder Tray-Icon zum Lösen",
            _ => "Klick = neu messen · Spaltenkopf = sortieren"
        };
        var hintColor = Pinned && _hover is not (Zone.SortName or Zone.SortShort or Zone.SortLong) ? Ui.Ok : Ui.Faint;
        var tl = new TextLayout(hint, new Typeface(FontFamily.Default), Ui.FTiny, new SolidColorBrush(hintColor),
            textAlignment: TextAlignment.Right, textTrimming: TextTrimming.CharacterEllipsis,
            maxWidth: area.Width, maxHeight: area.Height);
        tl.Draw(g, area.Position);
    }

    private void DrawTip(DrawingContext g)
    {
        if (string.IsNullOrEmpty(_tipText)) return;
        var tl = new TextLayout(_tipText, new Typeface(FontFamily.Default), Ui.FSmall, Ui.TextBrush,
            textWrapping: TextWrapping.Wrap, maxWidth: 250, maxHeight: 300);
        var wdt = tl.Width + 20;
        var hgt = tl.Height + 14;
        var x = Math.Min(_lastMouse.X + 14, Bounds.Width - wdt - 8);
        var y = _lastMouse.Y + 16;
        if (y + hgt > Bounds.Height - FooterH) y = _lastMouse.Y - hgt - 8;
        var box = new Rect(x, y, wdt, hgt);
        g.DrawRectangle(Ui.CardBrush, new Pen(new SolidColorBrush(Ui.HoverBorder), 1), box, 6);
        tl.Draw(g, new Point(x + 10, y + 7));
    }

    private static void Tl(DrawingContext g, string text, double size, IBrush brush, double maxW, double maxH,
        FontWeight weight, Point at)
    {
        var tl = new TextLayout(text, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush,
            textTrimming: TextTrimming.CharacterEllipsis, maxWidth: maxW, maxHeight: maxH);
        tl.Draw(g, at);
    }

    private string TipTextFor(int row)
    {
        if (row < 0 || row >= _rows.Count) return "";
        var v = _rows[row].View;
        var sb = new System.Text.StringBuilder();
        sb.Append(v.Config.Name).Append(" – ").Append(ProviderInfo.DisplayName(v.Config.Provider));
        if (v.IsStale) sb.Append("\n⚠ ").Append(WarnLines(v, RowState.Stale).Select(l => l.text).FirstOrDefault("veraltet"));
        else if (v.Snapshot is { Ok: false })
            sb.Append("\nFehler: ").Append(ShortError(v.Snapshot.Error));
        foreach (var line in WarnLines(v, _rows[row].State))
            sb.Append('\n').Append(line.crit ? "⚠ " : "⚠ ").Append(line.text);
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
                => new Cursor(StandardCursorType.Hand),
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
        // event) – the old IsLeftButtonPressed check returned here on every click, so pin,
        // refresh, settings and row clicks never fired on Linux.
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
    internal Point ColumnTitlePoint(SortColumn column) => column switch
    {
        SortColumn.Name => new Point(NameX + 20, TitleH + ColumnsH / 2),
        SortColumn.Short => new Point(CellsX + 20, TitleH + ColumnsH / 2),
        _ => new Point(CellsX + CellWidth(Bounds.Width) + CellGap + 20, TitleH + ColumnsH / 2)
    };

    /// <summary>Centre of the pin button in canvas coordinates (render harness click test).</summary>
    internal Point PinButtonPoint() => HeaderButtons(Bounds.Width).pin.Center;

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
