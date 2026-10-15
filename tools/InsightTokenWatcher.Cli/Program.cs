using System.Text.Json;
using InsightTokenWatcher.Core;

// InsightTokenWatcher.Cli — poll all accounts once and print the result.
//   InsightTokenWatcher.Cli accounts-plain.json          (plaintext import file, for debugging)
//   InsightTokenWatcher.Cli --app-config [path]          (encrypted app config, Windows only)

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: InsightTokenWatcher.Cli <accounts.json | --app-config>");
    return 64;
}

var accounts = new List<(AccountConfig cfg, string key)>();

if (args[0] == "--app-config")
{
    var path = args.Length > 1
        ? args[1]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TokenWatch", "config.json");
    if (!File.Exists(path))
    {
        Console.Error.WriteLine("config.json nicht gefunden: " + path);
        return 66;
    }
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    var arr = root.TryGetProperty("accounts", out var accs) ? accs : root.GetProperty("Accounts");
    foreach (var a in arr.EnumerateArray())
    {
        string? Str(params string[] names)
        {
            foreach (var n in names)
                if (a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }
        var cfg = new AccountConfig
        {
            Id = (a.TryGetProperty("Id", out var idEl) ? idEl : a.GetProperty("id")).GetGuid(),
            Name = Str("Name", "name") ?? "?",
            Provider = Enum.Parse<ProviderKind>(Str("Provider", "provider") ?? "Anthropic"),
            Auth = Enum.Parse<AuthKind>(Str("Auth", "auth") ?? "ApiKeyHeader"),
            ApiKeyProtected = Str("ApiKeyProtected", "apiKeyProtected") ?? "",
            BaseUrlOverride = Str("BaseUrlOverride", "baseUrlOverride"),
            Model = Str("Model", "model") ?? "",
            PollSeconds = a.TryGetProperty("PollSeconds", out var p) ? p.GetInt32()
                : a.TryGetProperty("pollSeconds", out var p2) ? p2.GetInt32() : 60,
            Enabled = a.TryGetProperty("Enabled", out var e) ? e.GetBoolean()
                : !a.TryGetProperty("enabled", out var e2) || e2.GetBoolean()
        };
        var key = string.Empty;
#pragma warning disable CA1416 // DPAPI is Windows-only – this mode only runs there
        if (OperatingSystem.IsWindows())
            key = KeyProtector.Unprotect(cfg.ApiKeyProtected, cfg.Id);
#pragma warning restore CA1416
        accounts.Add((cfg, key));
    }
}
else
{
    var file = PlainAccountFile.Load(args[0]);
    foreach (var dto in file.Accounts)
    {
        var (cfg, key, err) = file.ToConfig(dto);
        if (err is not null) { Console.Error.WriteLine("Übersprungen: " + err); continue; }
        accounts.Add((cfg, key));
    }
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var failures = 0;

foreach (var (cfg, key) in accounts)
{
    Console.WriteLine(new string('─', 70));
    Console.WriteLine($"{cfg.Name}  [{ProviderInfo.DisplayName(cfg.Provider)}]  → {ProviderInfo.BaseUrl(cfg)}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    AccountSnapshot snap;
    try
    {
        snap = await ProviderInfo.Poller(cfg.Provider).PollAsync(cfg, key, http, default);
    }
    catch (Exception ex)
    {
        snap = AccountSnapshot.Failure(cfg.Id, ex.GetType().Name + ": " + ex.Message);
    }
    sw.Stop();

    if (!snap.Ok)
    {
        failures++;
        Console.WriteLine($"  FEHLER ({sw.ElapsedMilliseconds} ms): {snap.Error}");
        continue;
    }
    foreach (var w in snap.Windows)
    {
        string val;
        if (w.IsInfoOnly)
            val = w.Remaining is not null ? Formatting.Money(w.Remaining, w.Unit) : "";
        else if (w.IsBalance)
            val = Formatting.Money(w.Remaining, w.Unit);
        else
            val = $"{Formatting.Percent(w.UsedFraction)} verbraucht" +
              (w.Remaining is not null && w.Total is not null
                  ? $"  (noch {Formatting.Count(w.Remaining)} von {Formatting.Count(w.Total)} {w.Unit})" : "");
        var reset = w.ResetAtUtc is null ? "" :
            $"  Reset: {w.ResetAtUtc.Value.ToLocalTime():dd.MM. HH:mm} (in {Formatting.Span(w.ResetAtUtc.Value - DateTimeOffset.UtcNow)})";
        var note = string.IsNullOrEmpty(w.Note) ? "" : $"  [{w.Note}]";
        Console.WriteLine($"  {w.Label,-22} {val}{reset}{note}");
    }
    Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
}
Console.WriteLine(new string('─', 70));
Console.WriteLine(failures == 0 ? "ALLE KONTEN OK" : $"{failures} Konto/Konten mit FEHLER");
return failures == 0 ? 0 : 1;
