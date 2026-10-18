using System.Text.Json;

namespace InsightTokenWatcher.Core;

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
        ProviderKind.OpenAi => "OpenAI",
        ProviderKind.OpenRouter => "OpenRouter",
        ProviderKind.GenericOpenAi => "OpenAI-kompatibel",
        ProviderKind.Groq => "Groq",
        ProviderKind.Mistral => "Mistral",
        ProviderKind.Xai => "xAI Grok",
        ProviderKind.Cerebras => "Cerebras",
        ProviderKind.Together => "Together AI",
        ProviderKind.Gemini => "Google Gemini",
        ProviderKind.Perplexity => "Perplexity",
        _ => k.ToString()
    };

    public static string DefaultBaseUrl(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => "https://api.anthropic.com",
        ProviderKind.KimiCode => "https://api.kimi.com",
        ProviderKind.Moonshot => "https://api.moonshot.ai",
        ProviderKind.Zai => "https://api.z.ai",
        ProviderKind.DeepSeek => "https://api.deepseek.com",
        ProviderKind.OpenAi => "https://api.openai.com",
        ProviderKind.OpenRouter => "https://openrouter.ai",
        ProviderKind.Groq => "https://api.groq.com/openai/v1",
        ProviderKind.Mistral => "https://api.mistral.ai/v1",
        ProviderKind.Xai => "https://api.x.ai/v1",
        ProviderKind.Cerebras => "https://api.cerebras.ai/v1",
        ProviderKind.Together => "https://api.together.xyz/v1",
        ProviderKind.Gemini => "https://generativelanguage.googleapis.com",
        ProviderKind.Perplexity => "https://api.perplexity.ai",
        _ => ""
    };

    public static string BaseUrlHint(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => "Standard: https://api.anthropic.com",
        ProviderKind.KimiCode => "Standard: https://api.kimi.com",
        ProviderKind.Moonshot => "Standard: https://api.moonshot.ai · China: https://api.moonshot.cn",
        ProviderKind.Zai => "Global: https://api.z.ai · BigModel China: https://open.bigmodel.cn",
        ProviderKind.DeepSeek => "Standard: https://api.deepseek.com",
        ProviderKind.OpenAi => "Standard: https://api.openai.com · Org-Admin-Key (sk-admin-…) empfohlen",
        ProviderKind.OpenRouter => "Standard: https://openrouter.ai",
        ProviderKind.GenericOpenAi => "Basis-URL mit /v1, z.B. https://api.groq.com/openai/v1 · Modell (Ping) setzen!",
        ProviderKind.Groq => "Standard: https://api.groq.com/openai/v1 · Modell (Ping) setzen!",
        ProviderKind.Mistral => "Standard: https://api.mistral.ai/v1 · Modell (Ping) setzen!",
        ProviderKind.Xai => "Standard: https://api.x.ai/v1 · Modell (Ping) setzen!",
        ProviderKind.Cerebras => "Standard: https://api.cerebras.ai/v1 · Modell (Ping) setzen!",
        ProviderKind.Together => "Standard: https://api.together.xyz/v1",
        ProviderKind.Gemini => "Standard: https://generativelanguage.googleapis.com",
        ProviderKind.Perplexity => "Standard: https://api.perplexity.ai",
        _ => ""
    };

    public static string BaseUrl(AccountConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.BaseUrlOverride)
            ? DefaultBaseUrl(cfg.Provider)
            : cfg.BaseUrlOverride!.Trim().TrimEnd('/');

    public static bool IsInfoOnly(ProviderKind k) =>
        k is ProviderKind.Together or ProviderKind.Gemini or ProviderKind.Perplexity;

    /// <summary>Default poll interval for NEW accounts (seconds); InfoOnly providers ping rarely.</summary>
    public static int DefaultPollSeconds(ProviderKind k) => IsInfoOnly(k) ? 3600 : 60;

    public static IAccountPoller Poller(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => new AnthropicPoller(),
        ProviderKind.KimiCode => new KimiCodePoller(),
        ProviderKind.Moonshot => new MoonshotPoller(),
        ProviderKind.Zai => new ZaiPoller(),
        ProviderKind.DeepSeek => new DeepSeekPoller(),
        ProviderKind.OpenAi => new OpenAiPoller(),
        ProviderKind.OpenRouter => new OpenRouterPoller(),
        ProviderKind.Groq or ProviderKind.Mistral or ProviderKind.Xai or ProviderKind.Cerebras
            or ProviderKind.GenericOpenAi => new GenericOpenAiPoller(),
        ProviderKind.Together or ProviderKind.Gemini or ProviderKind.Perplexity => new InfoOnlyPoller(),
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
