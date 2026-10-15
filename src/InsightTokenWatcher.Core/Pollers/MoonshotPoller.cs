using System.Net.Http.Headers;
using System.Text.Json;

namespace InsightTokenWatcher.Core;

/// <summary>
/// Moonshot / Kimi Open Platform (prepaid): GET {base}/v1/users/me/balance
/// International: api.moonshot.ai (USD) · China: api.moonshot.cn (CNY). No reset window.
/// </summary>
public sealed class MoonshotPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var url = ProviderInfo.BaseUrl(cfg) + "/v1/users/me/balance";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return AccountSnapshot.Failure(cfg.Id, $"HTTP {(int)resp.StatusCode}: {KimiCodePoller.Snippet(body)}");
        return ParseBody(cfg.Id, body, ProviderInfo.BaseUrl(cfg));
    }

    internal static AccountSnapshot ParseBody(Guid accountId, string body, string baseUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data)) root = data;

            var available = ProviderInfo.JsonDouble(root, "available_balance");
            if (available is null)
                return AccountSnapshot.Failure(accountId, "Antwort ohne available_balance: " + KimiCodePoller.Snippet(body));

            var voucher = ProviderInfo.JsonDouble(root, "voucher_balance");
            var cash = ProviderInfo.JsonDouble(root, "cash_balance");
            var cn = baseUrl.Contains(".cn");
            var unit = cn ? "CNY" : "USD";

            var note = $"Gutschein {Formatting.Money(voucher, unit)} · Bar {Formatting.Money(cash, unit)}";
            if (cash is < 0) note = $"Defizit {Formatting.Money(cash, unit)} · " + note;

            return new AccountSnapshot
            {
                AccountId = accountId,
                Ok = true,
                Windows =
                {
                    new QuotaWindow
                    {
                        Key = "balance",
                        Label = "Guthaben",
                        IsBalance = true,
                        Remaining = available,
                        Unit = unit,
                        Note = note
                    }
                }
            };
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }
}
