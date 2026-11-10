namespace InsightTokenWatcher.Core;

/// <summary>How a row presents an account's windows: short window | long window, or one spanning value.</summary>
public sealed class RowLayout
{
    public QuotaWindow? Short { get; set; }
    public QuotaWindow? Long { get; set; }

    /// <summary>Balance or info value shown across both cells.</summary>
    public QuotaWindow? Span { get; set; }

    /// <summary>Model-scoped window for the model column (e.g. the Fable week).</summary>
    public QuotaWindow? Model { get; set; }

    public List<QuotaWindow> Extras { get; } = new();
}

public static class WindowLayout
{
    public const double DayMinutes = 1440;

    /// <summary>
    /// The model family the model column stands for: the scope most accounts report (today
    /// "Fable"). Null = nobody reports a model-scoped limit → there is no such column.
    /// </summary>
    public static string? ColumnScope(IEnumerable<AccountView> views) =>
        views.SelectMany(v => v.Display?.Windows ?? Enumerable.Empty<QuotaWindow>())
            .Where(w => w.Scope is { Length: > 0 } && (w.WindowMinutes ?? 0) > DayMinutes)
            .GroupBy(w => w.Scope!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().Scope)
            .FirstOrDefault();

    /// <summary>
    /// Short windows (≤ 1 day, e.g. 5 h) go left, long windows (week) right, so the same
    /// kind of window lines up across all accounts. Balances/info span both cells. A long
    /// window limited to <paramref name="columnScope"/> goes to the model column; other
    /// model-scoped windows are extras – they never take the place of a general window.
    /// </summary>
    public static RowLayout Classify(IReadOnlyList<QuotaWindow> windows, string? columnScope = null)
    {
        var r = new RowLayout
        {
            Span = windows.FirstOrDefault(w => w.IsBalance) ?? windows.FirstOrDefault(w => w.IsInfoOnly)
        };

        var unknown = new List<QuotaWindow>();
        foreach (var w in windows)
        {
            if (ReferenceEquals(w, r.Span)) continue;
            if (r.Span is not null || w.IsBalance || w.IsInfoOnly)
            {
                r.Extras.Add(w);
                continue;
            }

            if (w.Scope is not null)
            {
                if (r.Model is null && columnScope is not null && (w.WindowMinutes ?? 0) > DayMinutes &&
                    w.Scope.Equals(columnScope, StringComparison.OrdinalIgnoreCase))
                    r.Model = w;
                else
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
            else unknown.Add(w);
        }

        // windows of unknown length only fill what is left – before, one listed first (e.g. a
        // monthly MCP quota without a known unit) took the "5 h" column from the real 5 h window
        foreach (var w in unknown)
        {
            if (r.Short is null) r.Short = w;
            else if (r.Long is null) r.Long = w;
            else r.Extras.Add(w);
        }
        return r;
    }

    /// <summary>
    /// The model limit that explains a warning when the general windows are fine (e.g. the
    /// Fable week is used up while 5 h and week have room); null when a general window is
    /// the reason or nothing is wrong. Lets the row say "FABLE LEER" instead of "KNAPP".
    /// </summary>
    public static QuotaWindow? ModelLimitBehind(AccountView v)
    {
        if (v.Display is not { } snap) return null;
        bool Bad(QuotaWindow w) => v.Evals.TryGetValue(w.Key, out var ev) && ev.Health is Health.Warn or Health.Crit;
        if (snap.Windows.Any(w => w.Scope is null && Bad(w))) return null;
        return snap.Windows.FirstOrDefault(w => w.Scope is not null && Bad(w));
    }

    /// <summary>Second line of a healthy row: the provider – or, when that would only repeat
    /// the account name ("DeepSeek / DeepSeek"), what the account holds.</summary>
    public static string Subline(AccountConfig cfg, RowLayout layout)
    {
        var provider = ProviderInfo.DisplayName(cfg.Provider);
        var name = cfg.Name.Trim();
        var repeats = name.Length > 0 && (provider.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
                                          name.StartsWith(provider, StringComparison.OrdinalIgnoreCase));
        if (!repeats) return provider;
        return layout.Span is { IsBalance: true } ? "Guthaben" : layout.Span is not null ? "Erreichbarkeit" : "Kontingent";
    }

    /// <summary>
    /// Share of the window's time that has already passed (0..1): a week that resets in two days
    /// is 5/7 through. Drawn as a tick on the usage bar – a bar that ends left of the tick is
    /// below the pace of time. This is the AVERAGE pace since the window started; the forecast
    /// (burn rate of the last hour) stays the authority for warnings. Null when length or reset
    /// are unknown, the reset has passed (old values) or lies further away than one window.
    /// </summary>
    public static double? ElapsedFraction(QuotaWindow w, DateTimeOffset now)
    {
        if (w.IsBalance || w.IsInfoOnly || w.WindowMinutes is not { } minutes || minutes < 5) return null;
        if (w.ResetAtUtc is not { } reset) return null;
        var left = (reset - now).TotalMinutes;
        if (left <= 0 || left > minutes * 1.02) return null;
        return Math.Clamp(1 - left / minutes, 0, 1);
    }

    /// <summary>"FABLE LEER" (limit reached) or "FABLE KNAPP" (runs out before its reset).</summary>
    public static string ModelLimitStatus(QuotaWindow w) =>
        $"{w.Scope} {(w.UsedFraction is >= 0.999 ? "leer" : "knapp")}".ToUpperInvariant();

    /// <summary>Compact cell label: "5 h", "Woche", "Tag", …; falls back to the window label.</summary>
    public static string ShortLabel(QuotaWindow w)
    {
        if (w.IsBalance) return "Guthaben";
        // "Fable" – not "Woche", which would read like the general week window
        if (w.Scope is { Length: > 0 } scope) return (w.WindowMinutes ?? 0) > DayMinutes ? scope : w.Label;
        if (w.WindowMinutes is not { } m || m <= 0) return w.Label;
        if (Math.Abs(m - 10080) < 1) return "Woche";
        if (Math.Abs(m - 1440) < 1) return "Tag";
        if (Math.Abs(m - 43200) < 1) return "Monat";
        if (m % 1440 == 0) return $"{m / 1440:0} Tage";
        if (m % 60 == 0) return $"{m / 60:0} h";
        return $"{m:0} min";
    }
}
