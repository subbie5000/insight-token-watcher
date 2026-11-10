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

Run("key protection: roundtrip, platform marker, garbage input", () =>
{
    var id = Guid.NewGuid();
    var cipher = KeyProtector.Protect("sk-test-secret-123", id);
    Check(KeyProtector.Unprotect(cipher, id) == "sk-test-secret-123", "roundtrip");
    if (!OperatingSystem.IsWindows())
        Check(cipher.StartsWith("v2:"), "linux marker, got: " + cipher[..Math.Min(10, cipher.Length)]);
    Check(KeyProtector.Unprotect("kein-base64", id) == "", "garbage -> empty");
});

Run("config store: linux dir, save/reload, private file", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "itw-test-" + Guid.NewGuid().ToString("N")[..8]);
    ConfigStore.AppDirOverride = dir;
    try
    {
        var store = new ConfigStore();
        var acc = new AccountConfig { Name = "Test", Provider = ProviderKind.DeepSeek };
        store.UpsertAccount(acc, "sk-key-123456");
        Check(store.Accounts.Count == 1, "saved");
        Check(store.PlainKey(store.Accounts[0]) == "sk-key-123456", "plain key roundtrip");

        var reloaded = new ConfigStore();
        Check(reloaded.Accounts.Count == 1, "reloaded");
        Check(reloaded.PlainKey(reloaded.Accounts[0]) == "sk-key-123456", "key survives reload");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(dir, "config.json"));
            Check((mode & UnixFileMode.UserRead) != 0 && (mode & UnixFileMode.GroupRead) == 0,
                "config 0600, got " + mode);
        }
    }
    finally
    {
        ConfigStore.AppDirOverride = null;
        Directory.Delete(dir, true);
    }
});

Run("provider catalog: matches ProviderInfo, complete and typed", () =>
{
    Check(ProvidersCatalog.All.Count == Enum.GetValues<ProviderKind>().Length,
        $"catalog count {ProvidersCatalog.All.Count} vs enum");
    foreach (ProviderKind k in Enum.GetValues<ProviderKind>())
    {
        var e = ProvidersCatalog.For(k);
        Check(e.Key == Enum.GetName(k)!, "key order/name " + e.Key);
        Check(e.DisplayName == ProviderInfo.DisplayName(k), "displayName drift " + e.Key);
        Check(e.DefaultBaseUrl == ProviderInfo.DefaultBaseUrl(k), "baseUrl drift " + e.Key);
        Check(e.DefaultPollSeconds == ProviderInfo.DefaultPollSeconds(k), "poll drift " + e.Key);
        if (ProviderInfo.IsInfoOnly(k)) Check(e.Kind == "infoOnly", "infoOnly marker " + e.Key);
    }
    var generic = ProvidersCatalog.For(ProviderKind.GenericOpenAi);
    Check(generic.DefaultBaseUrl == "" && generic.BaseUrlHint.Contains("/v1"), "generic needs manual base url");
});

Run("poller factory: header providers share the generic parser, infoOnly gets the pinger", () =>
{
    Check(ProviderInfo.Poller(ProviderKind.Groq) is GenericOpenAiPoller, "groq");
    Check(ProviderInfo.Poller(ProviderKind.Mistral) is GenericOpenAiPoller, "mistral");
    Check(ProviderInfo.Poller(ProviderKind.Xai) is GenericOpenAiPoller, "xai");
    Check(ProviderInfo.Poller(ProviderKind.Cerebras) is GenericOpenAiPoller, "cerebras");
    Check(ProviderInfo.Poller(ProviderKind.Together) is InfoOnlyPoller, "together");
    Check(ProviderInfo.Poller(ProviderKind.Gemini) is InfoOnlyPoller, "gemini");
    Check(ProviderInfo.Poller(ProviderKind.Perplexity) is InfoOnlyPoller, "perplexity");
});

Run("generic parser: mistral/xAI style headers produce token+request windows", () =>
{
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["x-ratelimit-limit-tokens"] = "32000",
        ["x-ratelimit-remaining-tokens"] = "29000",
        ["x-ratelimit-reset-tokens"] = "2m30s",
        ["x-ratelimit-limit-requests"] = "30",
        ["x-ratelimit-remaining-requests"] = "29",
        ["x-ratelimit-reset-requests"] = "1m"
    };
    var snap = GenericOpenAiPoller.ParseHeaders(Guid.NewGuid(), headers, 200, "{}");
    Check(snap.Ok && snap.Windows.Count == 2, "windows " + snap.Windows.Count);
    var tok = snap.Windows.First(w => w.Key == "tokens");
    Check(Math.Abs((tok.UsedFraction ?? -1) - (1 - 29000.0 / 32000)) < 0.001, "fraction");
    Check(tok.ResetAtUtc is not null, "reset parsed");
});

Run("infoOnly poller: gemini 200 -> ok note, 401 -> failure, request carries key header", () =>
{
    string? seenKey = null; string? seenUrl = null; int status = 200;
    var http = new HttpClient(new FakeHandler((req, _) =>
    {
        seenUrl = req.RequestUri?.ToString();
        seenKey = req.Headers.TryGetValues("x-goog-api-key", out var v) ? v.First() : null;
        return new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent("{\"models\":[]}")
        };
    }));
    var cfg = new AccountConfig { Name = "G", Provider = ProviderKind.Gemini };
    var snap = ProviderInfo.Poller(ProviderKind.Gemini).PollAsync(cfg, "g-key", http, default).GetAwaiter().GetResult();
    Check(snap.Ok, "gemini ok");
    Check(seenUrl!.EndsWith("/v1/models"), "models url " + seenUrl);
    Check(seenKey == "g-key", "key header");
    Check(snap.Windows.Single().IsInfoOnly && snap.Windows.Single().Note!.Contains("nicht abfragbar"), "info note");

    status = 401;
    var fail = ProviderInfo.Poller(ProviderKind.Gemini).PollAsync(cfg, "bad", http, default).GetAwaiter().GetResult();
    Check(!fail.Ok && fail.Error!.Contains("401"), "401 -> failure");
});

Run("infoOnly poller: perplexity ping shape and together bearer", () =>
{
    string? body = null; string? auth = null; string? url = null;
    var http = new HttpClient(new FakeHandler((req, _) =>
    {
        url = req.RequestUri?.ToString();
        auth = req.Headers.Authorization?.Scheme;
        if (req.Content is not null)
            body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
    }));
    var cfg = new AccountConfig { Name = "P", Provider = ProviderKind.Perplexity };
    ProviderInfo.Poller(ProviderKind.Perplexity).PollAsync(cfg, "p-key", http, default).GetAwaiter().GetResult();
    Check(url == "https://api.perplexity.ai/chat/completions", "perplexity url " + url);
    Check(auth == "Bearer", "bearer");
    Check(body!.Contains("\"model\":\"sonar\"") && body.Contains("\"max_tokens\":1"), "ping shape " + body);

    var t = new AccountConfig { Name = "T", Provider = ProviderKind.Together };
    ProviderInfo.Poller(ProviderKind.Together).PollAsync(t, "t-key", http, default).GetAwaiter().GetResult();
    Check(url!.EndsWith("/v1/models"), "together url " + url);
});

// ---------- sorting (column titles) ----------

var sortNow = DateTimeOffset.UtcNow;

