using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TokenWatch.Core;

/// <summary>
/// Generic OpenAI-compatible provider (Groq, Together, Cerebras, …): a minimal
/// POST /chat/completions (max_tokens=1) and parsing of the x-ratelimit-* headers.
/// Reset values may be RFC3339, epoch seconds or duration strings ("6m0s", "1h30m").
/// </summary>
public sealed class GenericOpenAiPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.Model))
            return AccountSnapshot.Failure(cfg.Id, "Modell fehlt – im Konto unter „Modell (Ping)“ eintragen (z.B. llama-3.3-70b-versatile).");

        var url = ProviderInfo.BaseUrl(cfg) + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = cfg.Model.Trim(),
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "hi" } }
            }), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var headers = resp.Headers
            .Concat(resp.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.FirstOrDefault() ?? "");

        return ParseHeaders(cfg.Id, headers, (int)resp.StatusCode,
            resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct));
    }

    internal static AccountSnapshot ParseHeaders(Guid accountId, IReadOnlyDictionary<string, string> headers,
        int statusCode, string body)
    {
        var snap = new AccountSnapshot { AccountId = accountId };
        var now = DateTimeOffset.UtcNow;

        foreach (var kind in new[] { "requests", "tokens" })
        {
            // real format (Groq/OpenRouter/…): x-ratelimit-{limit|remaining|reset}-{requests|tokens}
            if (!headers.TryGetValue($"x-ratelimit-limit-{kind}", out var limS) ||
                !double.TryParse(limS, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lim) || lim <= 0)
                continue;
            double? rem = headers.TryGetValue($"x-ratelimit-remaining-{kind}", out var remS) &&
                          double.TryParse(remS, System.Globalization.NumberStyles.Any,
                              System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
            DateTimeOffset? reset = null;
            if (headers.TryGetValue($"x-ratelimit-reset-{kind}", out var resS))
                reset = ParseReset(resS, now);

            snap.Windows.Add(new QuotaWindow
            {
                Key = kind,
                Label = kind == "requests" ? "Requests-Limit" : "Token-Limit",
                Unit = kind == "requests" ? "Requests" : "Tokens",
                Total = lim,
                Remaining = rem,
                UsedFraction = rem is null ? null : Math.Clamp(1 - rem.Value / lim, 0, 1),
                ResetAtUtc = reset
            });
        }

        if (snap.Windows.Count > 0)
        {
            snap.Ok = true;
            return snap;
        }

        var snippet = body.Length > 300 ? body[..300] : body;
        return AccountSnapshot.Failure(accountId,
            $"HTTP {statusCode} – keine x-ratelimit-*-Header. {snippet}".Trim());
    }

    internal static DateTimeOffset? ParseReset(string raw, DateTimeOffset now)
    {
        raw = raw.Trim();
        if (long.TryParse(raw, out var epoch)) return TimeParsing.FromEpochGuess(epoch);
        var dto = TimeParsing.ParseFlexible(raw);
        if (dto is not null) return dto;

        // duration strings: "6m0s", "1h30m", "90s", "1.5s"
        var m = System.Text.RegularExpressions.Regex.Matches(raw, @"(\d+(?:\.\d+)?)(h|m|s)");
        if (m.Count > 0)
        {
            double seconds = 0;
            foreach (System.Text.RegularExpressions.Match match in m)
            {
                var v = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                seconds += match.Groups[2].Value switch { "h" => v * 3600, "m" => v * 60, _ => v };
            }
            if (seconds > 0) return now.AddSeconds(seconds);
        }
        return null;
    }
}
