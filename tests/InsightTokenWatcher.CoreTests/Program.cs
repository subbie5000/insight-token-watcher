using InsightTokenWatcher.Core;

int passed = 0, failed = 0;
var failures = new List<string>();

void Check(bool cond, string what)
{
    if (!cond) throw new Exception("check failed: " + what);
}

void Run(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS  " + name); }
    catch (Exception ex) { failed++; failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL  " + name + ": " + ex.Message); }
}

// ---------- time parsing ----------

Run("time: RFC3339 with nanoseconds (Kimi)", () =>
{
    var t = TimeParsing.ParseFlexible("2026-01-09T15:23:13.716839300Z");
    Check(t is not null, "null");
    Check(t!.Value.Year == 2026 && t.Value.Month == 1 && t.Value.Day == 9, "wrong date");
    Check(Math.Abs(t.Value.ToUnixTimeSeconds() - 1767972193) < 3, "epoch off");
});

Run("time: epoch seconds as string", () =>
{
    var t = TimeParsing.ParseFlexible("1774933200");
    Check(t is not null, "null");
    Check(t!.Value.ToUnixTimeSeconds() == 1774933200, "wrong value");
});

Run("time: epoch ms as number", () =>
{
    var t = TimeParsing.FromEpochGuess(1774933200000L);
    Check(t.ToUnixTimeSeconds() == 1774933200, "ms conversion wrong");
});

// ---------- Anthropic ----------

Run("Anthropic: unified 5h+7d headers", () =>
{
    var h = new Dictionary<string, string>
    {
        ["anthropic-ratelimit-unified-status"] = "allowed",
        ["anthropic-ratelimit-unified-representative-claim"] = "five_hour",
        ["anthropic-ratelimit-unified-5h-status"] = "allowed",
        ["anthropic-ratelimit-unified-5h-reset"] = "1774933200",
        ["anthropic-ratelimit-unified-5h-utilization"] = "0.37",
        ["anthropic-ratelimit-unified-7d-status"] = "allowed",
        ["anthropic-ratelimit-unified-7d-reset"] = "1775534400",
        ["anthropic-ratelimit-unified-7d-utilization"] = "0.61",
    };
    var s = AnthropicPoller.ParseHeaders(Guid.NewGuid(), h, 200, "{}");
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "window count " + s.Windows.Count);
    Check(s.Windows[0].Key == "5h", "5h not first (representative-claim)");
    Check(Math.Abs(s.Windows[0].UsedFraction!.Value - 0.37) < 1e-9, "5h utilization");
    Check(s.Windows[0].ResetAtUtc!.Value.ToUnixTimeSeconds() == 1774933200, "5h reset");
    Check(Math.Abs(s.Windows[1].UsedFraction!.Value - 0.61) < 1e-9, "7d utilization");
});

Run("Anthropic: 429 with headers still parses", () =>
{
    var h = new Dictionary<string, string>
    {
        ["anthropic-ratelimit-unified-5h-status"] = "rate_limited",
        ["anthropic-ratelimit-unified-5h-reset"] = "1774933200",
        ["anthropic-ratelimit-unified-5h-utilization"] = "1",
    };
    var s = AnthropicPoller.ParseHeaders(Guid.NewGuid(), h, 429, "{}");
    Check(s.Ok, "not ok");
    Check(s.Windows[0].UsedFraction >= 0.999, "utilization 1");
    Check(s.Windows[0].Note == "limitiert", "status note");
});

Run("Anthropic: classic headers as fallback", () =>
{
    var h = new Dictionary<string, string>
    {
        ["anthropic-ratelimit-tokens-limit"] = "200000",
        ["anthropic-ratelimit-tokens-remaining"] = "150000",
        ["anthropic-ratelimit-tokens-reset"] = "2026-10-14T12:34:56Z",
        ["anthropic-ratelimit-requests-limit"] = "4000",
        ["anthropic-ratelimit-requests-remaining"] = "4000",
    };
    var s = AnthropicPoller.ParseHeaders(Guid.NewGuid(), h, 200, "{}");
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "Fensterzahl");
    Check(Math.Abs(s.Windows[0].UsedFraction!.Value - 0.25) < 1e-9, "25 % verbraucht");
});