AccountView SortView(string name, double? weekUsed = null, double? resetHours = 48, double? tteHours = null,
    bool stale = false, bool balance = false, bool loading = false, double? fiveUsed = null, double? runwayHours = null)
{
    var cfg = new AccountConfig { Name = name, Provider = balance ? ProviderKind.DeepSeek : ProviderKind.Anthropic };
    if (loading) return new AccountView { Config = cfg };
    var snap = new AccountSnapshot { AccountId = cfg.Id, Ok = true, AtUtc = sortNow };
    var evals = new Dictionary<string, WindowEvaluation>();
    if (balance)
    {
        snap.Windows.Add(new QuotaWindow { Key = "bal", Label = "Guthaben", IsBalance = true, Remaining = 20, Unit = "USD" });
        if (runwayHours is { } rw)
            evals["bal"] = new WindowEvaluation { HasRate = true, RatePerMin = 0.01, TimeToEmpty = TimeSpan.FromHours(rw) };
    }
    if (fiveUsed is { } f)
        snap.Windows.Add(new QuotaWindow { Key = "5h", Label = "5h", WindowMinutes = 300, UsedFraction = f,
            ResetAtUtc = sortNow.AddHours(3) });
    if (weekUsed is { } u)
    {
        snap.Windows.Add(new QuotaWindow { Key = "7d", Label = "7d", WindowMinutes = 10080, UsedFraction = u,
            ResetAtUtc = resetHours is { } rh ? sortNow.AddHours(rh) : null });
        evals["7d"] = tteHours is { } t
            ? new WindowEvaluation { HasRate = true, RatePerMin = 0.001, TimeToEmpty = TimeSpan.FromHours(t) }
            : new WindowEvaluation();
    }
    return stale
        ? new AccountView { Config = cfg, Snapshot = AccountSnapshot.Failure(cfg.Id, "HTTP 401 expired"), LastGood = snap, Evals = evals }
        : new AccountView { Config = cfg, Snapshot = snap, LastGood = snap, Evals = evals };
}

string Names(IEnumerable<AccountView> views) => string.Join(",", views.Select(v => v.Config.Name));

Run("sort: names in natural order (2 before 10), case-insensitive", () =>
{
    var input = new[] { "Anthropic 10", "DeepSeek", "Anthropic 2", "anthropic 1" }.Select(n => SortView(n, 0.1)).ToList();
    Check(Names(AccountSort.Apply(input, SortColumn.Name, false, sortNow)) == "anthropic 1,Anthropic 2,Anthropic 10,DeepSeek",
        "asc " + Names(AccountSort.Apply(input, SortColumn.Name, false, sortNow)));
    Check(Names(AccountSort.Apply(input, SortColumn.Name, true, sortNow)) == "DeepSeek,Anthropic 10,Anthropic 2,anthropic 1",
        "desc " + Names(AccountSort.Apply(input, SortColumn.Name, true, sortNow)));
    Check(Names(AccountSort.Apply(input, SortColumn.None, false, sortNow)) == "Anthropic 10,DeepSeek,Anthropic 2,anthropic 1",
        "None keeps the given (engine) order");
});

Run("sort: title click cycles ascending → descending → default", () =>
{
    Check(AccountSort.Next(SortColumn.None, false, SortColumn.Long) == (SortColumn.Long, false), "1st click");
    Check(AccountSort.Next(SortColumn.Long, false, SortColumn.Long) == (SortColumn.Long, true), "2nd click");
    Check(AccountSort.Next(SortColumn.Long, true, SortColumn.Long) == (SortColumn.None, false), "3rd click");
    Check(AccountSort.Next(SortColumn.Long, true, SortColumn.Name) == (SortColumn.Name, false), "other column starts ascending");
});

Run("sort: week – runs out first on top, then fullest; no week window last; blocked below everything", () =>
{
    var input = new List<AccountView>
    {
        SortView("E-balance", balance: true),
        SortView("A-blocked", weekUsed: 1.0),
        SortView("F-loading", loading: true),
        SortView("B-empty-in-10h", weekUsed: 0.5, resetHours: 72, tteHours: 10),
        SortView("C-80-idle", weekUsed: 0.8),
        SortView("D-20-safe", weekUsed: 0.2, resetHours: 24, tteHours: 50), // reset comes first
        SortView("G-90-stale", weekUsed: 0.9, tteHours: 1, stale: true)       // no projection on stale data
    };
    var asc = Names(AccountSort.Apply(input, SortColumn.Long, false, sortNow));
    Check(asc == "B-empty-in-10h,G-90-stale,C-80-idle,D-20-safe,E-balance,F-loading,A-blocked", "asc " + asc);
    var desc = Names(AccountSort.Apply(input, SortColumn.Long, true, sortNow));
    Check(desc == "E-balance,D-20-safe,C-80-idle,G-90-stale,B-empty-in-10h,F-loading,A-blocked", "desc " + desc);
});

Run("sort: a balance running dry within the column horizon ranks as 'runs out'", () =>
{
    var input = new List<AccountView>
    {
        SortView("DeepSeek-27d", balance: true, runwayHours: 27 * 24),
        SortView("Moonshot-unmeasured", balance: true),
        SortView("OpenRouter-16h", balance: true, runwayHours: 16.7),
        SortView("Quota-50", weekUsed: 0.5),
        SortView("Groq-error", loading: true)
    };
    var asc = Names(AccountSort.Apply(input, SortColumn.Long, false, sortNow));
    Check(asc == "OpenRouter-16h,Quota-50,DeepSeek-27d,Moonshot-unmeasured,Groq-error", "week asc " + asc);
    var desc = Names(AccountSort.Apply(input, SortColumn.Long, true, sortNow));
    Check(desc == "DeepSeek-27d,Moonshot-unmeasured,Quota-50,OpenRouter-16h,Groq-error", "week desc " + desc);
    var five = Names(AccountSort.Apply(input, SortColumn.Short, false, sortNow));
    Check(five == "DeepSeek-27d,Moonshot-unmeasured,OpenRouter-16h,Quota-50,Groq-error", "16 h > 5 h horizon: " + five);
});

Run("sort: 5h column uses the short window; a passed reset counts as fresh", () =>
{
    var input = new List<AccountView>
    {
        SortView("week-only", weekUsed: 0.9),
        SortView("five-70", weekUsed: 0.1, fiveUsed: 0.7),
        SortView("five-30", weekUsed: 0.1, fiveUsed: 0.3),
        SortView("week-reset-passed", weekUsed: 1.0, resetHours: -1)
    };
    Check(Names(AccountSort.Apply(input, SortColumn.Short, false, sortNow)) == "five-70,five-30,week-only,week-reset-passed",
        "short " + Names(AccountSort.Apply(input, SortColumn.Short, false, sortNow)));
    var week = Names(AccountSort.Apply(input, SortColumn.Long, false, sortNow));
    Check(week == "week-only,five-70,five-30,week-reset-passed", "blocked but reset passed is not 'empty' " + week);
});

// ---------- usable right now? ----------

