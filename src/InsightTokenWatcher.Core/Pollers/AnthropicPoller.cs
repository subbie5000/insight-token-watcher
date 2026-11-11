using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InsightTokenWatcher.Core;

/// <summary>
/// Anthropic: a mini call to /v1/messages (max_tokens=1, Haiku ≈ 1–2 ct/month at 1/min) –
/// the unified rate-limit headers ride on EVERY response, including 429s.
/// anthropic-ratelimit-unified-{5h,7d,7d_sonnet}-{status,reset(epoch s),utilization(0..1)}
///
/// Model-scoped weekly limits (e.g. the Fable week) are NOT part of a Haiku response. They
/// come from the subscription's usage endpoint (GET /api/oauth/usage, spends no tokens). Setup
/// tokens lack the right to read it (403, scope user:profile); for those a ping with the
/// scoped model returns the limit as header "7d_oi".
/// </summary>
public sealed class AnthropicPoller : IAccountPoller
{
    internal const string ClaudeCodeSystem = "You are Claude Code, Anthropic's official CLI for Claude.";

    /// <summary>Model of the fallback ping that makes Anthropic return the model-scoped header.</summary>
    public const string ScopedPingModel = "claude-fable-5-2";

    // A weekly window moves slowly: the free endpoint is asked every 5 min, the fallback ping
    // (it costs a few tokens of the very limit it measures) every 15 min. In between the last
    // values are reported again; after 45 min without a fresh value they are dropped.
    // Two things cut the wait short (never below ScopedMinGap): a general window that was
    // reset, and the user asking for fresh values.
    private static readonly TimeSpan UsageInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScopedPingInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ScopedMaxAge = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ScopedMinGap = TimeSpan.FromSeconds(30);

    private sealed class ScopedState
    {
        public int KeyHash;
        public bool UsageForbidden;
        public DateTimeOffset NextTryUtc;
        public DateTimeOffset LastTryUtc;
        public DateTimeOffset MeasuredUtc;
        public List<QuotaWindow> Windows = new();

        /// <summary>The general windows of the previous poll: usage and reset time by key.</summary>
        public Dictionary<string, (double Used, DateTimeOffset? Reset)> General = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Read the model limits at the next poll – but not within
        /// <see cref="ScopedMinGap"/> of the last read (several clicks, a flapping value).</summary>
        public void Hurry()
        {
            var soonest = LastTryUtc + ScopedMinGap;
            if (soonest < NextTryUtc) NextTryUtc = soonest;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ScopedState> ScopedStates = new();

    /// <summary>Test hook: the clock the model-limit schedule runs on.</summary>
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    /// <summary>Test hook: forget the cached model-scoped limits.</summary>
    internal static void ResetScopedCache() => ScopedStates.Clear();

    /// <summary>The user asked for fresh values ("measure now"): the model limits are read again
    /// with the next poll instead of being reused until their interval is over.</summary>
    public void Invalidate(Guid accountId)
    {
        if (!ScopedStates.TryGetValue(accountId, out var st)) return;
        lock (st) st.Hurry();
    }

    public async Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct)
    {
        var (status, headers, body) = await PingAsync(cfg, plainKey, cfg.EffectiveModel, http, ct);
        var snap = ParseHeaders(cfg.Id, headers, status, body);
        // only subscriptions have model-scoped limits; the header values win over the endpoint's
        if (snap.Ok && cfg.Auth == AuthKind.Bearer)
            foreach (var w in await ScopedWindowsAsync(cfg, plainKey, snap.Windows, http, ct))
                if (snap.Windows.All(x => !x.Key.Equals(w.Key, StringComparison.OrdinalIgnoreCase)))
                    snap.Windows.Add(w);
        return snap;
    }

    /// <summary>
    /// A general window fell back or got a later reset time: it was reset – by time or by hand in
    /// claude.ai. The model limits reset with it, so their stored values are from before the
    /// reset. Within a window the usage only grows; one point down is rounding, not a reset.
    /// </summary>
    private static bool WasReset(IReadOnlyDictionary<string, (double Used, DateTimeOffset? Reset)> before,
        IEnumerable<QuotaWindow> general) =>
        general.Any(w => before.TryGetValue(w.Key, out var b) &&
                         ((w.UsedFraction is { } used && used < b.Used - 0.015) ||
                          (w.ResetAtUtc is { } reset && b.Reset is { } old && reset - old > TimeSpan.FromMinutes(1))));