Run("Anthropic: no headers = error with body snippet", () =>
{
    var s = AnthropicPoller.ParseHeaders(Guid.NewGuid(), new Dictionary<string, string>(), 401,
        "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"}}");
    Check(!s.Ok, "should fail");
    Check(s.Error!.Contains("401"), "status code in error");
});

// ---------- Kimi Code ----------

Run("Anthropic: real header mix filters meta windows out", () =>
{
    // observed verbatim from api.anthropic.com (2026-10-14)
    var h = new Dictionary<string, string>
    {
        ["anthropic-ratelimit-unified-5h-status"] = "allowed",
        ["anthropic-ratelimit-unified-representative-claim"] = "seven_day",
        ["anthropic-ratelimit-unified-overage-status"] = "rejected",
        ["anthropic-ratelimit-unified-reset"] = "1792382400",
        ["anthropic-ratelimit-unified-5h-utilization"] = "0.46",
        ["anthropic-ratelimit-unified-7d-reset"] = "1792382400",
        ["anthropic-ratelimit-unified-5h-reset"] = "1791966000",
        ["anthropic-ratelimit-unified-7d-status"] = "allowed_warning",
        ["anthropic-ratelimit-unified-7d-surpassed-threshold"] = "0.75",
        ["anthropic-ratelimit-unified-fallback-percentage"] = "0.5",
        ["anthropic-ratelimit-unified-overage-disabled-reason"] = "org_level_disabled",
        ["anthropic-ratelimit-unified-7d-utilization"] = "0.81",
        ["anthropic-ratelimit-unified-status"] = "allowed_warning",
    };
    var s = AnthropicPoller.ParseHeaders(Guid.NewGuid(), h, 200, "{}");
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "expected exactly 2 windows, got " + s.Windows.Count +
        ": " + string.Join(",", s.Windows.Select(w => w.Key)));
    Check(s.Windows.Any(w => w.Key == "5h") && s.Windows.Any(w => w.Key == "7d"), "5h+7d expected");
    Check(s.Windows.First(w => w.Key == "7d").Note == "Warnstufe", "allowed_warning→Warnstufe label");
});
Run("Kimi Code: weekly and 5h windows", () =>
{
    var json = """
    {
      "usage": { "limit": "2048", "used": "214", "remaining": "1834",
                 "resetTime": "2026-01-09T15:23:13.716839300Z" },
      "limits": [ {
        "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
        "detail": { "limit": "200", "used": "139", "remaining": "61",
                    "resetTime": "2026-01-06T13:33:02.717479433Z" }
      } ]
    }
    """;
    var s = KimiCodePoller.ParseBody(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "window count " + s.Windows.Count);
    var week = s.Windows.First(w => w.Key == "week");
    Check(week.Remaining == 1834, "remaining");
    Check(Math.Abs(week.UsedFraction!.Value - 214.0 / 2048) < 1e-9, "frac");
    Check(week.ResetAtUtc is not null, "reset");
    var five = s.Windows.First(w => w.Key == "300m");
    Check(five.Label.Contains("5-Std"), "Label: " + five.Label);
    Check(Math.Abs(five.UsedFraction!.Value - 139.0 / 200) < 1e-9, "5h frac");
});

// ---------- Moonshot ----------