AccountView Acc(string name, bool stale, params QuotaWindow[] windows)
{
    var cfg = new AccountConfig { Name = name, Provider = ProviderKind.Anthropic };
    var snap = new AccountSnapshot { AccountId = cfg.Id, Ok = true, AtUtc = sortNow };
    snap.Windows.AddRange(windows);
    return stale
        ? new AccountView { Config = cfg, Snapshot = AccountSnapshot.Failure(cfg.Id, "HTTP 401 expired"), LastGood = snap }
        : new AccountView { Config = cfg, Snapshot = snap, LastGood = snap };
}
QuotaWindow Win(string key, double minutes, double used, double? resetHours) => new()
{
    Key = key, WindowMinutes = minutes, UsedFraction = used, ResetAtUtc = resetHours is { } h ? sortNow.AddHours(h) : null
};
QuotaWindow Bal(double remaining, bool unusable) => new()
{
    Key = "balance", IsBalance = true, Remaining = remaining, Unit = "USD", Unusable = unusable
};

Run("usability: blocked = a general limit is reached and has not reset yet", () =>
{
    var week = Usability.Blocked(Acc("w", false, Win("5h", 300, 0.2, 3), Win("7d", 10080, 1.0, 63)), sortNow);
    Check(week is { Window.Key: "7d" } && week.FreeAtUtc == sortNow.AddHours(63), "week at 100 %: free at its reset");
    Check(Usability.Blocked(Acc("ok", false, Win("5h", 300, 0.97, 3), Win("7d", 10080, 0.99, 63)), sortNow) is null,
        "97 % and 99 % still work – running dry is a forecast, not a block");
    Check(Usability.Blocked(Acc("passed", false, Win("7d", 10080, 1.0, -1)), sortNow) is null, "reset passed: fresh again");
    var noReset = Usability.Blocked(Acc("unknown", false, Win("7d", 10080, 1.0, null)), sortNow);
    Check(noReset is { FreeAtUtc: null }, "limit reached, reset unknown: blocked without a time");
    Check(Usability.Blocked(new AccountView { Config = new AccountConfig { Name = "loading" } }, sortNow) is null, "no data");
});

Run("usability: several limits reached – free when the last one resets", () =>
{
    var both = Usability.Blocked(Acc("b", false, Win("5h", 300, 1.0, 3), Win("7d", 10080, 1.0, 63)), sortNow);
    Check(both is { Window.Key: "7d" } && both.FreeAtUtc == sortNow.AddHours(63), "the week decides, not the 5 h window");
    var reversed = Usability.Blocked(Acc("r", false, Win("7d", 10080, 1.0, 63), Win("5h", 300, 1.0, 3)), sortNow);
    Check(reversed is { Window.Key: "7d" }, "independent of the order the provider lists them in");
    var five = Usability.Blocked(Acc("f", false, Win("5h", 300, 1.0, 3), Win("7d", 10080, 0.4, 63)), sortNow);
    Check(five is { Window.Key: "5h" } && five.FreeAtUtc == sortNow.AddHours(3), "only the 5 h window: free in 3 h");
    var open = Usability.Blocked(Acc("o", false, Win("7d", 10080, 1.0, null), Win("5h", 300, 1.0, 3)), sortNow);
    Check(open is { FreeAtUtc: null }, "one of them without a reset time: no time can be promised");
});

Run("usability: a model limit (Fable) never blocks the account", () =>
{
    Check(Usability.Blocked(Acc("fable", false, Win("7d", 10080, 0.4, 63), FableWin(1.0)), sortNow) is null,
        "Fable used up, week has room: every other model still runs");
    var week = Usability.Blocked(Acc("both", false, Win("7d", 10080, 1.0, 63), FableWin(1.0, 100)), sortNow);
    Check(week is { Window.Key: "7d" } && week.FreeAtUtc == sortNow.AddHours(63), "the general week decides, not the Fable reset");
});

Run("usability: a limit that was reached stays reached when a later poll fails", () =>
{
    Check(Usability.Blocked(Acc("stale", true, Win("7d", 10080, 1.0, 63)), sortNow) is { Window.Key: "7d" }, "old values, reset ahead");
    Check(Usability.Blocked(Acc("stale-passed", true, Win("7d", 10080, 1.0, -1)), sortNow) is null, "old values, reset passed");
});

Run("usability: a balance blocks only when the poller says it cannot pay", () =>
{
    var unusable = Usability.Blocked(Acc("ds", false, Bal(-0.01, true)), sortNow);
    Check(unusable is { FreeAtUtc: null, Window.IsBalance: true }, "unusable balance: blocked, no time known");
    Check(Usability.Blocked(Acc("zero", false, Bal(0, false)), sortNow) is null,
        "0 $ without the flag (e.g. OpenAI credit grants of an invoiced organisation) is not a block");
    Check(Usability.Blocked(Acc("low", false, Bal(0.59, false)), sortNow) is null, "a small balance still works");
    var info = new QuotaWindow { Key = "key-info", IsInfoOnly = true, UsedFraction = 1.0 };
    Check(Usability.Blocked(Acc("info", false, info, Bal(5, false)), sortNow) is null, "info values never block");
});

Run("pollers: which balances count as unusable", () =>
{
    var ds = DeepSeekPoller.ParseBody(Guid.NewGuid(),
        """{ "is_available": false, "balance_infos": [ { "currency": "USD", "total_balance": "-0.01", "granted_balance": "0.00", "topped_up_balance": "-0.01" } ] }""");
    Check(ds.Ok && ds.Windows[0].Unusable && ds.Windows[0].Note!.Contains("nicht nutzbar"), "DeepSeek is_available=false");
    var dsOk = DeepSeekPoller.ParseBody(Guid.NewGuid(),
        """{ "is_available": true, "balance_infos": [ { "currency": "USD", "total_balance": "0.02", "granted_balance": "0.00", "topped_up_balance": "0.02" } ] }""");
    Check(dsOk.Ok && !dsOk.Windows[0].Unusable, "DeepSeek is_available=true: usable, however little is left");

    var empty = MoonshotPoller.ParseBody(Guid.NewGuid(), """{"available_balance":0,"voucher_balance":0,"cash_balance":0}""", "https://api.moonshot.ai");
    var debt = MoonshotPoller.ParseBody(Guid.NewGuid(), """{"available_balance":-1.5,"voucher_balance":0,"cash_balance":-1.5}""", "https://api.moonshot.ai");
    var some = MoonshotPoller.ParseBody(Guid.NewGuid(), """{"available_balance":0.01,"voucher_balance":0,"cash_balance":0.01}""", "https://api.moonshot.ai");
    Check(empty.Windows[0].Unusable && debt.Windows[0].Unusable && !some.Windows[0].Unusable, "Moonshot: available_balance <= 0");

    AccountSnapshot OpenRouter(double credits, double usage)
    {
        using var http = new HttpClient(new FakeHandler((req, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(req.RequestUri!.AbsolutePath.EndsWith("/credits")
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"data\":{{\"total_credits\":{credits},\"total_usage\":{usage}}}}}")
                : """{"data":{"limit":null,"usage_daily":0,"usage_weekly":0,"usage_monthly":0,"is_free_tier":false}}""")
        }));
        var cfg = new AccountConfig { Name = "OR", Provider = ProviderKind.OpenRouter };
        return ProviderInfo.Poller(ProviderKind.OpenRouter).PollAsync(cfg, "k", http, default).GetAwaiter().GetResult();
    }
    Check(!OpenRouter(10, 9.41).Windows.Single(w => w.IsBalance).Unusable, "OpenRouter 0.59 $ left");
    Check(OpenRouter(10, 10).Windows.Single(w => w.IsBalance).Unusable, "OpenRouter used up");
    Check(OpenRouter(10, 10.004).Windows.Single(w => w.IsBalance).Unusable, "OpenRouter: what the row shows as 0,00 $ is used up");

    var grants = OpenAiPoller.ParseCreditGrants(Guid.NewGuid(), """{"total_granted":0,"total_used":0,"total_available":0}""");
    Check(grants.Ok && !grants.Windows[0].Unusable, "OpenAI: no grants says nothing about an invoiced organisation");
});

