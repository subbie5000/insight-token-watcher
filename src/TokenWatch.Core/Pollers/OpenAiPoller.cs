using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// OpenAI API platform. Preferred: organization Admin API key
/// (GET /v1/organization/costs, bucket_width=1d) – shows spend, no quota.
/// Fallback for legacy user keys: GET /v1/dashboard/billing/credit_grants.
/// Note: Admin keys cannot be used for inference; project service-account keys
/// cannot read organization endpoints.
/// </summary>
public sealed class OpenAiPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var baseUrl = ProviderInfo.BaseUrl(cfg);

        // 1) Admin API: organization costs (last 30 days, daily buckets)
        var start = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/v1/organization/costs?start_time={start}&bucket_width=1d&limit=31");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.IsSuccessStatusCode)
            return ParseCosts(cfg.Id, body);

        // 2) Legacy fallback: credit grants
        using var req2 = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/dashboard/billing/credit_grants");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp2 = await http.SendAsync(req2, ct);
        var body2 = await resp2.Content.ReadAsStringAsync(ct);

        if (resp2.IsSuccessStatusCode)
            return ParseCreditGrants(cfg.Id, body2);

        return AccountSnapshot.Failure(cfg.Id,
            $"Admin API: HTTP {(int)resp.StatusCode}; credit_grants: HTTP {(int)resp2.StatusCode}. " +
            "Hinweis: Org-Admin-Key (sk-admin-…) nötig; Projekt-/Service-Account-Keys reichen nicht. " +
            KimiCodePoller.Snippet(body2));
    }

    internal static AccountSnapshot ParseCosts(Guid accountId, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            double total = 0;
            var days = 0;
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var bucket in data.EnumerateArray())
                {
                    if (!bucket.TryGetProperty("results", out var results)) continue;
                    days++;
                    foreach (var r in results.EnumerateArray())
                        if (r.TryGetProperty("amount", out var amount))
                            total += ProviderInfo.JsonDouble(amount, "value") ?? 0;
                }

            var snap = new AccountSnapshot { AccountId = accountId, Ok = true };
            snap.Windows.Add(new QuotaWindow
            {
                Key = "costs30d",
                Label = "Ausgaben 30 Tage",
                IsInfoOnly = true,
                Remaining = Math.Round(total, 2),
                Unit = "USD",
                Note = $"Org-Kosten ({days} Tage mit Nutzung) – kein Limit bekannt"
            });
            return snap;
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }

    internal static AccountSnapshot ParseCreditGrants(Guid accountId, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var granted = ProviderInfo.JsonDouble(root, "total_granted");
            var used = ProviderInfo.JsonDouble(root, "total_used");
            var available = ProviderInfo.JsonDouble(root, "total_available");
            if (available is null)
                return AccountSnapshot.Failure(accountId, "Antwort ohne total_available: " + KimiCodePoller.Snippet(body));

            var snap = new AccountSnapshot { AccountId = accountId, Ok = true };
            snap.Windows.Add(new QuotaWindow
            {
                Key = "balance",
                Label = "Guthaben (credit_grants)",
                IsBalance = true,
                Remaining = available,
                Unit = "USD",
                Note = $"Gewährt {Formatting.Money(granted, "USD")} · Verbraucht {Formatting.Money(used, "USD")}"
            });
            return snap;
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }
}
