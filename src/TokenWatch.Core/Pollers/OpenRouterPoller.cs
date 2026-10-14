using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// OpenRouter: GET /api/v1/key (key limit + spend) and GET /api/v1/credits
/// (total_credits / total_usage → balance). Both with the regular API key.
/// </summary>
public sealed class OpenRouterPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var baseUrl = ProviderInfo.BaseUrl(cfg);
        var snap = new AccountSnapshot { AccountId = cfg.Id, Ok = true };

        // 1) key info: limit + spend
        using (var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/key"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return AccountSnapshot.Failure(cfg.Id, $"HTTP {(int)resp.StatusCode} (/api/v1/key): {KimiCodePoller.Snippet(body)}");
            try
            {
                using var doc = JsonDocument.Parse(body);
                var d = doc.RootElement.GetProperty("data");
                var limit = ProviderInfo.JsonDouble(d, "limit");
                var remaining = ProviderInfo.JsonDouble(d, "limit_remaining");
                var day = ProviderInfo.JsonDouble(d, "usage_daily");
                var week = ProviderInfo.JsonDouble(d, "usage_weekly");
                var month = ProviderInfo.JsonDouble(d, "usage_monthly");
                var freeTier = d.TryGetProperty("is_free_tier", out var ft) && ft.ValueKind == JsonValueKind.True;

                var spendNote = $"Tag {Formatting.Money(day, "USD")} · Woche {Formatting.Money(week, "USD")} · Monat {Formatting.Money(month, "USD")}";
                if (freeTier) spendNote = "Free Tier · " + spendNote;

                if (limit is > 0)
                {
                    snap.Windows.Add(new QuotaWindow
                    {
                        Key = "key-limit",
                        Label = "Key-Limit (Ausgaben-Obergrenze)",
                        Unit = "USD",
                        Total = limit,
                        Remaining = remaining,
                        UsedFraction = remaining is not null && limit > 0
                            ? Math.Clamp(1 - remaining.Value / limit.Value, 0, 1) : null,
                        Note = spendNote
                    });
                }
                else
                {
                    snap.Windows.Add(new QuotaWindow
                    {
                        Key = "key-info",
                        Label = "Key (kein Limit gesetzt)",
                        IsInfoOnly = true,
                        Note = spendNote
                    });
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return AccountSnapshot.Failure(cfg.Id, "JSON-Fehler (/api/v1/key): " + ex.Message);
            }
        }

        // 2) credits → balance (may be rejected for some keys; then keep key data only)
        using (var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/credits"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var d = doc.RootElement.GetProperty("data");
                    var credits = ProviderInfo.JsonDouble(d, "total_credits");
                    var usage = ProviderInfo.JsonDouble(d, "total_usage");
                    if (credits is not null && usage is not null)
                        snap.Windows.Add(new QuotaWindow
                        {
                            Key = "balance",
                            Label = "Guthaben",
                            IsBalance = true,
                            Remaining = Math.Round(credits.Value - usage.Value, 2),
                            Unit = "USD",
                            Note = $"Gekauft {Formatting.Money(credits, "USD")} · Verbraucht {Formatting.Money(usage, "USD")}"
                        });
                }
                catch (JsonException) { /* keep key info */ }
            }
        }

        return snap.Windows.Count > 0
            ? snap
            : AccountSnapshot.Failure(cfg.Id, "Keine verwertbaren Daten von /api/v1/key und /api/v1/credits");
    }
}
