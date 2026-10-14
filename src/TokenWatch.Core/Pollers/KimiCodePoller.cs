using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// Kimi For Coding (subscription): GET https://api.kimi.com/coding/v1/usages
/// usage = weekly quota, limits[] = sub-windows (e.g. 100 requests / 5h).
/// </summary>
public sealed class KimiCodePoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var url = ProviderInfo.BaseUrl(cfg) + "/coding/v1/usages";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return AccountSnapshot.Failure(cfg.Id, $"HTTP {(int)resp.StatusCode}: {Snippet(body)}");
        return ParseBody(cfg.Id, body);
    }

    internal static AccountSnapshot ParseBody(Guid accountId, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var snap = new AccountSnapshot { AccountId = accountId, Ok = true };

            if (root.TryGetProperty("usage", out var usage))
                snap.Windows.Add(ParseUsageDetail(usage, "week", "Wochenkontingent"));

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var lim in limits.EnumerateArray())
                {
                    string label = $"Unterfenster {i + 1}";
                    string key = $"sub{i + 1}";
                    if (lim.TryGetProperty("window", out var win))
                    {
                        var dur = ProviderInfo.JsonDouble(win, "duration");
                        var unit = ProviderInfo.JsonString(win, "timeUnit") ?? "";
                        if (dur is not null)
                        {
                            var minutes = unit.Contains("HOUR") ? dur.Value * 60
                                : unit.Contains("DAY") ? dur.Value * 1440
                                : dur.Value;
                            label = minutes % 60 == 0 ? $"{(int)(minutes / 60)}-Std-Fenster" : $"{(int)minutes}-Min-Fenster";
                            key = $"{(int)minutes}m";
                        }
                    }
                    if (lim.TryGetProperty("detail", out var detail))
                        snap.Windows.Add(ParseUsageDetail(detail, key, label));
                    i++;
                }
            }

            if (snap.Windows.Count == 0)
                return AccountSnapshot.Failure(accountId, "Antwort ohne usage/limits: " + Snippet(body));
            return snap;
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }

    private static QuotaWindow ParseUsageDetail(JsonElement d, string key, string label)
    {
        var limit = ProviderInfo.JsonDouble(d, "limit");
        var used = ProviderInfo.JsonDouble(d, "used");
        var remaining = ProviderInfo.JsonDouble(d, "remaining");
        var reset = TimeParsing.ParseFlexible(ProviderInfo.JsonString(d, "resetTime"));
        double? frac = limit is > 0 && used is not null
            ? Math.Clamp(used.Value / limit.Value, 0, 1)
            : null;
        return new QuotaWindow
        {
            Key = key,
            Label = label,
            Unit = "Requests",
            Total = limit,
            Remaining = remaining,
            UsedFraction = frac,
            ResetAtUtc = reset
        };
    }

    internal static string Snippet(string body) => body.Length <= 300 ? body : body[..300];
}
