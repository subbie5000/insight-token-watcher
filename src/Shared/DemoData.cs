using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Demo;

// Linked into the Windows and the Linux app (see the csproj files): both render harnesses draw
// the same accounts, evaluated by the real Evaluator.

/// <summary>Synthetic accounts covering every visual state (no real data).</summary>
internal static class DemoData
{
    /// <param name="fable">false: no account reports a model-scoped limit (two-column layout).</param>
    public static IReadOnlyList<AccountView> Build(DateTimeOffset now, int tick, bool fable = true)
    {
        // the Fable week resets together with the general week
        QuotaWindow[] Claude(double fableUsed, double weekResetHours, params QuotaWindow[] general) => fable
            ? general.Append(Read(W("7d_fable", "Fable-Woche", 10080, fableUsed, now.AddHours(weekResetHours), 0, scope: "Fable"),
                now.AddMinutes(-4))).ToArray()
            : general;

        var list = new List<AccountView>
        {
            Stale("Anthropic 1", ProviderKind.Anthropic, now, FailureKind.AuthExpired,
                "HTTP 401 – keine Rate-Limit-Header. {\"type\":\"error\",\"error\":{\"message\":\"OAuth access token has expired.\"}}",
                Claude(0.35, 100,
                    W("5h", "5-Std-Fenster", 300, 0.0, now.AddHours(4.9), 0),
                    W("7d", "7-Tage-Fenster", 10080, 1.0, now.AddHours(100), 0, "abgelehnt"))),
            Quota("Anthropic 2", ProviderKind.Anthropic, now, Claude(0.0, 27,
                W("5h", "5-Std-Fenster", 300, 0.12, now.AddHours(3.6), 0.0004),
                W("7d", "7-Tage-Fenster", 10080, 1.0, now.AddHours(27), 0, "abgelehnt"))),
            Quota("Anthropic 3", ProviderKind.Anthropic, now, Claude(0.62, 114,
                W("5h", "5-Std-Fenster", 300, 0.22 + tick * 0.002, now.AddHours(4.05), 0.0011),
                W("7d", "7-Tage-Fenster", 10080, 0.90, now.AddHours(114), 0.0001, "Warnstufe"))),
            // healthy account whose Fable week is used up: stays "OK", the cell and a line say it
            Quota("Anthropic 4", ProviderKind.Anthropic, now, Claude(1.0, 146,
                W("5h", "5-Std-Fenster", 300, 0.03, now.AddHours(4.8), 0),
                W("7d", "7-Tage-Fenster", 10080, 0.08, now.AddHours(146), 0))),
            // the owning tool renewed the token between two syncs: greyed out, old values, no alarm
            Stale("Anthropic 5", ProviderKind.Anthropic, now, FailureKind.AuthInvalid,
                "HTTP 401 – keine Rate-Limit-Header. {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"OAuth access token has been revoked.\"}}",
                Claude(0.62, 120,
                    W("5h", "5-Std-Fenster", 300, 0.30, now.AddHours(4.4), 0),
                    W("7d", "7-Tage-Fenster", 10080, 0.79, now.AddHours(120), 0))),
            Quota("Kimi Code", ProviderKind.KimiCode, now,
                W("week", "Wochenkontingent", 10080, 0.45, now.AddHours(145), 0.00002, total: 100),
                W("300m", "5-Std-Fenster", 300, 0.20, now.AddHours(4.1), 0.0006, total: 100)),
            Quota("GLM Coding Plan", ProviderKind.Zai, now,
                W("c1", "5-Std-Kontingent", 300, 0.585, now.AddHours(2.6), 0.00244, total: 28000, note: "Plan: max"),
                W("c2", "Wochen-Kontingent", 10080, 0.563, now.AddHours(94), 0.00005, total: 140000, note: "Plan: max")),
            Balance("DeepSeek", ProviderKind.DeepSeek, now, 23.42, 0.0006, "Bezahlt 23,42 $ · Gratis 0,00 $"),
            BalanceWithExtra("OpenRouter", ProviderKind.OpenRouter, now, 0.59, "Gekauft 10,00 $ · Verbraucht 9,41 $"),
            // prepaid balance used up: cannot be used until it is topped up
            Balance("Moonshot", ProviderKind.Moonshot, now, -0.01, 0, "Defizit -0,01 $ · Gutschein 0,00 $ · Bar -0,01 $",
                unusable: true),
            Error("Groq", ProviderKind.GenericOpenAi,
                "HTTP 401 – keine x-ratelimit-*-Header. {\"error\":{\"message\":\"Invalid API Key\"}}")
        };
        return list;
    }

    private static readonly Dictionary<QuotaWindow, double> Rates = new();

