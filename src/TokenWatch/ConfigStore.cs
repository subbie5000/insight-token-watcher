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
    private bool _loadFailed;

    public ConfigStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenWatch");
        _path = Path.Combine(_dir, "config.json");
        Load();
    }

    public AppSettings Settings => _cfg.Settings;
    public IReadOnlyList<AccountConfig> Accounts => _cfg.Accounts;

    /// <summary>
    /// Initial load. Only a parse error counts as corruption (backup + start empty). A
    /// transient I/O error (file being replaced by an import) is retried; if it persists, the
    /// store starts empty but refuses to save, so the real file is never overwritten.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path)) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path);
                _cfg = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions()) ?? new ConfigFile();
                _loadFailed = false;
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (JsonException)
            {
                var backup = _path + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(_path, backup, false); } catch { /* best effort */ }
                _cfg = new ConfigFile();
                return;
            }
        }
        _cfg = new ConfigFile();
        _loadFailed = true;
    }

    /// <summary>Re-reads the file (after an external change). Keeps the current state on failure.</summary>
    public bool TryReload()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(_path)) return false;
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions());
                if (cfg is null) return false;
                _cfg = cfg;
                _loadFailed = false;
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (JsonException)
            {
                return false;
            }
        }
        return false;
    }

    public void Save()
    {
        if (_loadFailed && !TryReload())
            throw new InvalidOperationException(
                "Die Konfiguration konnte nicht gelesen werden – Speichern blockiert, um Datenverlust zu vermeiden.");
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(_cfg, JsonOptions());
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, true);
    }

    public void UpsertAccount(AccountConfig acc, string? newPlainKey, bool save = true)
    {
        if (newPlainKey is not null)
            acc.ApiKeyProtected = KeyProtector.Protect(newPlainKey, acc.Id);
        var idx = _cfg.Accounts.FindIndex(a => a.Id == acc.Id);
        if (idx >= 0) _cfg.Accounts[idx] = acc; else _cfg.Accounts.Add(acc);
        if (save) Save();
    }

    public void RemoveAccount(Guid id, bool save = true)
    {
        _cfg.Accounts.RemoveAll(a => a.Id == id);
        if (save) Save();
    }

    public string PlainKey(AccountConfig acc) =>
        string.IsNullOrEmpty(acc.ApiKeyProtected) ? "" : KeyProtector.Unprotect(acc.ApiKeyProtected, acc.Id);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