    private static async Task<IReadOnlyList<QuotaWindow>> ScopedWindowsAsync(AccountConfig cfg, string plainKey,
        IReadOnlyList<QuotaWindow> measured, HttpClient http, CancellationToken ct)
    {
        var st = ScopedStates.GetOrAdd(cfg.Id, _ => new ScopedState());
        var now = Clock();
        bool due, forbidden;
        lock (st)
        {
            var hash = string.GetHashCode(plainKey, StringComparison.Ordinal);
            if (hash != st.KeyHash)
            {
                // another token may have other rights (setup token ↔ login token): ask again now
                st.KeyHash = hash;
                st.UsageForbidden = false;
                st.NextTryUtc = default;
            }
            // Without this the popup showed the new week next to the model limit from before
            // the reset ("FABLE LEER 100 %") until the interval was over.
            var general = measured.Where(w => w.Scope is null).ToList();
            if (WasReset(st.General, general)) st.Hurry();
            st.General = general.Where(w => w.UsedFraction is not null)
                .ToDictionary(w => w.Key, w => (w.UsedFraction!.Value, w.ResetAtUtc), StringComparer.OrdinalIgnoreCase);

            due = now >= st.NextTryUtc;
            if (due)
            {
                st.NextTryUtc = now + UsageInterval;
                st.LastTryUtc = now;
            }
            forbidden = st.UsageForbidden;
        }

        if (due)
        {
            List<QuotaWindow>? fresh = null;
            try
            {
                if (!forbidden)
                {
                    var (code, json) = await GetUsageAsync(cfg, plainKey, http, ct);
                    if (code == 200) fresh = ParseUsageLimits(json);
                    else if (code is 401 or 403) forbidden = true;
                }
                if (forbidden)
                {
                    var (status, headers, body) = await PingAsync(cfg, plainKey, ScopedPingModel, http, ct);
                    var parsed = ParseHeaders(cfg.Id, headers, status, body);
                    if (parsed.Ok) fresh = parsed.Windows.Where(w => w.Scope is not null).ToList();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // the main windows are measured – a failed extra request keeps the last values
            }
            lock (st)
            {
                st.UsageForbidden = forbidden;
                if (forbidden) st.NextTryUtc = now + ScopedPingInterval;
                if (fresh is not null)
                {
                    foreach (var w in fresh) w.MeasuredAtUtc = now;
                    st.Windows = fresh;
                    st.MeasuredUtc = now;
                }
            }
        }

        lock (st)
            return now - st.MeasuredUtc <= ScopedMaxAge ? st.Windows.ToList() : new List<QuotaWindow>();
    }

    /// <summary>GET /api/oauth/usage – the subscription's own usage view. Needs a login token
    /// (scope user:profile); setup tokens get 403.</summary>
    public static async Task<(int status, string body)> GetUsageAsync(AccountConfig cfg, string plainKey,
        HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderInfo.BaseUrl(cfg) + "/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("user-agent", "claude-cli/2.0 (external, tokenwatch)");
        using var resp = await http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Model-scoped limits from the usage endpoint: <c>limits[]</c> entries that carry
    /// <c>scope.model.display_name</c>, e.g. {"kind":"weekly_scoped","group":"weekly","percent":14,
    /// "resets_at":"…","scope":{"model":{"display_name":"Fable"}}}. The general windows
    /// (session, weekly_all) are ignored here – they come from the headers.
    /// </summary>
    internal static List<QuotaWindow> ParseUsageLimits(string json)
    {
        var list = new List<QuotaWindow>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var l in limits.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Object ||
                !l.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object ||
                !scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object)
                continue;
            var name = ProviderInfo.JsonString(model, "display_name", "id")?.Trim();
            var percent = ProviderInfo.JsonDouble(l, "percent");
            if (string.IsNullOrEmpty(name) || percent is null) continue;

            double? minutes = ProviderInfo.JsonString(l, "group") switch
            {
                "weekly" => 10080,
                "session" => 300,
                _ => null
            };
            var key = (minutes == 300 ? "5h_" : "7d_") + Slug(name);
            if (list.Any(w => w.Key == key)) continue;
            list.Add(new QuotaWindow
            {
                Key = key,
                Label = ScopedLabel(name, minutes),
                Scope = name,
                Unit = "Quota",
                WindowMinutes = minutes,
                UsedFraction = Math.Clamp(percent.Value / 100.0, 0, 1),
                ResetAtUtc = RoundToSecond(TimeParsing.ParseFlexible(ProviderInfo.JsonString(l, "resets_at")))
            });
        }
        return list;
    }

