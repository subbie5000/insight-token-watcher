namespace TokenWatch.Core;

/// <summary>Value history of a quota window (utilization or balance) with reset detection.</summary>
public sealed class WindowSeries
{
    private readonly List<Sample> _samples = new();
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    public IReadOnlyList<Sample> Samples => _samples;

    public void Add(Sample s, bool balanceKind)
    {
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (balanceKind)
            {
                // Balance: clear jump upwards = topped up -> start series fresh
                if (s.Remaining is not null && last.Remaining is not null &&
                    last.Remaining > 0 && s.Remaining > last.Remaining * 1.05 + 0.01)
                    _samples.Clear();
            }
            else
            {
                // Window: utilization drops clearly = reset happened -> start series fresh
                if (s.UsedFraction < last.UsedFraction - 0.02)
                    _samples.Clear();
            }
        }
        _samples.Add(s);
        var cutoff = s.AtUtc - MaxAge;
        _samples.RemoveAll(x => x.AtUtc < cutoff);
    }
}

public static class BurnMath
{
    /// <summary>Least-squares slope per minute over the last <paramref name="windowMinutes"/>.</summary>
    public static double? SlopePerMinute(IReadOnlyList<Sample> samples, bool balanceKind, DateTimeOffset now, int windowMinutes = 60)
    {
        var cutoff = now - TimeSpan.FromMinutes(windowMinutes);
        var pts = samples.Where(x => x.AtUtc >= cutoff).ToList();
        if (pts.Count < 2) return null;
        var spanMin = (pts[^1].AtUtc - pts[0].AtUtc).TotalMinutes;
        if (spanMin < 2) return null;

        double Value(Sample s) => balanceKind ? -(s.Remaining ?? 0) : s.UsedFraction;

        var t0 = pts[0].AtUtc;
        int n = pts.Count;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in pts)
        {
            var x = (p.AtUtc - t0).TotalMinutes;
            var y = Value(p);
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return null;
        var slope = (n * sxy - sx * sy) / denom; // window: utilization/min; balance: -amount/min
        return Math.Max(0, slope);
    }
}

public sealed class WindowEvaluation
{
    public Health Health { get; set; } = Health.Unknown;
    public double RatePerMin { get; set; }
    public bool HasRate { get; set; }
    public TimeSpan? TimeToEmpty { get; set; }
    public TimeSpan? TimeToReset { get; set; }
    public string Verdict { get; set; } = "";
}

public static class Evaluator
{
    public static WindowEvaluation Evaluate(QuotaWindow w, IReadOnlyList<Sample> samples, DateTimeOffset now)
    {
        var ev = new WindowEvaluation();

        if (w.IsInfoOnly)
        {
            ev.Health = Health.Ok;
            ev.Verdict = w.Note ?? "";
            return ev;
        }

        var rate = BurnMath.SlopePerMinute(samples, w.IsBalance, now);
        ev.HasRate = rate is > 1e-9;
        ev.RatePerMin = rate ?? 0;

        if (w.ResetAtUtc is not null)
            ev.TimeToReset = w.ResetAtUtc.Value - now;

        // Zeit bis leer
        if (ev.HasRate)
        {
            if (w.IsBalance && w.Remaining is not null)
                ev.TimeToEmpty = TimeSpan.FromMinutes(w.Remaining.Value / ev.RatePerMin);
            else if (!w.IsBalance && w.UsedFraction is not null)
                ev.TimeToEmpty = TimeSpan.FromMinutes((1.0 - w.UsedFraction.Value) / ev.RatePerMin);
        }

        if (!w.IsBalance && w.UsedFraction is >= 0.999)
        {
            ev.Health = Health.Crit;
            ev.Verdict = "Limit erreicht" + (ev.TimeToReset is not null ? $" · Reset in {Formatting.Span(ev.TimeToReset)}" : "");
            return ev;
        }

        if (w.IsBalance)
        {
            if (!ev.HasRate || ev.TimeToEmpty is null)
            {
                ev.Health = Health.Ok;
                ev.Verdict = "Kein Verbrauch gemessen";
            }
            else if (ev.TimeToEmpty > TimeSpan.FromDays(2))
            {
                ev.Health = Health.Ok;
                ev.Verdict = $"Reicht noch {Formatting.Span(ev.TimeToEmpty)} bei aktuellem Verbrauch";
            }
            else if (ev.TimeToEmpty > TimeSpan.FromHours(12))
            {
                ev.Health = Health.Warn;
                ev.Verdict = $"Reicht nur noch {Formatting.Span(ev.TimeToEmpty)}";
            }
            else
            {
                ev.Health = Health.Crit;
                ev.Verdict = $"Guthaben reicht nur noch {Formatting.Span(ev.TimeToEmpty)}";
            }
            return ev;
        }

        // Window with reset
        if (!ev.HasRate || ev.TimeToEmpty is null)
        {
            ev.Health = Health.Ok;
            ev.Verdict = "Kein Verbrauch gemessen" + (ev.TimeToReset is not null ? $" · Reset in {Formatting.Span(ev.TimeToReset)}" : "");
            return ev;
        }

        if (ev.TimeToReset is null || ev.TimeToReset <= TimeSpan.Zero)
        {
            ev.Health = Health.Ok;
            ev.Verdict = $"Noch {Formatting.Span(ev.TimeToEmpty)} bei aktuellem Verbrauch";
            return ev;
        }

        var tte = ev.TimeToEmpty.Value;
        var ttr = ev.TimeToReset.Value;
        if (tte > ttr)
        {
            var knapp = tte < ttr * 1.15;
            ev.Health = knapp ? Health.Warn : Health.Ok;
            ev.Verdict = $"Noch {Formatting.Span(tte)} · Reset in {Formatting.Span(ttr)} – {(knapp ? "knapp, aber reicht" : "reicht bis zum Reset")}";
        }
        else
        {
            ev.Health = Health.Crit;
            ev.Verdict = $"Leer in {Formatting.Span(tte)} – Reset erst in {Formatting.Span(ttr)}!";
        }
        return ev;
    }
}