Run("Kimi Code: proto3 omits used=0 → 0 %, not unknown", () =>
{
    // observed live: the 5 h window right after a reset has no "used" field
    var json = """
    { "usage": { "limit": "100", "used": "61", "remaining": "39", "resetTime": "2026-10-20T10:22:09Z" },
      "limits": [ { "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
                    "detail": { "limit": "100", "remaining": "100", "resetTime": "2026-10-14T18:22:09Z" } } ] }
    """;
    var s = KimiCodePoller.ParseBody(Guid.NewGuid(), json);
    var five = s.Windows.First(w => w.Key == "300m");
    Check(five.UsedFraction == 0, "expected 0, got " + five.UsedFraction);
});

Run("Moonshot: balance (international = USD)", () =>
{
    var json = """{"code":0,"data":{"available_balance":45.20,"voucher_balance":10.00,"cash_balance":35.20}}""";
    var s = MoonshotPoller.ParseBody(Guid.NewGuid(), json, "https://api.moonshot.ai");
    Check(s.Ok, "not ok");
    var w = s.Windows[0];
    Check(w.IsBalance, "Balance");
    Check(Math.Abs(w.Remaining!.Value - 45.20) < 1e-9, "Betrag");
    Check(w.Unit == "USD", "currency " + w.Unit);
});

Run("Moonshot: China = CNY", () =>
{
    var json = """{"available_balance":88.5,"voucher_balance":0,"cash_balance":88.5}""";
    var s = MoonshotPoller.ParseBody(Guid.NewGuid(), json, "https://api.moonshot.cn");
    Check(s.Ok && s.Windows[0].Unit == "CNY", "CNY expected");
});

// ---------- GLM / z.ai ----------

Run("z.ai: limits with percentage + nextResetTime (ms)", () =>
{
    var json = """
    {
      "code": 0,
      "data": {
        "planName": "GLM Coding Plan Pro",
        "limits": [
          { "type": "TOKENS_LIMIT", "percentage": 42, "nextResetTime": 1774933200000,
            "window": { "unit": "hours", "number": 5 } },
          { "type": "TIME_LIMIT", "percentage": 3, "nextResetTime": 1775534400000,
            "window": { "unit": "days", "number": 30 } }
        ]
      }
    }
    """;
    var s = ZaiPoller.ParseBody(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "Fensterzahl");
    var t = s.Windows[0];
    Check(Math.Abs(t.UsedFraction!.Value - 0.42) < 1e-9, "pct");
    Check(t.ResetAtUtc!.Value.ToUnixTimeSeconds() == 1774933200, "reset ms");
    Check(t.Note!.Contains("GLM Coding Plan Pro"), "planName");
});