Run("sort: accounts that cannot be used go below all others – in every column and direction", () =>
{
    var input = new List<AccountView>
    {
        Acc("B-week-full-63h", false, Win("5h", 300, 0.0, 3), Win("7d", 10080, 1.0, 63)),
        Acc("A-usable-90", false, Win("5h", 300, 0.5, 3), Win("7d", 10080, 0.9, 20)),
        Acc("E-balance-empty", false, Bal(-0.01, true)),
        Acc("D-usable-10", false, Win("5h", 300, 0.1, 3), Win("7d", 10080, 0.1, 100)),
        Acc("C-5h-full-3h", false, Win("5h", 300, 1.0, 3), Win("7d", 10080, 0.3, 100)),
        Acc("F-week-full-142h", true, Win("7d", 10080, 1.0, 142)),
        new AccountView { Config = new AccountConfig { Name = "G-loading" } }
    };
    const string blocked = "C-5h-full-3h,B-week-full-63h,F-week-full-142h,E-balance-empty";
    string Order(SortColumn c, bool desc) => Names(AccountSort.Apply(input, c, desc, sortNow));
    Check(Order(SortColumn.None, false) == "A-usable-90,D-usable-10,G-loading," + blocked, "default " + Order(SortColumn.None, false));
    Check(Order(SortColumn.Name, false) == "A-usable-90,D-usable-10,G-loading," + blocked, "name asc " + Order(SortColumn.Name, false));
    Check(Order(SortColumn.Name, true) == "G-loading,D-usable-10,A-usable-90," + blocked, "name desc " + Order(SortColumn.Name, true));
    Check(Order(SortColumn.Long, false) == "A-usable-90,D-usable-10,G-loading," + blocked, "week asc " + Order(SortColumn.Long, false));
    Check(Order(SortColumn.Long, true) == "D-usable-10,A-usable-90,G-loading," + blocked, "week desc " + Order(SortColumn.Long, true));
    Check(Order(SortColumn.Short, false) == "A-usable-90,D-usable-10,G-loading," + blocked, "5h asc " + Order(SortColumn.Short, false));
    Check(Order(SortColumn.Model, true) == "A-usable-90,D-usable-10,G-loading," + blocked, "model desc " + Order(SortColumn.Model, true));

    // every blocked account sits below every usable one – for all 2^n subsets the same rule
    foreach (var column in new[] { SortColumn.None, SortColumn.Name, SortColumn.Short, SortColumn.Long, SortColumn.Model })
        foreach (var desc in new[] { false, true })
        {
            var order = AccountSort.Apply(input, column, desc, sortNow);
            var firstBlocked = order.FindIndex(v => Usability.Blocked(v, sortNow) is not null);
            Check(firstBlocked == 3 && order.Skip(firstBlocked).All(v => Usability.Blocked(v, sortNow) is not null),
                $"{column}/{desc}: " + Names(order));
            Check(order.Count == input.Count && order.Distinct().Count() == input.Count, "nobody lost or doubled");
        }
});

Run("layout: a window of unknown length listed first does not take the 5 h column", () =>
{
    var mcp = new QuotaWindow { Key = "TIME_LIMIT-1", Label = "MCP / Zeitfenster", UsedFraction = 0.1 };
    var five = new QuotaWindow { Key = "c1", WindowMinutes = 300, UsedFraction = 0.3 };
    var week = new QuotaWindow { Key = "c2", WindowMinutes = 10080, UsedFraction = 0.5 };
    var l = WindowLayout.Classify(new List<QuotaWindow> { mcp, five, week });
    Check(l.Short == five && l.Long == week, "short/long by length");
    Check(l.Extras.Count == 1 && l.Extras[0] == mcp, "unknown one goes to extras");
    var g = WindowLayout.Classify(new List<QuotaWindow> { mcp, new QuotaWindow { Key = "t" } });
    Check(g.Short == mcp, "unknown windows still fill free columns in order");
});

Run("time: epoch units by magnitude, never throws", () =>
{
    const long s = 1774933200;
    Check(TimeParsing.FromEpochGuess(s).ToUnixTimeSeconds() == s, "seconds");
    Check(TimeParsing.FromEpochGuess(s * 1000).ToUnixTimeSeconds() == s, "ms");
    Check(TimeParsing.FromEpochGuess(s * 1_000_000).ToUnixTimeSeconds() == s, "µs");
    Check(TimeParsing.FromEpochGuess(s * 1_000_000_000).ToUnixTimeSeconds() == s, "ns");
    Check(TimeParsing.FromEpochGuess(long.MaxValue).Year == 2262, "max as ns, no throw");
    Check(TimeParsing.FromEpochGuess(long.MinValue).Year == 1677, "min as ns, no throw");
    Check(TimeParsing.FromEpochGuess(900_000_000_000).Year == 9999, "absurd seconds clamped");
});

Run("generic reset: '20ms' is milliseconds, not minutes", () =>
{
    var now = DateTimeOffset.UtcNow;
    var r = GenericOpenAiPoller.ParseReset("20ms", now)!.Value;
    Check((r - now).TotalSeconds < 1, "20ms → " + (r - now));
    Check(Math.Abs((GenericOpenAiPoller.ParseReset("2m59.5s", now)!.Value - now).TotalSeconds - 179.5) < 0.01, "2m59.5s");
    Check(Math.Abs((GenericOpenAiPoller.ParseReset("6m0s", now)!.Value - now).TotalSeconds - 360) < 0.01, "6m0s");
});

Run("sort: on a tie the confirmed value ranks before the stale one", () =>
{
    var input = new List<AccountView> { SortView("stale-100", weekUsed: 1.0, stale: true), SortView("live-100", weekUsed: 1.0) };
    Check(Names(AccountSort.Apply(input, SortColumn.Long, false, sortNow)) == "live-100,stale-100", "asc");
    Check(Names(AccountSort.Apply(input, SortColumn.Long, true, sortNow)) == "live-100,stale-100", "desc");
});

Run("import: every provider kind is importable under its own type", () =>
{
    foreach (var (raw, want) in new (string, ProviderKind)[]
             {
                 ("groq", ProviderKind.Groq), ("Together", ProviderKind.Together), ("cerebras", ProviderKind.Cerebras),
                 ("mistral", ProviderKind.Mistral), ("xai", ProviderKind.Xai), ("grok", ProviderKind.Xai),
                 ("gemini", ProviderKind.Gemini), ("perplexity", ProviderKind.Perplexity), ("glm", ProviderKind.Zai),
                 ("kimi-code", ProviderKind.KimiCode), ("openai-compatible", ProviderKind.GenericOpenAi),
                 ("z.ai", ProviderKind.Zai), ("anthropic", ProviderKind.Anthropic)
             })
        Check(PlainAccountFile.TryParseProvider(raw, out var got) && got == want, $"{raw} → {got}");
    foreach (var k in Enum.GetValues<ProviderKind>())
        Check(PlainAccountFile.TryParseProvider(k.ToString(), out var g) && g == k, "enum name " + k);
    var (cfg, _, err) = new PlainAccountFile().ToConfig(new PlainAccountDto { Name = "G", Provider = "gemini", ApiKey = "k" });
    Check(err is null && cfg.PollSeconds == 3600, "info-only default interval, got " + cfg?.PollSeconds);
});

