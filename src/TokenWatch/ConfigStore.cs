using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenWatch.Core;

namespace TokenWatch;

internal sealed class AppSettings
{
    public bool ShowCritBalloons { get; set; } = true;
    public bool Autostart { get; set; }
    public int BurnWindowMinutes { get; set; } = 60;
}

internal sealed class ConfigFile
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<AccountConfig> Accounts { get; set; } = new();
}

/// <summary>Loads/saves %APPDATA%\TokenWatch\config.json; keys DPAPI-encrypted (LocalMachine).</summary>
internal sealed class ConfigStore
{
    private readonly string _dir;
    private readonly string _path;
    private ConfigFile _cfg = new();

    public ConfigStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenWatch");
        _path = Path.Combine(_dir, "config.json");
        Load();
    }

    public AppSettings Settings => _cfg.Settings;
    public IReadOnlyList<AccountConfig> Accounts => _cfg.Accounts;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _cfg = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions()) ?? new ConfigFile();
            }
        }
        catch
        {
            var backup = _path + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(_path, backup, false); } catch { }
            _cfg = new ConfigFile();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(_cfg, JsonOptions());
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, true);
    }

    public void UpsertAccount(AccountConfig acc, string? newPlainKey)
    {
        if (newPlainKey is not null)
            acc.ApiKeyProtected = Protect(newPlainKey, acc.Id);
        var idx = _cfg.Accounts.FindIndex(a => a.Id == acc.Id);
        if (idx >= 0) _cfg.Accounts[idx] = acc; else _cfg.Accounts.Add(acc);
        Save();
    }

    public void RemoveAccount(Guid id)
    {
        _cfg.Accounts.RemoveAll(a => a.Id == id);
        Save();
    }

    public string PlainKey(AccountConfig acc)
    {
        if (string.IsNullOrEmpty(acc.ApiKeyProtected)) return "";
        return KeyProtector.Unprotect(acc.ApiKeyProtected, acc.Id);
    }

    private static string Protect(string plain, Guid id) => KeyProtector.Protect(plain, id);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
