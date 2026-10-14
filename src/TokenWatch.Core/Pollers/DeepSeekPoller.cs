using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// DeepSeek (Prepaid): GET {base}/user/balance
/// balance_infos[] per currency (total_balance, granted_balance, topped_up_balance). No reset window.
/// </summary>
public sealed class DeepSeekPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var url = ProviderInfo.BaseUrl(cfg) + "/user/balance";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return AccountSnapshot.Failure(cfg.Id, $"HTTP {(int)resp.StatusCode}: {KimiCodePoller.Snippet(body)}");
        return ParseBody(cfg.Id, body);
    }

    internal static AccountSnapshot ParseBody(Guid accountId, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var isAvailable = root.TryGetProperty("is_available", out var ia) && ia.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array ||
                infos.GetArrayLength() == 0)
                return AccountSnapshot.Failure(accountId, "Antwort ohne balance_infos: " + KimiCodePoller.Snippet(body));

            var snap = new AccountSnapshot { AccountId = accountId, Ok = true };
            var all = infos.EnumerateArray().ToList();
            var main = all.FirstOrDefault(e =>
                ProviderInfo.JsonString(e, "currency")?.Equals("USD", StringComparison.OrdinalIgnoreCase) == true);
            if (main.ValueKind == JsonValueKind.Undefined) main = all[0];

            var cur = ProviderInfo.JsonString(main, "currency") ?? "USD";
            var total = ProviderInfo.JsonDouble(main, "total_balance");
            var granted = ProviderInfo.JsonDouble(main, "granted_balance");
            var topped = ProviderInfo.JsonDouble(main, "topped_up_balance");

            var note = $"Bezahlt {Formatting.Money(topped, cur)} · Gratis {Formatting.Money(granted, cur)}";
            if (!isAvailable) note = "Guthaben für API derzeit nicht nutzbar · " + note;
            if (all.Count > 1) note += $" · {all.Count} Währungen";

            snap.Windows.Add(new QuotaWindow
            {
                Key = "balance",
                Label = "Guthaben",
                IsBalance = true,
                Remaining = total,
                Unit = cur,
                Note = note
            });
            return snap;
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }
}
