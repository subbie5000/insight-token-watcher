using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightTokenWatcher.Core;

public sealed class ProviderEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("poller")] public string Poller { get; set; } = "";
    /// <summary>quota = windows/bars, balance = money, infoOnly = reachability ping only.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "quota";
    [JsonPropertyName("authKind")] public string AuthKind { get; set; } = "Bearer";
    [JsonPropertyName("defaultBaseUrl")] public string DefaultBaseUrl { get; set; } = "";
    [JsonPropertyName("baseUrlHint")] public string BaseUrlHint { get; set; } = "";
    [JsonPropertyName("defaultPollSeconds")] public int DefaultPollSeconds { get; set; } = 60;
    [JsonPropertyName("infoNote")] public string? InfoNote { get; set; }
}

/// <summary>The provider catalog as DATA – shared source of truth for the Windows/Linux
/// add-dialogs and the iOS port (providers.json is copied into the app bundle there).
/// A test pins this file against ProviderInfo so the two can never drift.</summary>
public static class ProvidersCatalog
{
    private static IReadOnlyList<ProviderEntry>? _all;

    public static IReadOnlyList<ProviderEntry> All => _all ??= Load();

    public static ProviderEntry For(ProviderKind k)
    {
        var name = Enum.GetName(k) ?? throw new ArgumentOutOfRangeException(nameof(k));
        return All.First(p => p.Key == name);
    }

    private static IReadOnlyList<ProviderEntry> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().First(n => n.EndsWith("providers.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(res)!;
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("providers.json konnte nicht gelesen werden");
        return file.Providers;
    }

    private sealed class CatalogFile
    {
        public int Version { get; set; }
        public List<ProviderEntry> Providers { get; set; } = new();
    }
}
