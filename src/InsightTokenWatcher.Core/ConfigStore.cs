using System.Runtime.InteropServices;
using System.Text.Json;

namespace InsightTokenWatcher.Core;

public sealed class AppSettings
{
    public bool ShowCritBalloons { get; set; } = true;
    public bool Autostart { get; set; }
    public int BurnWindowMinutes { get; set; } = 60;

    /// <summary>Position where the user parked the popup by dragging it (null = near the tray).</summary>
    public int? PopupX { get; set; }
    public int? PopupY { get; set; }

    /// <summary>
    /// Popup order chosen via the column titles (None = provider order). On disk this is never
    /// "Model": versions before 1.9 reject an unknown name and would treat the whole file as
    /// corrupt (start without accounts) after a downgrade. The model column is stored as
    /// <see cref="SortByModel"/>, which older versions simply ignore.
    /// </summary>
    public SortColumn SortColumn { get; set; }
    public bool SortDescending { get; set; }
    public bool SortByModel { get; set; }

    /// <summary>The order to apply (includes the model column).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SortColumn EffectiveSort => SortByModel ? SortColumn.Model : SortColumn;

    public void SetSort(SortColumn column, bool descending)
    {
        SortByModel = column == SortColumn.Model;
        SortColumn = SortByModel ? SortColumn.None : column;
        SortDescending = column != SortColumn.None && descending;
    }
}

public sealed class ConfigFile
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<AccountConfig> Accounts { get; set; } = new();
}

/// <summary>Loads/saves the config file; keys are protected via <see cref="KeyProtector"/>.
/// Windows: %APPDATA%\InsightTokenWatcher\config.json (migrates the pre-1.4 TokenWatch dir).
/// Linux: $XDG_CONFIG_HOME/insight-token-watcher/config.json, chmod 600.</summary>
public sealed class ConfigStore
{
    private readonly string _dir;
    private readonly string _path;
    private ConfigFile _cfg = new();
    private bool _loadFailed;
    private DateTime _seenWriteUtc;
    private long _seenLength = -1;

    public ConfigStore()
    {
        _dir = AppDir;
        MigrateLegacyDir();
        _path = Path.Combine(_dir, "config.json");
        Load();
    }

    /// <summary>Tests can point this at a temp dir before the first ConfigStore is created.</summary>
    public static string? AppDirOverride { get; set; }

    public static string AppDir
    {
        get
        {
            if (AppDirOverride is not null) return AppDirOverride;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "InsightTokenWatcher"
                : "insight-token-watcher";
            return Path.Combine(baseDir, name);
        }
    }

    public AppSettings Settings => _cfg.Settings;
    public IReadOnlyList<AccountConfig> Accounts => _cfg.Accounts;

    // One-time move from the TokenWatch days (Windows only): copy config + history
    // (DPAPI keys are LocalMachine-scoped, so they stay valid at the new path).
    private static void MigrateLegacyDir()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenWatch");
            if (!Directory.Exists(legacy) || File.Exists(Path.Combine(AppDir, "config.json"))) return;
            Directory.CreateDirectory(AppDir);
            foreach (var f in Directory.GetFiles(legacy))
                File.Copy(f, Path.Combine(AppDir, Path.GetFileName(f)), overwrite: false);
        }
        catch
        {
            // best effort – a failed migration just starts with an empty config
        }
    }

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
                RememberFileState();
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
                RememberFileState();
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
        // keep the key file private (no-op on Windows)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
        // a concurrent import replaces the file at the same moment: retry briefly, then report
        IOException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(tmp, _path, true);
                RememberFileState();
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(200 * (attempt + 1));
            }
        }
        throw last!;
    }

    /// <summary>
    /// Changes app settings on top of the latest file content, then saves. A UI tweak (sort
    /// order, popup position, checkboxes) must not overwrite an import that replaced the file
    /// since the last reload – that would silently drop e.g. a freshly rotated token.
    /// </summary>
    public void UpdateSettings(Action<AppSettings> change)
    {
        RefreshIfChangedOnDisk();
        change(_cfg.Settings);
        Save();
    }

    public void UpsertAccount(AccountConfig acc, string? newPlainKey, bool save = true)
    {
        if (save) RefreshIfChangedOnDisk(); // batch callers (the importer) work on a fresh load
        var idx = _cfg.Accounts.FindIndex(a => a.Id == acc.Id);
        if (newPlainKey is not null)
            acc.ApiKeyProtected = KeyProtector.Protect(newPlainKey, acc.Id);
        else if (idx >= 0)
            acc.ApiKeyProtected = _cfg.Accounts[idx].ApiKeyProtected; // key untouched: keep the stored (maybe rotated) one
        if (idx >= 0) _cfg.Accounts[idx] = acc; else _cfg.Accounts.Add(acc);
        if (save) Save();
    }

    public void RemoveAccount(Guid id, bool save = true)
    {
        if (save) RefreshIfChangedOnDisk();
        _cfg.Accounts.RemoveAll(a => a.Id == id);
        if (save) Save();
    }

    private void RememberFileState()
    {
        try
        {
            var fi = new FileInfo(_path);
            _seenWriteUtc = fi.Exists ? fi.LastWriteTimeUtc : default;
            _seenLength = fi.Exists ? fi.Length : -1;
        }
        catch
        {
            // best effort – worst case the next mutation reloads once more
        }
    }

    /// <summary>Another process (an import) replaced the file since we last read or wrote it → reload first.</summary>
    private void RefreshIfChangedOnDisk()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && (fi.LastWriteTimeUtc != _seenWriteUtc || fi.Length != _seenLength))
                TryReload();
        }
        catch
        {
            // keep the in-memory state
        }
    }

    public string PlainKey(AccountConfig acc) =>
        string.IsNullOrEmpty(acc.ApiKeyProtected) ? "" : KeyProtector.Unprotect(acc.ApiKeyProtected, acc.Id);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