    /// <summary>A value that was read before the snapshot it is shown with (model limits).</summary>
    private static QuotaWindow Read(QuotaWindow w, DateTimeOffset at)
    {
        w.MeasuredAtUtc = at;
        return w;
    }

    private static QuotaWindow W(string key, string label, double minutes, double used, DateTimeOffset reset,
        double ratePerMin, string? note = null, double? total = null, string? scope = null)
    {
        var w = new QuotaWindow
        {
            Key = key,
            Label = label,
            Scope = scope,
            WindowMinutes = minutes,
            UsedFraction = used,
            ResetAtUtc = reset,
            Total = total,
            Remaining = total is null ? null : Math.Round(total.Value * (1 - used)),
            Unit = total is null ? "Quota" : "Requests",
            Note = note
        };
        Rates[w] = ratePerMin;
        return w;
    }

    private static (AccountSnapshot snap, Dictionary<string, WindowEvaluation> evals, Health health, List<Sample> series)
        Evaluate(Guid id, DateTimeOffset now, IEnumerable<QuotaWindow> windows)
    {
        var snap = new AccountSnapshot { AccountId = id, AtUtc = now, Ok = true };
        var evals = new Dictionary<string, WindowEvaluation>();
        var health = Health.Ok;
        List<Sample> primary = new();
        foreach (var w in windows)
        {
            Rates.TryGetValue(w, out var rate);
            var samples = new List<Sample>();
            for (var m = 60; m >= 0; m -= 5)
            {
                var t = now.AddMinutes(-m);
                samples.Add(w.IsBalance
                    ? new Sample(t, 0, (w.Remaining ?? 0) + rate * m)
                    : new Sample(t, Math.Max(0, (w.UsedFraction ?? 0) - rate * m), null));
            }
            snap.Windows.Add(w);
            var ev = Evaluator.Evaluate(w, samples, now);
            evals[w.Key] = ev;
            if (w.Scope is null && ev.Health > health) health = ev.Health;
            if (primary.Count == 0) primary = samples;
        }
        // like the engine: a model limit marks a healthy account amber, never critical
        if (health == Health.Ok && snap.Windows.Any(w => w.Scope is not null && evals[w.Key].Health is Health.Warn or Health.Crit))
            health = Health.Warn;
        return (snap, evals, health, primary);
    }

    private static AccountView Quota(string name, ProviderKind p, DateTimeOffset now, params QuotaWindow[] windows)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        var (snap, evals, health, series) = Evaluate(cfg.Id, now, windows);
        return new AccountView
        {
            Config = cfg, Snapshot = snap, LastGood = snap, Evals = evals, Health = health, PrimarySeries = series
        };
    }

    private static AccountView Stale(string name, ProviderKind p, DateTimeOffset now, FailureKind kind, string error,
        params QuotaWindow[] windows)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        var (good, evals, _, series) = Evaluate(cfg.Id, now.AddMinutes(-37), windows);
        return new AccountView
        {
            Config = cfg,
            Snapshot = AccountSnapshot.Failure(cfg.Id, error),
            LastGood = good,
            Evals = evals,
            Health = kind == FailureKind.AuthExpired ? Health.Unknown : Health.Crit, // as the engine sets it
            Failure = kind,
            ConsecutiveFailures = 2,
            PrimarySeries = series
        };
    }

    private static AccountView Balance(string name, ProviderKind p, DateTimeOffset now, double amount, double ratePerMin,
        string note, bool unusable = false)
    {
        var w = new QuotaWindow
        {
            Key = "balance", Label = "Guthaben", IsBalance = true, Remaining = amount, Unit = "USD", Note = note,
            Unusable = unusable
        };
        Rates[w] = ratePerMin;
        return Quota(name, p, now, w);
    }

    private static AccountView BalanceWithExtra(string name, ProviderKind p, DateTimeOffset now, double amount, string note)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        var bal = new QuotaWindow { Key = "balance", Label = "Guthaben", IsBalance = true, Remaining = amount, Unit = "USD", Note = note };
        var key = new QuotaWindow
        {
            Key = "key-info", Label = "Key (kein Limit gesetzt)", IsInfoOnly = true,
            Note = "Tag 0,00 $ · Woche 0,00 $ · Monat 0,00 $"
        };
        var (snap, evals, health, series) = Evaluate(cfg.Id, now, new[] { key, bal });
        return new AccountView
        {
            Config = cfg, Snapshot = snap, LastGood = snap, Evals = evals, Health = health, PrimarySeries = series
        };
    }

    private static AccountView Error(string name, ProviderKind p, string error)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        return new AccountView
        {
            Config = cfg,
            Snapshot = AccountSnapshot.Failure(cfg.Id, error),
            Health = Health.Crit,
            Failure = FailureKind.AuthInvalid,
            ConsecutiveFailures = 1
        };
    }
}
