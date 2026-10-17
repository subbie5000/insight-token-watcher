using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Linux;

/// <summary>Synthetic rows for --render previews and the offscreen visual check.
/// Same scenarios as the Windows harness: exhausted, tight, stale, balance and error rows.</summary>
internal static class DemoData
{
    public static IReadOnlyList<AccountView> Rows(DateTimeOffset now, double tick = 0)
    {
        QuotaWindow W(string key, string label, double minutes, double used, DateTimeOffset reset,
            double ratePerMin, double total = 0, string? note = null) => new()
        {
            Key = key, Label = label, WindowMinutes = minutes,
            UsedFraction = used, Total = total > 0 ? total : null,
            ResetAtUtc = reset, Note = note
        };

        WindowEvaluation E(Health h, double rate, TimeSpan? tte, TimeSpan? ttr, string verdict) => new()
        {
            Health = h, RatePerMin = rate, HasRate = rate > 0,
            TimeToEmpty = tte, TimeToReset = ttr, Verdict = verdict
        };

        AccountView V(string name, ProviderKind provider, Health health, FailureKind failure,
            params QuotaWindow[] windows)
        {
            var okSnapshot = new AccountSnapshot { Ok = true, AtUtc = now, Windows = windows.ToList() };
            var snapshot = failure == FailureKind.None
                ? okSnapshot
                : new AccountSnapshot
                {
                    Ok = false, AtUtc = now,
                    Error = failure switch
                    {
                        FailureKind.AuthExpired => "OAuth access token has expired.",
                        _ => "HTTP 401 – keine Rate-Limit-Header. {\"error\":{\"message\":\"Invalid API Key\"}}"
                    }
                };
            return new AccountView
            {
                Config = new AccountConfig { Name = name, Provider = provider },
                Snapshot = snapshot,
                LastGood = failure != FailureKind.None && windows.Length > 0 ? okSnapshot : null,
                Evals = windows.ToDictionary(w => w.Key, w =>
                {
                    TimeSpan? ttr = w.ResetAtUtc is { } r ? (TimeSpan?)(r - now) : null;
                    var tte = w.IsBalance
                        ? (TimeSpan?)TimeSpan.FromDays((w.Remaining ?? 0) / 0.85)
                        : w.UsedFraction is { } u && u < 1
                            ? (TimeSpan?)TimeSpan.FromMinutes((1 - u) * (w.WindowMinutes ?? 300) / (0.002 + 0.000001))
                            : null;
                    return E(Health.Unknown, 0, tte, ttr, "");
                }),
                Health = health,
                Failure = failure,
                ConsecutiveFailures = failure == FailureKind.None ? 0 : 3
            };
        }

        var list = new List<AccountView>
        {
            V("Anthropic 1", ProviderKind.Anthropic, Health.Crit, FailureKind.None,
                W("5h", "5-Std-Fenster", 300, 0.12, now.AddHours(3.6), 0.0004),
                W("7d", "7-Tage-Fenster", 10080, 1.0, now.AddHours(27), 0, note: "abgelehnt")),
            V("Anthropic 2", ProviderKind.Anthropic, Health.Crit, FailureKind.None,
                W("5h", "5-Std-Fenster", 300, 0.22 + tick * 0.002, now.AddHours(4.05), 0.0011),
                W("7d", "7-Tage-Fenster", 10080, 0.90, now.AddHours(114), 0.0001, note: "Warnstufe")),
            V("Anthropic 3", ProviderKind.Anthropic, Health.Ok, FailureKind.None,
                W("5h", "5-Std-Fenster", 300, 0.03, now.AddHours(4.8), 0),
                W("7d", "7-Tage-Fenster", 10080, 0.08, now.AddHours(146), 0)),
            V("Anthropic 4", ProviderKind.Anthropic, Health.Crit, FailureKind.AuthExpired,
                W("5h", "5-Std-Fenster", 300, 0.0, now.AddHours(4.9), 0),
                W("7d", "7-Tage-Fenster", 10080, 1.0, now.AddHours(100), 0, note: "abgelehnt")),
            V("Kimi Code", ProviderKind.KimiCode, Health.Ok, FailureKind.None,
                W("week", "Wochenkontingent", 10080, 0.45, now.AddHours(145), 0.00002, total: 100),
                W("300m", "5-Std-Fenster", 300, 0.20, now.AddHours(4.1), 0.0006, total: 100)),
            V("GLM Coding Plan", ProviderKind.Zai, Health.Warn, FailureKind.None,
                W("c1", "5-Std-Kontingent", 300, 0.585, now.AddHours(2.6), 0.00244, total: 28000, note: "Plan: max"),
                W("c2", "Wochen-Kontingent", 10080, 0.563, now.AddHours(94), 0.00005, total: 140000, note: "Plan: max")),
            V("DeepSeek", ProviderKind.DeepSeek, Health.Ok, FailureKind.None,
                new QuotaWindow { Key = "balance", Label = "Guthaben", IsBalance = true, Remaining = 23.42, Unit = "USD", Note = "Bezahlt 23,42 $ · Gratis 0,00 $" }),
            V("OpenRouter", ProviderKind.OpenRouter, Health.Ok, FailureKind.None,
                new QuotaWindow { Key = "balance", Label = "Guthaben", IsBalance = true, Remaining = 0.59, Unit = "USD", Note = "Gekauft 10,00 $ · Verbraucht 9,41 $" }),
            V("Groq", ProviderKind.GenericOpenAi, Health.Crit, FailureKind.AuthInvalid)
        };
        return list;
    }
}
