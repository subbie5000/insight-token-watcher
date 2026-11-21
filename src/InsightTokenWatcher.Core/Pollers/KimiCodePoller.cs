using System.Net.Http.Headers;
using System.Text.Json;

namespace InsightTokenWatcher.Core;

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
                snap.Windows.Add(ParseUsageDetail(usage, "week", "Wochenkontingent", 10080));

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var lim in limits.EnumerateArray())
                {
                    string label = $"Unterfenster {i + 1}";
                    string key = $"sub{i + 1}";
                    double? windowMinutes = null;
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
                            windowMinutes = minutes;
                        }
                    }
                    if (lim.TryGetProperty("detail", out var detail))
                        snap.Windows.Add(ParseUsageDetail(detail, key, label, windowMinutes));
                    i++;
                }
            }

            if (snap.Windows.Count == 0)
                return AccountSnapshot.Failure(accountId, "Antwort ohne usage/limits: " + Snippet(body));

            // Since ~Nov 2026 the response also carries "usages": exact ratios per window, the
            // same figures kimi.com shows. The legacy fields above count whole requests, so a
            // week with 0.38 of a request used arrived as 0 % – the ratios win where they exist.
            if (root.TryGetProperty("usages", out var ratios) && ratios.ValueKind == JsonValueKind.Object)
                ApplyRatios(snap, ratios);

            return snap;
        }
        catch (JsonException ex)
        {
            return AccountSnapshot.Failure(accountId, "JSON-Fehler: " + ex.Message);
        }
    }

    private static QuotaWindow ParseUsageDetail(JsonElement d, string key, string label, double? windowMinutes)
    {
        var limit = ProviderInfo.JsonDouble(d, "limit");
        var used = ProviderInfo.JsonDouble(d, "used");
        var remaining = ProviderInfo.JsonDouble(d, "remaining");
        // proto3 JSON omits zero values: an unused window has no "used" field at all
        if (used is null && limit is > 0)
            used = remaining is not null ? Math.Max(0, limit.Value - remaining.Value) : 0;
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
            ResetAtUtc = reset,
            WindowMinutes = windowMinutes
        };
    }

    /// <summary>
    /// <c>usages.limit_5h / limit_7d</c> (and any other <c>limit_&lt;n&gt;[hd]</c>): used_ratio 0..1 plus
    /// reset_time. A window that already exists gets the precise ratio and reset; the rounded
    /// request counts are dropped, because "0.38 %" next to "noch 100 von 100 Requests" would
    /// contradict itself. Unknown windows are added.
    /// </summary>
    private static void ApplyRatios(AccountSnapshot snap, JsonElement ratios)
    {
        foreach (var p in ratios.EnumerateObject())
        {
            var m = System.Text.RegularExpressions.Regex.Match(p.Name, @"^limit_(\d+)([hd])$");
            if (!m.Success || p.Value.ValueKind != JsonValueKind.Object) continue;
            var minutes = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) *
                          (m.Groups[2].Value == "h" ? 60 : 1440);
            var ratio = ProviderInfo.JsonDouble(p.Value, "used_ratio");
            if (ratio is null) continue;
            var reset = TimeParsing.ParseFlexible(ProviderInfo.JsonString(p.Value, "reset_time"));

            var w = snap.Windows.FirstOrDefault(x => x.WindowMinutes is { } wm && Math.Abs(wm - minutes) < 1);
            if (w is null)
            {
                var label = Math.Abs(minutes - 10080) < 1 ? "Wochenkontingent"
                    : minutes % 1440 == 0 ? $"{minutes / 1440:0}-Tage-Fenster"
                    : minutes % 60 == 0 ? $"{minutes / 60:0}-Std-Fenster"
                    : $"{minutes:0}-Min-Fenster";
                w = new QuotaWindow
                {
                    Key = Math.Abs(minutes - 10080) < 1 ? "week" : $"{(int)minutes}m",
                    Label = label,
                    WindowMinutes = minutes
                };
                snap.Windows.Add(w);
            }
            w.UsedFraction = Math.Clamp(ratio.Value, 0, 1);
            if (reset is not null) w.ResetAtUtc = reset;
            w.Total = null;
            w.Remaining = null;
        }
    }

    internal static string Snippet(string body) => body.Length <= 300 ? body : body[..300];
}