Run("engine: default order is natural by name within a provider", () =>
{
    using var eng = new PollingEngine();
    var list = new[] { "Anthropic 10", "Anthropic 9", "Anthropic 1" }
        .Select(n => (new AccountConfig { Name = n, Provider = ProviderKind.Anthropic }, "k")).ToList();
    eng.Reconfigure(list);
    Check(Names(eng.GetView()) == "Anthropic 1,Anthropic 9,Anthropic 10", Names(eng.GetView()));
});

Run("burn window: the setting changes the look-back (was ignored before)", () =>
{
    var now = DateTimeOffset.UtcNow;
    var w = new QuotaWindow { Key = "7d", WindowMinutes = 10080, UsedFraction = 0.5, ResetAtUtc = now.AddDays(3) };
    // consumption only between 120 and 60 minutes ago, flat since
    var samples = new[] { (120, 0.1), (90, 0.3), (60, 0.5), (30, 0.5), (0, 0.5) }
        .Select(p => new Sample(now.AddMinutes(-p.Item1), p.Item2, null)).ToList();
    Check(!Evaluator.Evaluate(w, samples, now, 60).HasRate, "60 min sees no consumption");
    Check(Evaluator.Evaluate(w, samples, now, 180).HasRate, "180 min sees the earlier consumption");
    using var eng = new PollingEngine { BurnWindowMinutes = 5 };
    Check(eng.BurnWindowMinutes == 15, "clamped to 15, got " + eng.BurnWindowMinutes);
});

Run("config store: a UI save keeps an import that landed meanwhile; untouched key stays rotated", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "itw-test-" + Guid.NewGuid().ToString("N")[..8]);
    ConfigStore.AppDirOverride = dir;
    try
    {
        var app = new ConfigStore();                        // the running tray app
        var acc = new AccountConfig { Name = "Claude 1", Provider = ProviderKind.Anthropic };
        app.UpsertAccount(acc, "old-token");
        var appCopy = app.Accounts[0].Clone();               // what an open edit dialog holds

        Thread.Sleep(20);
        var import = new ConfigStore();                      // the sync job's --import
        var rotated = import.Accounts[0].Clone();
        import.UpsertAccount(rotated, "new-token", save: false);
        import.UpsertAccount(new AccountConfig { Name = "Claude 2", Provider = ProviderKind.Anthropic }, "t2", save: false);
        import.Save();

        app.UpdateSettings(s => s.SortColumn = SortColumn.Long);   // click on WOCHE
        var after = new ConfigStore();
        Check(after.Accounts.Count == 2, "import kept, got " + after.Accounts.Count);
        Check(after.Settings.SortColumn == SortColumn.Long, "sort saved");

        appCopy.Name = "Claude 1 (umbenannt)";                       // edit without touching the key
        app.UpsertAccount(appCopy, null);
        var final = new ConfigStore();
        var c1 = final.Accounts.Single(a => a.Id == acc.Id);
        Check(c1.Name == "Claude 1 (umbenannt)", "rename saved");
        Check(final.PlainKey(c1) == "new-token", "rotated token survives the stale dialog copy, got " + final.PlainKey(c1));
        Check(final.Accounts.Count == 2, "second import account still there");
    }
    finally
    {
        ConfigStore.AppDirOverride = null;
        Directory.Delete(dir, true);
    }
});

// ---------- model-scoped limits (Fable) ----------

// observed verbatim on 2026-11-10: ping with claude-fable-5-2 + Claude Code system prompt
Dictionary<string, string> FableHeaders(string used) => new()
{
    ["anthropic-ratelimit-unified-5h-reset"] = "1794289800",
    ["anthropic-ratelimit-unified-5h-status"] = "allowed",
    ["anthropic-ratelimit-unified-5h-utilization"] = "0.02",
    ["anthropic-ratelimit-unified-7d-reset"] = "1794801600",
    ["anthropic-ratelimit-unified-7d-status"] = "allowed",
    ["anthropic-ratelimit-unified-7d-utilization"] = "0.15",
    ["anthropic-ratelimit-unified-7d_oi-reset"] = "1794801600",
    ["anthropic-ratelimit-unified-7d_oi-status"] = "allowed",
    ["anthropic-ratelimit-unified-7d_oi-utilization"] = used,
    ["anthropic-ratelimit-unified-fallback-percentage"] = "0.5",
    ["anthropic-ratelimit-unified-overage-disabled-reason"] = "org_level_disabled",
    ["anthropic-ratelimit-unified-overage-status"] = "rejected",
    ["anthropic-ratelimit-unified-representative-claim"] = "five_hour",
    ["anthropic-ratelimit-unified-reset"] = "1794289800",
    ["anthropic-ratelimit-unified-status"] = "allowed",
};

// observed verbatim on 2026-11-10 (GET /api/oauth/usage, shortened to the fields in use)
const string UsageJson = """
{"five_hour":{"utilization":54.0,"resets_at":"2026-11-10T04:00:00.338536+00:00"},
 "seven_day":{"utilization":57.0,"resets_at":"2026-11-16T11:00:00.338558+00:00"},
 "seven_day_opus":null,"seven_day_sonnet":null,"extra_usage":{"is_enabled":false},
 "limits":[
  {"kind":"session","group":"session","percent":54,"severity":"normal","resets_at":"2026-11-10T04:00:00.338536+00:00","scope":null,"is_active":false},
  {"kind":"weekly_all","group":"weekly","percent":57,"severity":"normal","resets_at":"2026-11-16T11:00:00.338558+00:00","scope":null,"is_active":true},
  {"kind":"weekly_scoped","group":"weekly","percent":14,"severity":"normal","resets_at":"2026-11-16T11:00:00.338721+00:00","scope":{"model":{"id":null,"display_name":"Fable"},"surface":null},"is_active":false}]}
""";

Run("Anthropic: header 7d_oi is the Fable week (own scope, not a general window)", () =>
{
    var s = AnthropicPoller.ParseHeaders(Guid.NewGuid(), FableHeaders("0.14"), 200, "{}");
    Check(s.Ok && s.Windows.Count == 3, "three windows, got " + s.Windows.Count);
    Check(string.Join(",", s.Windows.Select(w => w.Key)) == "5h,7d,7d_fable", "order " + string.Join(",", s.Windows.Select(w => w.Key)));
    var f = s.Windows[2];
    Check(f.Scope == "Fable" && f.Label == "Fable-Woche", $"scope/label {f.Scope}/{f.Label}");
    Check(f.WindowMinutes == 10080 && Math.Abs(f.UsedFraction!.Value - 0.14) < 1e-9, "week, 14 %");
    Check(f.ResetAtUtc!.Value.ToUnixTimeSeconds() == 1794801600, "reset");
    Check(s.Windows[0].Scope is null && s.Windows[1].Scope is null, "5h/7d are general");
    Check(AnthropicPoller.Canonical("7d_sonnet") == ("7d_sonnet", "Sonnet"), "sonnet scope");
    Check(AnthropicPoller.Canonical("5h") == ("5h", null), "plain window");
});

