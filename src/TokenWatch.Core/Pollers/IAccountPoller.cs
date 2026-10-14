using System.Text.Json;

namespace TokenWatch.Core;

public interface IAccountPoller
{
    Task<AccountSnapshot> PollAsync(AccountConfig cfg, string plainKey, HttpClient http, CancellationToken ct);
}

public static class ProviderInfo
{
    public static string DisplayName(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => "Anthropic",
        ProviderKind.KimiCode => "Kimi Code (Abo)",
        ProviderKind.Moonshot => "Moonshot / Kimi Guthaben",
        ProviderKind.Zai => "GLM (z.ai / BigModel)",
        ProviderKind.DeepSeek => "DeepSeek",
        _ => k.ToString()
    };

    public static string DefaultBaseUrl(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => "https://api.anthropic.com",
        ProviderKind.KimiCode => "https://api.kimi.com",
        ProviderKind.Moonshot => "https://api.moonshot.ai",
        ProviderKind.Zai => "https://api.z.ai",
        ProviderKind.DeepSeek => "https://api.deepseek.com",
        _ => ""
    };

    public static string BaseUrlHint(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => "Standard: https://api.anthropic.com",
        ProviderKind.KimiCode => "Standard: https://api.kimi.com",
        ProviderKind.Moonshot => "Standard: https://api.moonshot.ai · China: https://api.moonshot.cn",
        ProviderKind.Zai => "Global: https://api.z.ai · BigModel China: https://open.bigmodel.cn",
        ProviderKind.DeepSeek => "Standard: https://api.deepseek.com",
        _ => ""
    };

    public static string BaseUrl(AccountConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.BaseUrlOverride)
            ? DefaultBaseUrl(cfg.Provider)
            : cfg.BaseUrlOverride!.Trim().TrimEnd('/');

    public static IAccountPoller Poller(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => new AnthropicPoller(),
        ProviderKind.KimiCode => new KimiCodePoller(),
        ProviderKind.Moonshot => new MoonshotPoller(),
        ProviderKind.Zai => new ZaiPoller(),
        ProviderKind.DeepSeek => new DeepSeekPoller(),
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    public static string? JsonString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number)
                    return v.TryGetInt64(out var l) ? l.ToString() : v.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        return null;
    }

    public static double? JsonDouble(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
            }
        return null;
    }
}