    // the endpoint jitters around the real reset by fractions of a second (03:59:59.78 /
    // 04:00:00.48 for the same reset) – unrounded, the clock time showed "11:59"
    private static DateTimeOffset? RoundToSecond(DateTimeOffset? t) => t is { } v
        ? new DateTimeOffset((v.UtcTicks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero)
        : null;

    private static string Slug(string name) =>
        new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    private static string ScopedLabel(string scope, double? minutes) =>
        minutes == 300 ? $"{scope} (5 h)" : $"{scope}-Woche";

    /// <summary>
    /// Header window key → canonical key and model scope. "7d_oi" is the Fable week (observed
    /// 2026-11-10 on two accounts: same utilization and reset as the usage endpoint's "Fable"
    /// entry). Every other suffix names its scope itself ("7d_sonnet" → Sonnet).
    /// </summary>
    internal static (string key, string? scope) Canonical(string headerKey)
    {
        var i = headerKey.IndexOf('_');
        if (i <= 0 || i == headerKey.Length - 1) return (headerKey, null);
        var suffix = headerKey[(i + 1)..];
        if (suffix.Equals("oi", StringComparison.OrdinalIgnoreCase)) return (headerKey[..i] + "_fable", "Fable");
        return (headerKey, char.ToUpperInvariant(suffix[0]) + suffix[1..]);
    }

    /// <summary>The 1-token ping with a given model. Returns the status, the response headers
    /// (lower-case names) and the body – also used by the CLI's <c>--headers</c> diagnosis.</summary>
    public static async Task<(int status, Dictionary<string, string> headers, string body)> PingAsync(
        AccountConfig cfg, string plainKey, string model, HttpClient http, CancellationToken ct)
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
        var messages = new[] { new { role = "user", content = "hi" } };
        // Subscription (OAuth) tokens are only accepted for non-Haiku models when the request
        // identifies as Claude Code; without this line Anthropic answers 429 "Error" WITHOUT
        // any rate-limit header – it looks like a limit but is a rejected request shape.
        object payload = cfg.Auth == AuthKind.Bearer
            ? new { model, max_tokens = 1, system = ClaudeCodeSystem, messages }
            : new { model, max_tokens = 1, messages };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, ProviderInfo.HeaderMap(resp), body);
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

            var (canonical, scope) = Canonical(winKey);
            if (!windows.TryGetValue(canonical, out var w))
            {
                var minutes = MinutesFor(winKey);
                w = new QuotaWindow
                {
                    Key = canonical,
                    Label = scope is null ? LabelFor(winKey) : ScopedLabel(scope, minutes),
                    Unit = "Quota",
                    WindowMinutes = minutes,
                    Scope = scope
                };
                windows[canonical] = w;
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
            foreach (var k in new[] { "5h", "7d", "7d_fable", "7d_opus", "7d_sonnet" })
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
                ResetAtUtc = reset,
                WindowMinutes = 1
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
        _ => key
    };

    /// <summary>"5h" → 300, "7d"/"7d_sonnet" → 10080.</summary>
    internal static double? MinutesFor(string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(key, @"^(\d+)([hd])");
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value == "h" ? n * 60 : n * 1440;
    }

    private static string StatusLabel(string raw) => raw switch
    {
        "allowed" => "frei",
        "allowed_warning" => "Warnstufe",
        "rejected" => "abgelehnt",
        "rate_limited" => "limitiert",
        _ => raw
    };
}