Run("Anthropic: usage endpoint – only model-scoped limits, named by Anthropic", () =>
{
    var list = AnthropicPoller.ParseUsageLimits(UsageJson);
    Check(list.Count == 1, "one scoped limit, got " + list.Count);
    var f = list[0];
    Check(f.Key == "7d_fable" && f.Scope == "Fable" && f.Label == "Fable-Woche", $"{f.Key}/{f.Scope}/{f.Label}");
    Check(Math.Abs(f.UsedFraction!.Value - 0.14) < 1e-9 && f.WindowMinutes == 10080, "14 % of the week");
    Check(f.ResetAtUtc == new DateTimeOffset(2026, 11, 16, 11, 0, 0, TimeSpan.Zero), "reset rounded to the second: " + f.ResetAtUtc);
    var jitter = AnthropicPoller.ParseUsageLimits(UsageJson.Replace("2026-11-16T11:00:00.338721", "2026-11-16T10:59:59.784639"));
    Check(jitter[0].ResetAtUtc == f.ResetAtUtc, "sub-second jitter before the hour gives the same reset: " + jitter[0].ResetAtUtc);
    Check(AnthropicPoller.ParseUsageLimits("""{"type":"error","error":{"type":"permission_error"}}""").Count == 0, "error body");
    Check(AnthropicPoller.ParseUsageLimits("""{"limits":[{"kind":"weekly_scoped","percent":3,"scope":{"model":null,"surface":"x"}}]}""").Count == 0,
        "scope without a model is skipped");
});

HttpResponseMessage Reply(int status, string body, Dictionary<string, string>? headers = null)
{
    var r = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };
    foreach (var (k, v) in headers ?? new())
        r.Headers.TryAddWithoutValidation(k, v);
    return r;
}

Dictionary<string, string> HaikuHeaders() =>
    FableHeaders("0").Where(h => !h.Key.Contains("7d_oi")).ToDictionary(h => h.Key, h => h.Value);

Run("Anthropic poll: login token reads the Fable week from the usage endpoint, at most every 5 min", () =>
{
    AnthropicPoller.ResetScopedCache();
    var calls = new List<string>();
    string? pingBody = null, usageBeta = null, usageAuth = null;
    using var http = new HttpClient(new FakeHandler((req, _) =>
    {
        calls.Add(req.Method + " " + req.RequestUri!.AbsolutePath);
        if (req.RequestUri.AbsolutePath == "/api/oauth/usage")
        {
            usageBeta = string.Join(",", req.Headers.GetValues("anthropic-beta"));
            usageAuth = req.Headers.Authorization?.Scheme;
            return Reply(200, UsageJson);
        }
        pingBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Reply(200, "{}", HaikuHeaders());
    }));
    var cfg = new AccountConfig { Name = "C", Provider = ProviderKind.Anthropic, Auth = AuthKind.Bearer };
    var poller = new AnthropicPoller();
    var s = poller.PollAsync(cfg, "tok-1", http, default).GetAwaiter().GetResult();
    Check(s.Ok && string.Join(",", s.Windows.Select(w => w.Key)) == "5h,7d,7d_fable", "windows " + string.Join(",", s.Windows.Select(w => w.Key)));
    Check(Math.Abs(s.Windows[2].UsedFraction!.Value - 0.14) < 1e-9, "fable 14 %");
    Check(string.Join(" | ", calls) == "POST /v1/messages | GET /api/oauth/usage", "calls " + string.Join(" | ", calls));
    Check(usageAuth == "Bearer" && usageBeta == "oauth-2025-04-20", "usage request shape");
    Check(pingBody!.Contains("claude-haiku") && pingBody.Contains("\"system\":\"You are Claude Code"), "ping stays Haiku, identifies as Claude Code: " + pingBody);

    var s2 = poller.PollAsync(cfg, "tok-1", http, default).GetAwaiter().GetResult();
    Check(s2.Windows.Count == 3, "second poll still reports the Fable week");
    Check(calls.Count == 3 && calls[2] == "POST /v1/messages", "no second usage request within 5 min: " + string.Join(" | ", calls));

    poller.PollAsync(cfg, "tok-2", http, default).GetAwaiter().GetResult();
    Check(calls.Count == 5 && calls[4] == "GET /api/oauth/usage", "a new token asks again: " + string.Join(" | ", calls));
});

Run("Anthropic poll: setup token (usage 403) falls back to a ping with the scoped model", () =>
{
    AnthropicPoller.ResetScopedCache();
    var calls = new List<string>();
    using var http = new HttpClient(new FakeHandler((req, _) =>
    {
        if (req.RequestUri!.AbsolutePath == "/api/oauth/usage")
        {
            calls.Add("usage");
            return Reply(403, """{"type":"error","error":{"type":"permission_error","message":"OAuth token does not meet scope requirement user:profile"}}""");
        }
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var fable = body.Contains("\"model\":\"" + AnthropicPoller.ScopedPingModel + "\"");
        calls.Add(fable ? "ping-fable" : "ping-haiku");
        return Reply(200, "{}", fable ? FableHeaders("0.31") : HaikuHeaders());
    }));
    var cfg = new AccountConfig { Name = "S", Provider = ProviderKind.Anthropic, Auth = AuthKind.Bearer };
    var poller = new AnthropicPoller();
    var s = poller.PollAsync(cfg, "setup-token", http, default).GetAwaiter().GetResult();
    Check(string.Join(",", calls) == "ping-haiku,usage,ping-fable", "calls " + string.Join(",", calls));
    var f = s.Windows.Single(w => w.Scope == "Fable");
    Check(f.Key == "7d_fable" && Math.Abs(f.UsedFraction!.Value - 0.31) < 1e-9, "fable from header 7d_oi");
    poller.PollAsync(cfg, "setup-token", http, default).GetAwaiter().GetResult();
    Check(calls.Count == 4 && calls[3] == "ping-haiku", "no extra request on the next poll: " + string.Join(",", calls));
});

Run("Anthropic poll: API key – no usage request, no Claude Code line; failed extra request keeps the poll", () =>
{
    AnthropicPoller.ResetScopedCache();
    var calls = new List<string>();
    string? body = null;
    using var http = new HttpClient(new FakeHandler((req, _) =>
    {
        calls.Add(req.RequestUri!.AbsolutePath);
        if (req.RequestUri.AbsolutePath == "/api/oauth/usage") throw new HttpRequestException("boom");
        body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Reply(200, "{}", HaikuHeaders());
    }));
    var api = new AccountConfig { Name = "K", Provider = ProviderKind.Anthropic, Auth = AuthKind.ApiKeyHeader };
    var s = new AnthropicPoller().PollAsync(api, "sk-test", http, default).GetAwaiter().GetResult();
    Check(s.Ok && s.Windows.Count == 2 && calls.Count == 1, "api key: one request, two windows");
    Check(!body!.Contains("system"), "no system line for API keys: " + body);

    var sub = new AccountConfig { Name = "B", Provider = ProviderKind.Anthropic, Auth = AuthKind.Bearer };
    var s2 = new AnthropicPoller().PollAsync(sub, "tok", http, default).GetAwaiter().GetResult();
    Check(s2.Ok && s2.Windows.Count == 2, "usage request failed → poll still ok with 5h/7d");
});

QuotaWindow FableWin(double used, double resetHours = 48) => new()
{
    Key = "7d_fable", Label = "Fable-Woche", Scope = "Fable", WindowMinutes = 10080, UsedFraction = used,
    ResetAtUtc = DateTimeOffset.UtcNow.AddHours(resetHours)
};

