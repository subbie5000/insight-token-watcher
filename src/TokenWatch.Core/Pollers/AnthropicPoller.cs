using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// Anthropic: a mini call to /v1/messages (max_tokens=1, Haiku ≈ 1–2 ct/month at 1/min) –
/// the unified rate-limit headers ride on EVERY response, including 429s.
/// anthropic-ratelimit-unified-{5h,7d,7d_sonnet}-{status,reset(epoch s),utilization(0..1)}
/// </summary>
public sealed class AnthropicPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var url = ProviderInfo.BaseUrl(cfg) + "/v1/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (cfg.Auth == AuthKind.Bearer)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("user-agent", "claude-cli/2.0 (external, tokenwatch)");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("x-api-key", plainKey);
        }
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = cfg.EffectiveModel,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "hi" } }
            }), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);

        var headers = resp.Headers
            .Concat(resp.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.FirstOrDefault() ?? "");

        return ParseHeaders(cfg.Id, headers, (int)resp.StatusCode, body);
    }

    internal static AccountSnapshot ParseHeaders(Guid accountId, IReadOnlyDictionary<string, string> headers,
        int statusCode, string body)
    {
        var windows = new Dictionary<string, QuotaWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            const string p = "anthropic-ratelimit-unified-";
            if (!name.StartsWith(p, StringComparison.Ordinal)) continue;
            var rest = name[p.Length..]; // z.B. "5h-utilization", "7d-reset"
            var idx = rest.LastIndexOf('-');
            if (idx <= 0) continue;
            var winKey = rest[..idx];
            var field = rest[(idx + 1)..];
            // Only real windows: 5h/7d/7d_sonnet/… with known fields.
            // Filters meta headers like "overage-status", "7d-surpassed-threshold", "fallback-percentage".
            if (!System.Text.RegularExpressions.Regex.IsMatch(winKey, @"^\d+[hd](_\w+)?$")) continue;
            if (field is not ("utilization" or "reset" or "status")) continue;

            if (!windows.TryGetValue(winKey, out var w))
            {
                w = new QuotaWindow { Key = winKey, Label = LabelFor(winKey), Unit = "Quota" };
                windows[winKey] = w;
            }
            switch (field)
            {
                case "utilization":
                    if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var u))
                        w.UsedFraction = Math.Clamp(u, 0, 1);
                    break;
                case "reset":
                    if (long.TryParse(value, out var epoch)) w.ResetAtUtc = TimeParsing.FromEpochGuess(epoch);
                    else w.ResetAtUtc = TimeParsing.ParseFlexible(value);
                    break;
                case "status":
                    w.Note = StatusLabel(value);
                    break;
            }
        }

        if (windows.Count > 0)
        {
            // Ordering: the binding window (representative-claim) first, then 5h, 7d, rest
            headers.TryGetValue("anthropic-ratelimit-unified-representative-claim", out var claim);
            var order = new List<string>();
            if (claim == "five_hour") order.Add("5h");
            if (claim is "seven_day" or "seven_day_opus") order.Add("7d");
            foreach (var k in new[] { "5h", "7d", "7d_sonnet", "7d_opus" })
                if (!order.Contains(k)) order.Add(k);
            return new AccountSnapshot
            {
                AccountId = accountId,
                Ok = true,
                Windows = windows.Values
                    .OrderBy(w => { var i = order.IndexOf(w.Key); return i < 0 ? 99 : i; })
                    .ToList()
            };
        }

        var classic = TryClassicHeaders(accountId, headers);
        if (classic is not null) return classic;

        var snippet = body.Length > 300 ? body[..300] : body;
        return AccountSnapshot.Failure(accountId,
            $"HTTP {statusCode} – keine Rate-Limit-Header. {snippet}".Trim());
    }

    private static AccountSnapshot? TryClassicHeaders(Guid accountId, IReadOnlyDictionary<string, string> h)
    {
        QuotaWindow? Make(string prefix, string label)
        {
            if (!h.TryGetValue($"{prefix}-limit", out var limS) || !double.TryParse(limS,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lim) || lim <= 0)
                return null;
            double? rem = h.TryGetValue($"{prefix}-remaining", out var remS) &&
                          double.TryParse(remS, System.Globalization.NumberStyles.Any,
                              System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
            DateTimeOffset? reset = h.TryGetValue($"{prefix}-reset", out var resS)
                ? TimeParsing.ParseFlexible(resS)
                : null;
            return new QuotaWindow
            {
                Key = prefix.Replace("anthropic-ratelimit-", ""),
                Label = label,
                Unit = prefix.Contains("tokens") ? "Tokens" : "Requests",
                Total = lim,
                Remaining = rem,
                UsedFraction = rem is null ? null : Math.Clamp(1 - rem.Value / lim, 0, 1),
                ResetAtUtc = reset
            };
        }

        var list = new[]
        {
            Make("anthropic-ratelimit-tokens", "Token-Limit (pro Minute)"),
            Make("anthropic-ratelimit-requests", "Request-Limit (pro Minute)")
        }.Where(w => w is not null).Cast<QuotaWindow>().ToList();

        if (list.Count == 0) return null;
        return new AccountSnapshot { AccountId = accountId, Ok = true, Windows = list };
    }

    private static string LabelFor(string key) => key switch
    {
        "5h" => "5-Std-Fenster",
        "7d" => "7-Tage-Fenster",
        "7d_sonnet" => "Sonnet-Woche",
        "7d_opus" => "Opus-Woche",
        _ => key
    };

    private static string StatusLabel(string raw) => raw switch
    {
        "allowed" => "frei",
        "allowed_warning" => "Warnstufe",
        "rejected" => "abgelehnt",
        "rate_limited" => "limitiert",
        _ => raw
    };
}
