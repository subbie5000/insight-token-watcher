using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenWatch.Core;

/// <summary>Plaintext import format (import/debug only – never persist permanently).</summary>
public sealed class PlainAccountDto
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Auth { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? BaseUrlOverride { get; set; }
    public string? Model { get; set; }
    public int? PollSeconds { get; set; }
    public bool? Enabled { get; set; }
    public string? ExternalId { get; set; }
}

public sealed class PlainAccountFile
{
    public List<PlainAccountDto> Accounts { get; set; } = new();

    /// <summary>
    /// Remove previously imported accounts that are missing from this file. Accounts added
    /// manually in the settings dialog are never touched.
    /// </summary>
    public bool Prune { get; set; }

    public static PlainAccountFile Load(string path)
    {
        var json = File.ReadAllText(path);
        var f = JsonSerializer.Deserialize<PlainAccountFile>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return f ?? new PlainAccountFile();
    }

    public (AccountConfig cfg, string key, string? error) ToConfig(PlainAccountDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return (null!, "", "Name fehlt");
        if (string.IsNullOrWhiteSpace(dto.ApiKey)) return (null!, "", $"{dto.Name}: apiKey fehlt");

        if (!TryParseProvider(dto.Provider, out var prov))
            return (null!, "", $"{dto.Name}: unbekannter provider '{dto.Provider}' " +
                               "(anthropic|kimicode|moonshot|zai|deepseek)");

        var auth = AuthKind.ApiKeyHeader;
        if (!string.IsNullOrWhiteSpace(dto.Auth))
        {
            if (dto.Auth.Equals("bearer", StringComparison.OrdinalIgnoreCase) ||
                dto.Auth.Equals("oauth", StringComparison.OrdinalIgnoreCase))
                auth = AuthKind.Bearer;
            else if (!dto.Auth.Equals("apikey", StringComparison.OrdinalIgnoreCase) &&
                     !dto.Auth.Equals("api_key", StringComparison.OrdinalIgnoreCase))
                return (null!, "", $"{dto.Name}: unbekannter auth-Typ '{dto.Auth}' (apikey|bearer)");
        }

        return (new AccountConfig
        {
            Name = dto.Name.Trim(),
            Provider = prov,
            Auth = prov == ProviderKind.Anthropic ? auth : AuthKind.Bearer,
            BaseUrlOverride = string.IsNullOrWhiteSpace(dto.BaseUrlOverride) ? null : dto.BaseUrlOverride.Trim(),
            Model = dto.Model?.Trim() ?? "",
            PollSeconds = dto.PollSeconds is > 0 ? dto.PollSeconds.Value : 60,
            Enabled = dto.Enabled ?? true,
            ExternalId = string.IsNullOrWhiteSpace(dto.ExternalId) ? null : dto.ExternalId.Trim()
        }, dto.ApiKey.Trim(), null);
    }

    private static bool TryParseProvider(string s, out ProviderKind kind)
    {
        switch (s.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", ""))
        {
            case "anthropic": kind = ProviderKind.Anthropic; return true;
            case "kimicode" or "kimi": kind = ProviderKind.KimiCode; return true;
            case "moonshot" or "moonshotbalance" or "kimibalance": kind = ProviderKind.Moonshot; return true;
            case "zai" or "glm" or "bigmodel" or "zhipu": kind = ProviderKind.Zai; return true;
            case "deepseek": kind = ProviderKind.DeepSeek; return true;
            case "openai": kind = ProviderKind.OpenAi; return true;
            case "openrouter": kind = ProviderKind.OpenRouter; return true;
            case "genericopenai" or "openaikompatibel" or "openaicompatible" or "groq" or "together" or "cerebras":
                kind = ProviderKind.GenericOpenAi; return true;
            default: kind = default; return false;
        }
    }
}