Run("engine: a full Fable week marks the account amber (FABLE LEER), never critical, no balloon", () =>
{
    using var eng = new PollingEngine();
    var cfg = new AccountConfig { Name = "a", Provider = ProviderKind.Anthropic };
    eng.Reconfigure(new[] { (cfg, "k") });
    AccountView? crit = null;
    eng.CritEntered += (v, _) => crit = v;
    var snap = OkSnap(cfg.Id, 0.2);
    snap.Windows.Add(FableWin(1.0));
    eng.IngestFor(cfg.Id, snap);
    var v = eng.GetView()[0];
    Check(v.Health == Health.Warn, "marked, but not critical – other models still run; got " + v.Health);
    Check(v.Evals["7d_fable"].Health == Health.Crit, "the Fable window itself is at its limit");
    Check(crit is null, "no balloon for a model limit");
    Check(WindowLayout.ModelLimitBehind(v) is { } cause && WindowLayout.ModelLimitStatus(cause) == "FABLE LEER", "status names the model limit");

    // week exhausted as well → the general window decides: critical, and it is the reason shown
    var both = OkSnap(cfg.Id, 1.0);
    both.Windows.Add(FableWin(1.0));
    eng.IngestFor(cfg.Id, both);
    var v2 = eng.GetView()[0];
    Check(v2.Health == Health.Crit && WindowLayout.ModelLimitBehind(v2) is null, "general limit wins: " + v2.Health);

    // fine again
    var fine = OkSnap(cfg.Id, 0.0);
    fine.Windows.Add(FableWin(0.0));
    eng.IngestFor(cfg.Id, fine);
    Check(eng.GetView()[0].Health == Health.Ok && WindowLayout.ModelLimitBehind(eng.GetView()[0]) is null, "unused → OK");
});

Run("layout: the Fable week gets the model column, other scopes stay extras", () =>
{
    var five = new QuotaWindow { Key = "5h", WindowMinutes = 300, UsedFraction = 0.1 };
    var week = new QuotaWindow { Key = "7d", WindowMinutes = 10080, UsedFraction = 0.2 };
    var sonnet = new QuotaWindow { Key = "7d_sonnet", Scope = "Sonnet", WindowMinutes = 10080, UsedFraction = 0.3 };
    var fable = FableWin(0.4);
    var l = WindowLayout.Classify(new List<QuotaWindow> { fable, five, sonnet, week }, "fable");
    Check(l.Short == five && l.Long == week, "general windows keep their columns even when listed later");
    Check(l.Model == fable, "model column");
    Check(l.Extras.Count == 1 && l.Extras[0] == sonnet, "other scope → extra");
    var none = WindowLayout.Classify(new List<QuotaWindow> { fable, week });
    Check(none.Model is null && none.Long == week && none.Extras.Single() == fable, "without a model column it is an extra");
    Check(WindowLayout.ShortLabel(fable) == "Fable", "label names the model, not 'Woche'");

    AccountView V(params QuotaWindow[] ws) => new()
    {
        Config = new AccountConfig { Name = "x", Provider = ProviderKind.Anthropic },
        Snapshot = new AccountSnapshot { Ok = true, Windows = ws.ToList() }
    };
    Check(WindowLayout.ColumnScope(new[] { V(week, fable), V(week, FableWin(0)), V(week, sonnet) }) == "Fable", "most frequent scope");
    Check(WindowLayout.ColumnScope(new[] { V(week), V(five) }) is null, "no scoped limit → no column");
});

Run("sort: model column – runs out first by the Fable week; accounts without it last", () =>
{
    AccountView F(string name, double? fable, double week = 0.5)
    {
        var cfg = new AccountConfig { Name = name, Provider = ProviderKind.Anthropic };
        var snap = new AccountSnapshot { AccountId = cfg.Id, Ok = true, AtUtc = sortNow };
        snap.Windows.Add(new QuotaWindow { Key = "7d", WindowMinutes = 10080, UsedFraction = week, ResetAtUtc = sortNow.AddHours(48) });
        if (fable is { } f) snap.Windows.Add(FableWin(f));
        return new AccountView { Config = cfg, Snapshot = snap, LastGood = snap };
    }
    var input = new List<AccountView>
    {
        F("no-fable-week-90", null, 0.9), F("fable-14", 0.14), SortView("balance", balance: true, runwayHours: 2),
        F("fable-full", 1.0), F("fable-0", 0.0)
    };
    var asc = Names(AccountSort.Apply(input, SortColumn.Model, false, sortNow));
    Check(asc == "fable-full,fable-14,fable-0,no-fable-week-90,balance", "asc " + asc);
    var desc = Names(AccountSort.Apply(input, SortColumn.Model, true, sortNow));
    Check(desc == "fable-0,fable-14,fable-full,no-fable-week-90,balance", "desc " + desc);
    Check(AccountSort.Next(SortColumn.Long, false, SortColumn.Model) == (SortColumn.Model, false), "click cycle knows the column");
});

Run("row texts: quiet second line names the provider – or what the account holds", () =>
{
    var quota = new RowLayout();
    var balance = new RowLayout { Span = new QuotaWindow { IsBalance = true } };
    string Sub(string name, ProviderKind k, RowLayout l) => WindowLayout.Subline(new AccountConfig { Name = name, Provider = k }, l);
    Check(Sub("Abo 3", ProviderKind.Anthropic, quota) == "Anthropic", "provider");
    Check(Sub("Kimi Code", ProviderKind.KimiCode, quota) == "Kontingent", "no 'Kimi Code / Kimi Code (Abo)': " + Sub("Kimi Code", ProviderKind.KimiCode, quota));
    Check(Sub("deepseek", ProviderKind.DeepSeek, balance) == "Guthaben", "balance account");
    Check(Sub("GLM Coding Plan", ProviderKind.Zai, quota) == "GLM (z.ai / BigModel)", "different → provider");

});

Run("config: sorting by the model column is stored so that older versions can still read the file", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "itw-test-" + Guid.NewGuid().ToString("N")[..8]);
    ConfigStore.AppDirOverride = dir;
    try
    {
        var store = new ConfigStore();
        store.UpdateSettings(s => s.SetSort(SortColumn.Model, true));
        var text = File.ReadAllText(Path.Combine(dir, "config.json"));
        Check(!text.Contains("\"Model\""), "no enum name an older version would reject: " + text);
        Check(text.Contains("\"SortColumn\": \"None\"") && text.Contains("\"SortByModel\": true"), "stored as flag: " + text);
        var back = new ConfigStore().Settings;
        Check(back.EffectiveSort == SortColumn.Model && back.SortDescending, "restored: " + back.EffectiveSort);

        store.UpdateSettings(s => s.SetSort(SortColumn.Long, false));
        back = new ConfigStore().Settings;
        Check(back.EffectiveSort == SortColumn.Long && !back.SortByModel && !back.SortDescending, "another column clears the flag");
        store.UpdateSettings(s => s.SetSort(SortColumn.None, true));
        Check(!new ConfigStore().Settings.SortDescending, "default order has no direction");
    }
    finally
    {
        ConfigStore.AppDirOverride = null;
        Directory.Delete(dir, true);
    }
});