Run("z.ai: usage/currentValue beats percentage", () =>
{
    var json = """
    { "data": { "limits": [
      { "type": "TOKENS_LIMIT", "percentage": 10, "usage": 1000000, "currentValue": 250000,
        "nextResetTime": "1774933200000" } ] } }
    """;
    var s = ZaiPoller.ParseBody(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    Check(Math.Abs(s.Windows[0].UsedFraction!.Value - 0.25) < 1e-9, "25% from counts");
});

Run("z.ai: HTTP 200 with code=401 detected as error", () =>
{
    var json = """{"code":401,"msg":"token expired or incorrect","success":false}""";
    var s = ZaiPoller.ParseBody(Guid.NewGuid(), json);
    Check(!s.Ok, "must not be ok");
    Check(s.Error!.Contains("token expired"), "error text: " + s.Error);
});

// ---------- DeepSeek ----------

Run("z.ai: real response with unit/number on the limit entry", () =>
{
    // observed verbatim from api.z.ai (2026-10-14, plan "max")
    var json = """
    {"code":200,"msg":"Operation successful","data":{"limits":[
      {"type":"CREDIT_LIMIT","unit":3,"number":5,"usage":28000,"currentValue":10177,"remaining":17822,"percentage":36,"nextResetTime":1791978690073},
      {"type":"CREDIT_LIMIT","unit":6,"number":1,"usage":140000,"currentValue":72618,"remaining":67381,"percentage":51,"nextResetTime":1792311215983}
    ],"level":"max"},"success":true}
    """;
    var s = ZaiPoller.ParseBody(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "Fensterzahl");
    Check(s.Windows[0].Label == "5-Std-Kontingent", "Label 0: " + s.Windows[0].Label);
    Check(s.Windows[1].Label == "Wochen-Kontingent", "Label 1: " + s.Windows[1].Label);
    Check(s.Windows[0].Unit == "Credits", "Credits unit");
    Check(Math.Abs(s.Windows[0].UsedFraction!.Value - 10177.0 / 28000) < 0.001, "frac from counts");
    Check(s.Windows[0].Remaining == 17822, "remaining");
    Check(s.Windows[0].Note!.Contains("max"), "level→plan note");
});
Run("DeepSeek: USD preferred, breakdown in note", () =>
{
    var json = """
    { "is_available": true, "balance_infos": [
      { "currency": "CNY", "total_balance": "12.00", "granted_balance": "0.00", "topped_up_balance": "12.00" },
      { "currency": "USD", "total_balance": "37.61", "granted_balance": "7.61", "topped_up_balance": "30.00" }
    ] }
    """;
    var s = DeepSeekPoller.ParseBody(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    var w = s.Windows[0];
    Check(w.Unit == "USD", "expected USD, got " + w.Unit);
    Check(Math.Abs(w.Remaining!.Value - 37.61) < 1e-9, "Betrag");
    Check(w.Note!.Contains("30,00 $"), "paid breakdown in German format: " + w.Note);
});

// ---------- Burn-Engine ----------

Run("BurnMath: slope from 5 points", () =>
{
    var t0 = DateTimeOffset.UtcNow.AddMinutes(-40);
    var pts = Enumerable.Range(0, 5)
        .Select(i => new Sample(t0.AddMinutes(i * 10), 0.10 + i * 0.05, null))
        .ToList();
    var slope = BurnMath.SlopePerMinute(pts, false, DateTimeOffset.UtcNow);
    Check(slope is not null, "null");
    Check(Math.Abs(slope!.Value - 0.005) < 0.001, "slope " + slope);
});

Run("WindowSeries: reset detects utilization drop", () =>
{
    var s = new WindowSeries();
    var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
    s.Add(new Sample(t0, 0.5, null), false);
    s.Add(new Sample(t0.AddMinutes(5), 0.6, null), false);
    s.Add(new Sample(t0.AddMinutes(10), 0.05, null), false); // reset!
    Check(s.Samples.Count == 1, "series not reset: " + s.Samples.Count);
});

Run("WindowSeries: balance top-up resets series", () =>
{
    var s = new WindowSeries();
    var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
    s.Add(new Sample(t0, 0, 100), true);
    s.Add(new Sample(t0.AddMinutes(5), 0, 95), true);
    s.Add(new Sample(t0.AddMinutes(10), 0, 150), true); // top-up
    Check(s.Samples.Count == 1, "series not reset");
});

Run("Evaluator: lasts until reset = OK", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "5h", IsBalance = false, UsedFraction = 0.3, ResetAtUtc = now.AddHours(2) };
    var pts = new List<Sample>
    {
        new(now.AddMinutes(-30), 0.20, null),
        new(now.AddMinutes(-15), 0.25, null),
        new(now, 0.30, null)
    };
    var ev = Evaluator.Evaluate(w, pts, now);
    Check(ev.Health == Health.Ok, "expected Ok, got " + ev.Health + " / " + ev.Verdict);
    Check(ev.TimeToEmpty > ev.TimeToReset, "tte>ttr");
});

Run("Evaluator: empty before reset = CRIT", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "5h", IsBalance = false, UsedFraction = 0.9, ResetAtUtc = now.AddHours(3) };
    var pts = new List<Sample>
    {
        new(now.AddMinutes(-20), 0.7, null),
        new(now.AddMinutes(-10), 0.8, null),
        new(now, 0.9, null)
    };
    var ev = Evaluator.Evaluate(w, pts, now);
    Check(ev.Health == Health.Crit, "expected Crit, got " + ev.Health + " / " + ev.Verdict);
});

