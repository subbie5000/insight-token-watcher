using System.Text.Json.Serialization;

namespace TokenWatch.Core;

public enum ProviderKind
{
    Anthropic,
    KimiCode,
    Moonshot,
    Zai,
    DeepSeek
}

public enum AuthKind
{
    ApiKeyHeader, // Anthropic: x-api-key
    Bearer        // Authorization: Bearer (also Anthropic OAuth / setup-token)
}

public enum Health
{
    Unknown,
    Ok,
    Warn,
    Crit
}

public sealed class AccountConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProviderKind Provider { get; set; }
    public AuthKind Auth { get; set; } = AuthKind.ApiKeyHeader;
    public string ApiKeyProtected { get; set; } = "";
    public string? BaseUrlOverride { get; set; }
    public string Model { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string EffectiveModel =>
        !string.IsNullOrWhiteSpace(Model) ? Model.Trim() : "claude-haiku-4-5-20251001";
}

public sealed class QuotaWindow
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsBalance { get; set; }
    public double? UsedFraction { get; set; }
    public double? Remaining { get; set; }
    public double? Total { get; set; }
    public string Unit { get; set; } = "";
    public DateTimeOffset? ResetAtUtc { get; set; }
    public string? Note { get; set; }
}

public sealed class AccountSnapshot
{
    public Guid AccountId { get; set; }
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<QuotaWindow> Windows { get; set; } = new();

    public static AccountSnapshot Failure(Guid id, string error) =>
        new() { AccountId = id, Ok = false, Error = error };
}

public readonly record struct Sample(DateTimeOffset AtUtc, double UsedFraction, double? Remaining);