Run("parked popup stays on the monitor it was dropped on", () =>
{
    // two rows of monitors, the lower row enumerated first (as Windows reported it on the
    // machine of the bug report); 48 px taskbar; the upper row is shifted 12 px to the right
    static (System.Drawing.Rectangle, System.Drawing.Rectangle) Screen(int x, int y) =>
        (new System.Drawing.Rectangle(x, y, 1920, 1080), new System.Drawing.Rectangle(x, y, 1920, 1032));
    var screens = new[] { Screen(-1920, 0), Screen(0, 0), Screen(12, -1080), Screen(-3840, 0), Screen(-1908, -1080), Screen(-3828, -1080) };
    const int width = 560, desired = 907, title = 77; // 13 accounts: taller than the 85 % cap (877)
    System.Drawing.Rectangle Park(int x, int y) =>
        PopupPlacement.Parked(new System.Drawing.Point(x, y), width, desired, title, screens)!.Value;

    // dropped on the upper monitor above the primary, not at its very top: the full desired height
    // would reach into the primary below – the popup must still stay on the upper monitor
    var up = Park(600, -800);
    Check(up.Y < 0 && up.Bottom <= -48, $"dropped on the monitor above the primary → stays there, got {up}");
    Check(up.X == 600 && up.Height == 877, $"x kept, height capped: {up}");
    Check(up.Y == -925, $"shifted up just enough to fit above the taskbar: {up}");

    // upper-left monitor, lower-left monitor is enumerated earlier
    var upLeft = Park(-2513, -600);
    Check(upLeft.Y < 0 && upLeft.X == -2513, $"upper-left monitor → stays there, got {upLeft}");

    // a spot that already fits is not touched (no jump on the next data refresh)
    Check(Park(-2513, -1025) == new System.Drawing.Rectangle(-2513, -1025, width, 877), "fitting spot unchanged: " + Park(-2513, -1025));
    Check(Park(600, 100) == new System.Drawing.Rectangle(600, 100, width, 877), "primary, fitting spot unchanged");

    // title band across two monitors: the one showing more of it wins, then it is pulled fully onto it
    var across = Park(-200, 20); // 200 px on the left monitor, 360 px on the primary
    Check(across.X == 0 && across.Y == 20, $"more of the title on the primary → primary: {across}");

    // the result is stable: parking the result again gives the same bounds
    Check(Park(up.X, up.Y) == up, "stable after the first placement");

    // monitor gone → null (caller falls back to the tray anchor)
    Check(PopupPlacement.Parked(new System.Drawing.Point(9000, 9000), width, desired, title, screens) is null, "off all screens");

    // The full monitor wall of the report (11 screens in two rows, enumeration order as reported
    // by Windows, rows offset against each other, one taller taskbar): wherever the popup is
    // dropped, it ends up completely inside the working area of THAT monitor.
    static (System.Drawing.Rectangle, System.Drawing.Rectangle) Wall(int x, int y, int workHeight = 1032) =>
        (new System.Drawing.Rectangle(x, y, 1920, 1080), new System.Drawing.Rectangle(x, y, 1920, workHeight));
    var wall = new[]
    {
        Wall(-1920, 0), Wall(0, 0), Wall(5772, -1086, 1008), Wall(12, -1080), Wall(-3840, 0), Wall(-1908, -1080),
        Wall(-3828, -1080), Wall(1920, 0), Wall(3852, -1084), Wall(1932, -1084), Wall(3840, 0)
    };
    var drops = 0;
    foreach (var (bounds, work) in wall)
        foreach (var fx in new[] { 0.0, 0.3, 0.6 })       // the title band lies completely on this monitor
            foreach (var fy in new[] { 0.0, 0.1, 0.35, 0.6, 0.9 })
            {
                var at = new System.Drawing.Point(bounds.X + (int)(fx * 1920), bounds.Y + (int)(fy * 1000));
                var placed = PopupPlacement.Parked(at, width, desired, title, wall)!.Value;
                Check(work.Contains(placed), $"dropped at {at} on {bounds} → {placed} is not inside {work}");
                Check(placed.X == at.X, $"x untouched for a drop at {at}: {placed}");
                Check(PopupPlacement.Parked(placed.Location, width, desired, title, wall) == placed, $"stable at {placed}");
                drops++;
            }
    Check(drops == 165, "all drop points checked: " + drops);
});

Run("pace tick: share of the window's time that has passed", () =>
{
    var now = DateTimeOffset.UtcNow;
    QuotaWindow W(double? minutes, double? resetInMin, bool balance = false) => new()
    {
        Key = "w", WindowMinutes = minutes, IsBalance = balance,
        ResetAtUtc = resetInMin is { } m ? now.AddMinutes(m) : null
    };
    double? E(QuotaWindow w) => WindowLayout.ElapsedFraction(w, now);
    Check(Math.Abs(E(W(300, 216))!.Value - 0.28) < 1e-9, "5 h window, reset in 3 h 36 min → 28 %: " + E(W(300, 216)));
    Check(Math.Abs(E(W(10080, 10080 - 1440))!.Value - 1.0 / 7) < 1e-9, "week, one day in");
    Check(E(W(300, 300)) == 0, "just started");
    Check(E(W(300, 304)) == 0, "reset slightly beyond the window (clock skew) clamps to 0");
    Check(E(W(300, 400)) is null, "reset further away than one window: length does not fit");
    Check(E(W(300, -3)) is null, "reset has passed: old values, no tick");
    Check(E(W(null, 100)) is null && E(W(300, null)) is null, "unknown length or reset");
    Check(E(W(1, 0.5)) is null, "placeholder length");
    Check(E(W(300, 100, balance: true)) is null, "balances have no window");
});

Run("tray ring: mean of the general weeks – Fable weeks do not dilute it", () =>
{
    AccountView A(double week, double? fable)
    {
        var snap = new AccountSnapshot { Ok = true };
        snap.Windows.Add(new QuotaWindow { Key = "5h", WindowMinutes = 300, UsedFraction = 0.9 });
        snap.Windows.Add(new QuotaWindow { Key = "7d", WindowMinutes = 10080, UsedFraction = week });
        if (fable is { } f) snap.Windows.Add(FableWin(f));
        return new AccountView { Config = new AccountConfig { Name = "c", Provider = ProviderKind.Anthropic }, Snapshot = snap };
    }
    var ring = TrayGauge.WeekFraction(new[] { A(0.6, 0.0), A(0.2, 0.0) });
    Check(ring is { } r && Math.Abs(r - 0.4) < 1e-9, "0.4 expected, got " + ring);
    Check(TrayGauge.WeekFraction(Array.Empty<AccountView>()) is null, "no data → null");
});

Run("percent for cells: whole numbers, never rounded onto 0 % or 100 %", () =>
{
    Check(Formatting.PercentShort(0) == "0 %", "0");
    Check(Formatting.PercentShort(0.003) == "<1 %", "0.3 %");
    Check(Formatting.PercentShort(0.585) == "59 %" || Formatting.PercentShort(0.585) == "58 %", "58.5 → whole: " + Formatting.PercentShort(0.585));
    Check(Formatting.PercentShort(0.996) == "99 %", "99.6 stays below 100: " + Formatting.PercentShort(0.996));
    Check(Formatting.PercentShort(1.0) == "100 %", "100");
    Check(Formatting.PercentShort(null) == "–", "null");
});

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
sealed class FakeHandler : HttpClientHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;
    public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) => _responder = responder;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_responder(request, ct));
}