Run("Evaluator: no consumption = OK with note", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "5h", IsBalance = false, UsedFraction = 0.3, ResetAtUtc = now.AddHours(2) };
    var ev = Evaluator.Evaluate(w, new List<Sample> { new(now, 0.3, null) }, now);
    Check(ev.Health == Health.Ok, "expected Ok");
    Check(ev.Verdict.Contains("Kein Verbrauch"), "verdict: " + ev.Verdict);
});

Run("Evaluator: limit reached = CRIT", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "5h", IsBalance = false, UsedFraction = 1.0, ResetAtUtc = now.AddHours(1) };
    var ev = Evaluator.Evaluate(w, new List<Sample> { new(now, 1.0, null) }, now);
    Check(ev.Health == Health.Crit, "expected Crit");
    Check(ev.Verdict.Contains("Limit erreicht"), "verdict: " + ev.Verdict);
});

Run("Evaluator: balance graded by days", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "balance", IsBalance = true, Remaining = 100, Unit = "USD" };
    var pts = new List<Sample>
    {
        new(now.AddMinutes(-60), 0, 100.694),
        new(now, 0, 100)
    };
    var ev = Evaluator.Evaluate(w, pts, now);
    Check(ev.HasRate, "expected rate");
    Check(ev.Health == Health.Ok, "expected Ok, got " + ev.Health + " / " + ev.Verdict);
});

Run("Evaluator: low balance = CRIT", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "balance", IsBalance = true, Remaining = 1, Unit = "USD" };
    var pts = new List<Sample>
    {
        new(now.AddMinutes(-60), 0, 1.25),
        new(now, 0, 1)
    };
    var ev = Evaluator.Evaluate(w, pts, now);
    Check(ev.Health == Health.Crit, "expected Crit, got " + ev.Health + " / " + ev.Verdict);
});

// ---------- OpenAI ----------

