using System.Text;
using System.Text.Json;

namespace InsightTokenWatcher.Core;

/// <summary>Providers that expose NO quota/balance API (Together, Gemini, Perplexity for now):
/// a cheap reachability ping reports OK with an explanatory note — never fake numbers.
/// Ping costs are minimal but not zero (Perplexity answers with a real completion), so the
/// catalog gives these providers a default poll interval of one hour.</summary>
public sealed class InfoOnlyPoller : IAccountPoller
{
    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        using var resp = cfg.Provider switch
        {
            // free endpoint, lists available models – proves the key works
            ProviderKind.Gemini => await GetAsync(http, $"{ProviderInfo.BaseUrl(cfg)}/v1/models",
                keyHeader: true, plainKey, ct),
            // model list – proves the key works
            ProviderKind.Together => await GetAsync(http, $"{ProviderInfo.BaseUrl(cfg)}/models",
                keyHeader: false, plainKey, ct),
            // no free endpoint at all – smallest possible completion (billed, ~nothing)
            ProviderKind.Perplexity => await ChatPingAsync(http, ProviderInfo.BaseUrl(cfg),
                string.IsNullOrWhiteSpace(cfg.Model) ? "sonar" : cfg.Model.Trim(), plainKey, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(cfg.Provider))
        };

        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode is < 200 or >= 300)
            return AccountSnapshot.Failure(cfg.Id, $"HTTP {(int)resp.StatusCode} – {Short(body)}");

        return new AccountSnapshot
        {
            AccountId = cfg.Id,
            Ok = true,
            Windows =
            [
                new QuotaWindow
                {
                    Key = "info",
                    Label = "Info",
                    IsInfoOnly = true,
                    Note = "Limits nicht abfragbar – der Anbieter stellt keine Quota-API bereit."
                }
            ]
        };
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient http, string url, bool keyHeader,
        string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (keyHeader) req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        else req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static Task<HttpResponseMessage> ChatPingAsync(HttpClient http, string baseUrl, string model,
        string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "hi" } }
        }), Encoding.UTF8, "application/json");
        return http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string Short(string body)
    {
        var b = body;
        var brace = b.IndexOf('{');
        if (brace > 0) b = b[..brace];
        b = b.Trim().TrimEnd('.', ':');
        return b.Length > 160 ? b[..160] + "…" : b;
    }
}
