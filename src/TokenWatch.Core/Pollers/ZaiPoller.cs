using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// GLM Coding Plan / z.ai / BigModel: GET {base}/api/monitor/usage/quota/limit
/// Global: api.z.ai · China: open.bigmodel.cn. Response: data.limits[] with
/// type (TOKENS_LIMIT/CREDIT_LIMIT/TIME_LIMIT), percentage, currentValue/usage/remaining,
/// nextResetTime (epoch ms), window{unit,number}; data.planName.
/// </summary>
public sealed class ZaiPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var url = ProviderInfo.BaseUrl(cfg) + "/api/monitor/usage/quota/limit";
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

            // z.ai/BigModel reports auth errors as HTTP 200 with {"code":401,"success":false}
            if (root.TryGetProperty("success", out var succ) && succ.ValueKind == JsonValueKind.False)
            {
                var msg = ProviderInfo.JsonString(root, "msg", "message", "error") ?? body;
                return AccountSnapshot.Failure(accountId, "API-Fehler: " + msg);
            }
            if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number &&
                codeEl.TryGetInt32(out var code) && code is not 0 and not 200)
            {
                var msg = ProviderInfo.JsonString(root, "msg", "message", "error") ?? "";
                return AccountSnapshot.Failure(accountId, $"API-Fehler {code}: {msg}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return AccountSnapshot.Failure(accountId, "Antwort ohne data: " + KimiCodePoller.Snippet(body));

            var plan = ProviderInfo.JsonString(data, "planName", "plan", "plan_type", "packageName", "level");
            var snap = new AccountSnapshot { AccountId = accountId, Ok = true };

            if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var lim in limits.EnumerateArray())
                {
                    i++;
                    var type = ProviderInfo.JsonString(lim, "type") ?? "LIMIT";
                    var pct = ProviderInfo.JsonDouble(lim, "percentage");
                    var usage = ProviderInfo.JsonDouble(lim, "usage");
                    var current = ProviderInfo.JsonDouble(lim, "currentValue");
                    var remaining = ProviderInfo.JsonDouble(lim, "remaining");
                    var resetStr = ProviderInfo.JsonString(lim, "nextResetTime");
                    DateTimeOffset? reset = null;
                    if (resetStr is not null && long.TryParse(resetStr, out var ms))
                        reset = TimeParsing.FromEpochGuess(ms);
                    else if (resetStr is not null)
                        reset = TimeParsing.ParseFlexible(resetStr);

                    double? frac = null;
                    if (usage is > 0 && (current is not null || remaining is not null))
                    {
                        var usedVal = current ?? (usage.Value - (remaining ?? 0));
                        frac = Math.Clamp(usedVal / usage.Value, 0, 1);
                    }
                    else if (pct is not null)
                    {
                        frac = Math.Clamp(pct.Value / 100.0, 0, 1);
                    }

                    var label = type switch
                    {
                        "TOKENS_LIMIT" => "Token-Kontingent",
                        "CREDIT_LIMIT" => "Credit-Kontingent",
                        "TIME_LIMIT" => "MCP / Zeitfenster",
                        _ => type
                    };
                    // z.ai puts unit/number directly on the limit entry (unit 3=hours, 4=days, 6=weeks)
                    var wUnit = ProviderInfo.JsonDouble(lim, "unit");
                    var wNumber = ProviderInfo.JsonDouble(lim, "number");
                    if (wUnit is not null && wNumber is not null)
                    {
                        label = ((int)wUnit.Value, wNumber.Value) switch
                        {
                            (3, 5) => "5-Std-Kontingent",
                            (3, var n) => $"{n:0}-Std-Kontingent",
                            (4, 1) => "Tages-Kontingent",
                            (4, var n) => $"{n:0}-Tage-Kontingent",
                            (6, _) => "Wochen-Kontingent",
                            _ => label
                        };
                    }
                    else if (lim.TryGetProperty("window", out var win))
                    {
                        var num = ProviderInfo.JsonDouble(win, "number");
                        var unit = ProviderInfo.JsonString(win, "unit") ?? "";
                        if (num is not null && !string.IsNullOrEmpty(unit))
                            label += $" ({num:0} {unit})";
                    }

                    snap.Windows.Add(new QuotaWindow
                    {
                        Key = $"{type}-{i}",
                        Label = label,
                        Unit = type == "CREDIT_LIMIT" ? "Credits" : "Tokens",
                        UsedFraction = frac,
                        Total = usage,
                        Remaining = remaining ?? (usage is > 0 && frac is not null ? usage.Value * (1 - frac.Value) : null),
                        ResetAtUtc = reset,
                        Note = plan is not null ? $"Plan: {plan}" : null
                    });
                }
            }

            if (snap.Windows.Count == 0)
                return AccountSnapshot.Failure(accountId,
                    plan is not null ? $"Keine Limits gemeldet (Plan: {plan})" : "Keine Limits in Antwort: " + KimiCodePoller.Snippet(body));
            return snap;
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }
}
