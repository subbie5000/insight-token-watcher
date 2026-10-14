namespace TokenWatch.Core;

/// <summary>How a row presents an account's windows: short window | long window, or one spanning value.</summary>
public sealed class RowLayout
{
    public QuotaWindow? Short { get; set; }
    public QuotaWindow? Long { get; set; }

    /// <summary>Balance or info value shown across both cells.</summary>
    public QuotaWindow? Span { get; set; }

    public List<QuotaWindow> Extras { get; } = new();
}

public static class WindowLayout
{
    public const double DayMinutes = 1440;

    /// <summary>
    /// Short windows (≤ 1 day, e.g. 5 h) go left, long windows (week) right, so the same
    /// kind of window lines up across all accounts. Balances/info span both cells.
    /// </summary>
    public static RowLayout Classify(IReadOnlyList<QuotaWindow> windows)
    {
        var r = new RowLayout
        {
            Span = windows.FirstOrDefault(w => w.IsBalance) ?? windows.FirstOrDefault(w => w.IsInfoOnly)
        };

        foreach (var w in windows)
        {
            if (ReferenceEquals(w, r.Span)) continue;
            if (r.Span is not null || w.IsBalance || w.IsInfoOnly)
            {
                r.Extras.Add(w);
                continue;
            }

            if (w.WindowMinutes is { } m)
            {
                if (m <= DayMinutes)
                {
                    if (r.Short is null) r.Short = w; else r.Extras.Add(w);
                }
                else
                {
                    if (r.Long is null) r.Long = w; else r.Extras.Add(w);
                }
            }
            else if (r.Short is null) r.Short = w;
            else if (r.Long is null) r.Long = w;
            else r.Extras.Add(w);
        }
        return r;
    }

    /// <summary>Compact cell label: "5 h", "Woche", "Tag", …; falls back to the window label.</summary>
    public static string ShortLabel(QuotaWindow w)
    {
        if (w.IsBalance) return "Guthaben";
        if (w.WindowMinutes is not { } m || m <= 0) return w.Label;
        if (Math.Abs(m - 10080) < 1) return "Woche";
        if (Math.Abs(m - 1440) < 1) return "Tag";
        if (Math.Abs(m - 43200) < 1) return "Monat";
        if (m % 1440 == 0) return $"{m / 1440:0} Tage";
        if (m % 60 == 0) return $"{m / 60:0} h";
        return $"{m:0} min";
    }
}