Run("OpenAI: Admin-Costs summieren (30d)", () =>
{
    var json = """
    { "object": "page", "data": [
      { "start_time": 1789000000, "end_time": 1789086400, "results": [
        { "amount": { "value": 1.25, "currency": "usd" }, "line_item": null },
        { "amount": { "value": 0.75, "currency": "usd" }, "line_item": "gpt-5" } ] },
      { "start_time": 1789086400, "end_time": 1789172800, "results": [
        { "amount": { "value": 2.00, "currency": "usd" }, "line_item": "gpt-5" } ] }
    ], "has_more": false }
    """;
    var s = OpenAiPoller.ParseCosts(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    var w = s.Windows[0];
    Check(w.IsInfoOnly, "info only");
    Check(Math.Abs(w.Remaining!.Value - 4.00) < 1e-9, "Summe 4.00, kam " + w.Remaining);
});

Run("OpenAI: credit_grants Fallback", () =>
{
    var json = """{"object":"credit_grants","total_granted":120.0,"total_used":97.5,"total_available":22.5}""";
    var s = OpenAiPoller.ParseCreditGrants(Guid.NewGuid(), json);
    Check(s.Ok, "not ok");
    Check(s.Windows[0].IsBalance, "balance");
    Check(Math.Abs(s.Windows[0].Remaining!.Value - 22.5) < 1e-9, "22.5");
});

// ---------- OpenRouter ----------

Run("OpenRouter: Key-Limit + Credits", () =>
{
    var keyJson = """
    { "data": { "label": "main", "usage": 12.34, "usage_daily": 0.87, "usage_weekly": 4.20,
                "usage_monthly": 12.34, "limit": 30.0, "limit_remaining": 17.66,
                "is_free_tier": false, "rate_limit": { "requests": 200, "interval": "10s" } } }
    """;
    var creditsJson = """{ "data": { "total_credits": 25.0, "total_usage": 12.34 } }""";
    var doc = System.Text.Json.JsonDocument.Parse(keyJson);
    var d = doc.RootElement.GetProperty("data");
    var limit = ProviderInfo.JsonDouble(d, "limit");
    var remaining = ProviderInfo.JsonDouble(d, "limit_remaining");
    Check(limit == 30.0 && remaining == 17.66, "key fields");
    var cdoc = System.Text.Json.JsonDocument.Parse(creditsJson);
    var cd = cdoc.RootElement.GetProperty("data");
    var credits = ProviderInfo.JsonDouble(cd, "total_credits");
    var usage = ProviderInfo.JsonDouble(cd, "total_usage");
    Check(Math.Abs(credits!.Value - usage!.Value - 12.66) < 1e-9, "balance 12.66");
});

// ---------- Generic OpenAI-compatible ----------

Run("GenericOpenAi: x-ratelimit header incl. duration reset", () =>
{
    var now = DateTimeOffset.UtcNow;
    var h = new Dictionary<string, string>
    {
        ["x-ratelimit-limit-requests"] = "500",
        ["x-ratelimit-remaining-requests"] = "499",
        ["x-ratelimit-reset-requests"] = "2.5s",
        ["x-ratelimit-limit-tokens"] = "100000",
        ["x-ratelimit-remaining-tokens"] = "40000",
        ["x-ratelimit-reset-tokens"] = "6m0s",
    };
    var s = GenericOpenAiPoller.ParseHeaders(Guid.NewGuid(), h, 200, "{}");
    Check(s.Ok, "not ok");
    Check(s.Windows.Count == 2, "2 windows");
    var t = s.Windows.First(w => w.Key == "tokens");
    Check(Math.Abs(t.UsedFraction!.Value - 0.6) < 1e-9, "60 % used");
    var dt = t.ResetAtUtc!.Value - now;
    Check(dt.TotalMinutes is > 5.9 and < 6.1, "reset ≈ 6 min, got " + dt.TotalMinutes);
});

Run("GenericOpenAi: without headers = readable error", () =>
{
    var s = GenericOpenAiPoller.ParseHeaders(Guid.NewGuid(), new Dictionary<string, string>(), 401, "{}");
    Check(!s.Ok && s.Error!.Contains("401"), "error with status code");
});

// ---------- layout ----------

QuotaWindow Qw(string key, double? minutes, bool balance = false, bool info = false) =>
    new() { Key = key, Label = key, WindowMinutes = minutes, IsBalance = balance, IsInfoOnly = info, UsedFraction = 0.1 };

Run("Layout: Anthropic 7d first (representative) still lands right, 5h left", () =>
{
    var r = WindowLayout.Classify(new[] { Qw("7d", 10080), Qw("5h", 300), Qw("7d_sonnet", 10080) });
    Check(r.Short?.Key == "5h", "short=5h, got " + r.Short?.Key);
    Check(r.Long?.Key == "7d", "long=7d, got " + r.Long?.Key);
    Check(r.Extras.Count == 1 && r.Extras[0].Key == "7d_sonnet", "sonnet as extra");
});

Run("Layout: Kimi week+5h sorted into columns", () =>
{
    var r = WindowLayout.Classify(new[] { Qw("week", 10080), Qw("300m", 300) });
    Check(r.Short?.Key == "300m" && r.Long?.Key == "week", "columns");
});

Run("Layout: balance spans, everything else becomes extra", () =>
{
    var r = WindowLayout.Classify(new[] { Qw("key", null, info: true), Qw("balance", null, balance: true) });
    Check(r.Span?.Key == "balance", "balance spans (preferred over info)");
    Check(r.Extras.Count == 1 && r.Extras[0].Key == "key", "key info as extra");
    Check(r.Short is null && r.Long is null, "no cells");
});

Run("Layout: unknown durations fill short then long", () =>
{
    var r = WindowLayout.Classify(new[] { Qw("requests", null), Qw("tokens", null), Qw("x", null) });
    Check(r.Short?.Key == "requests" && r.Long?.Key == "tokens" && r.Extras.Count == 1, "fill order");
});

Run("Layout: short labels", () =>
{
    Check(WindowLayout.ShortLabel(Qw("a", 300)) == "5 h", "5 h");
    Check(WindowLayout.ShortLabel(Qw("b", 10080)) == "Woche", "Woche");
    Check(WindowLayout.ShortLabel(Qw("c", 1440)) == "Tag", "Tag");
    Check(WindowLayout.ShortLabel(Qw("d", null)) == "d", "fallback label");
    Check(WindowLayout.ShortLabel(Qw("e", null, balance: true)) == "Guthaben", "balance");
});

Run("Anthropic: window minutes from header keys", () =>
{
    Check(AnthropicPoller.MinutesFor("5h") == 300, "5h");
    Check(AnthropicPoller.MinutesFor("7d") == 10080, "7d");
    Check(AnthropicPoller.MinutesFor("7d_sonnet") == 10080, "7d_sonnet");
    Check(AnthropicPoller.MinutesFor("weird") is null, "unknown");
});

// ---------- engine ----------

AccountSnapshot OkSnap(Guid id, double used) => new()
{
    AccountId = id, Ok = true,
    Windows = { new QuotaWindow { Key = "5h", Label = "5h", WindowMinutes = 300, UsedFraction = used,
        ResetAtUtc = DateTimeOffset.UtcNow.AddHours(2) } }
};

Run("Engine: failure after success keeps last good values (stale)", () =>
{
    using var eng = new PollingEngine();
    var cfg = new AccountConfig { Name = "a", Provider = ProviderKind.Anthropic };
    eng.Reconfigure(new[] { (cfg, "k") });
    eng.IngestFor(cfg.Id, OkSnap(cfg.Id, 0.4));
    eng.IngestFor(cfg.Id, AccountSnapshot.Failure(cfg.Id, "HTTP 401 – … OAuth access token has expired."));
    var v = eng.GetView()[0];
    Check(v.IsStale, "stale");
    Check(v.Display?.Windows[0].UsedFraction == 0.4, "shows last good value");
    Check(v.Failure == FailureKind.AuthExpired, "auth expired, got " + v.Failure);
    Check(v.Health == Health.Unknown, "expired token is not an alarm, got " + v.Health);
});

Run("Engine: network errors become critical only after 3 in a row", () =>
{
    using var eng = new PollingEngine();
    var cfg = new AccountConfig { Name = "a", Provider = ProviderKind.DeepSeek };
    eng.Reconfigure(new[] { (cfg, "k") });
    eng.IngestFor(cfg.Id, OkSnap(cfg.Id, 0.1));
    eng.IngestFor(cfg.Id, AccountSnapshot.Failure(cfg.Id, "HttpRequestException: no route"));
    eng.IngestFor(cfg.Id, AccountSnapshot.Failure(cfg.Id, "TaskCanceledException: timeout"));
    Check(eng.GetView()[0].Health != Health.Crit, "2 failures: not crit yet");
    eng.IngestFor(cfg.Id, AccountSnapshot.Failure(cfg.Id, "HttpRequestException: no route"));
    Check(eng.GetView()[0].Health == Health.Crit, "3 failures: crit");
});

Run("Engine: success after failure clears error state", () =>
{
    using var eng = new PollingEngine();
    var cfg = new AccountConfig { Name = "a", Provider = ProviderKind.Anthropic };
    eng.Reconfigure(new[] { (cfg, "k") });
    eng.IngestFor(cfg.Id, AccountSnapshot.Failure(cfg.Id, "HTTP 500 – boom"));
    eng.IngestFor(cfg.Id, OkSnap(cfg.Id, 0.2));
    var v = eng.GetView()[0];
    Check(!v.IsStale && v.Failure == FailureKind.None && v.ConsecutiveFailures == 0, "cleared");
});

Run("Engine: rotated token keeps history, provider change resets it", () =>
{
    using var eng = new PollingEngine();
    var cfg = new AccountConfig { Name = "a", Provider = ProviderKind.Anthropic };
    eng.Reconfigure(new[] { (cfg, "k1") });
    eng.IngestFor(cfg.Id, OkSnap(cfg.Id, 0.2));
    eng.Reconfigure(new[] { (cfg, "k2") }); // token rotation
    Check(eng.GetView()[0].LastGood is not null, "history kept on rotation");
    var moved = new AccountConfig { Id = cfg.Id, Name = "a", Provider = ProviderKind.DeepSeek };
    eng.Reconfigure(new[] { (moved, "k2") });
    Check(eng.GetView()[0].LastGood is null, "reset on provider change");
});

Run("Engine: stable provider order instead of severity order", () =>
{
    using var eng = new PollingEngine();
    var ds = new AccountConfig { Name = "DeepSeek", Provider = ProviderKind.DeepSeek };
    var an = new AccountConfig { Name = "Anthropic b", Provider = ProviderKind.Anthropic };
    var an2 = new AccountConfig { Name = "Anthropic a", Provider = ProviderKind.Anthropic };
    eng.Reconfigure(new[] { (ds, "k"), (an, "k"), (an2, "k") });
    var names = eng.GetView().Select(v => v.Config.Name).ToList();
    Check(string.Join(",", names) == "Anthropic a,Anthropic b,DeepSeek", string.Join(",", names));
});

Run("Engine: failure classification", () =>
{
    Check(PollingEngine.ClassifyFailure("Kein API-Schlüssel hinterlegt (Einstellungen).") == FailureKind.AuthInvalid, "missing key");
    Check(PollingEngine.ClassifyFailure("HTTP 401: {\"error\":\"invalid api key\"}") == FailureKind.AuthInvalid, "401");
    Check(PollingEngine.ClassifyFailure("HttpRequestException: Name or service not known") == FailureKind.Network, "dns");
    Check(PollingEngine.ClassifyFailure("HTTP 503: upstream") == FailureKind.Network, "503");
    Check(PollingEngine.ClassifyFailure("HTTP 500: boom") == FailureKind.Other, "500");
});

// ---------- formatting ----------

Run("formatting: time spans", () =>
{
    Check(Formatting.Span(TimeSpan.FromMinutes(45)) == "45 min", "45min");
    Check(Formatting.Span(TimeSpan.FromHours(2.25)) == "2 h 15 min", "2h15");
    Check(Formatting.Span(TimeSpan.FromDays(1.5)) == "1 d 12 h", "1.5d");
    Check(Formatting.Span(null) == "∞", "null");
});

Run("formatting: spans are rounded, so texts computed seconds apart agree", () =>
{
    // 27 h minus a few seconds used to show "1 d 2 h" in one place and "1 d 3 h" in another
    Check(Formatting.Span(TimeSpan.FromHours(27) - TimeSpan.FromSeconds(5)) == "1 d 3 h",
        Formatting.Span(TimeSpan.FromHours(27) - TimeSpan.FromSeconds(5)));
    Check(Formatting.Span(TimeSpan.FromMinutes(156) - TimeSpan.FromSeconds(20)) == "2 h 36 min",
        Formatting.Span(TimeSpan.FromMinutes(156) - TimeSpan.FromSeconds(20)));
    Check(Formatting.Span(TimeSpan.FromSeconds(20)) == "<1 min", "<1 min");
});

Run("formatting: money in German style", () =>
{
    Check(Formatting.Money(23.42, "USD") == "23,42 $", Formatting.Money(23.42, "USD"));
    Check(Formatting.Money(1234.5, "CNY") == "1.234,50 ¥", Formatting.Money(1234.5, "CNY"));
});

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
