using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Media;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Linux;

/// <summary>Owns the config store and the polling engine; mirrors the Windows TrayAppContext:
/// config reload via FileSystemWatcher, tray badge updates, crit notifications via notify-send,
/// status.json for remote checks. No Windows-specific APIs.</summary>
internal sealed class EngineHost : IDisposable
{
    private readonly ConfigStore _store = new();
    private readonly PollingEngine _engine = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly TrayHost? _tray;
    private int _configReloads;
    private DateTime _lastStatusWriteUtc = DateTime.MinValue;
    private bool _notifyAvailable;

    public ConfigStore Store => _store;
    public PollingEngine Engine => _engine;
    public event Action<IReadOnlyList<AccountView>>? ViewUpdated;

    public EngineHost(TrayHost? tray)
    {
        _tray = tray;
        ReconfigureAccounts();

        try
        {
            Directory.CreateDirectory(ConfigStore.AppDir);
            _watcher = new FileSystemWatcher(ConfigStore.AppDir, "config.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // no inotify = no live reload; the app still works
        }

        _engine.Updated += _ => Dispatcher.UIThread.Post(() => ViewUpdated?.Invoke(_engine.GetView()));
        _engine.CritEntered += (view, msg) => Dispatcher.UIThread.Post(() => NotifyCrit(view, msg));
        _engine.Start();

        _tick.Tick += (_, _) => UpdateTrayVisual();
        _statusTimer.Tick += (_, _) =>
        {
            WriteStatus();
            _tick.Start();
        };
        _tick.Start();
        _statusTimer.Start();

        // one status write at startup so remote checks see the app alive immediately
        _lastStatusWriteUtc = DateTime.MinValue;
        WriteStatus();

        _notifyAvailable = FindNotifySend() is not null;
    }

    private DispatcherTimer? _reloadDebounce;

    private void ScheduleReload()
    {
        _reloadDebounce ??= CreateDebounce();
        _reloadDebounce.Stop();
        _reloadDebounce.Start();
    }

    private DispatcherTimer CreateDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_store.TryReload()) return;
            _configReloads++;
            ReconfigureAccounts();
            WriteStatus(force: true);
        };
        return t;
    }

    public void ReconfigureAccounts()
    {
        _engine.Reconfigure(_store.Accounts.Select(a => (a, _store.PlainKey(a))).ToList());
    }

    public void ForceRefresh(Guid? id = null) => _engine.ForceRefresh(id);

    private void NotifyCrit(AccountView view, string msg)
    {
        if (!_store.Settings.ShowCritBalloons || !_notifyAvailable) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList =
                {
                    "-a", "Insight Token Watcher",
                    "Insight Token Watcher – " + view.Config.Name,
                    msg.Length > 250 ? msg[..250] : msg
                },
                CreateNoWindow = true
            });
        }
        catch
        {
            _notifyAvailable = false;
        }
    }

    private static string? FindNotifySend()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "notify-send");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void UpdateTrayVisual()
    {
        var views = _engine.GetView();
        int crit = 0, errors = 0, warn = 0;
        var anyData = false;
        foreach (var v in views)
        {
            if (v.Snapshot is null) continue;
            if (v.Snapshot.Ok) anyData = true;
            switch (v.Health)
            {
                case Health.Crit when v.Snapshot.Ok: crit++; break;
                case Health.Crit: errors++; break;
                case Health.Warn: warn++; break;
            }
        }
        _tray?.UpdateIcon(crit, errors, warn, anyData);
    }

    private void WriteStatus(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastStatusWriteUtc).TotalSeconds < 60) return;
        _lastStatusWriteUtc = DateTime.UtcNow;
        try
        {
            var path = Path.Combine(ConfigStore.AppDir, "status.json");
            var status = new
            {
                pid = Environment.ProcessId,
                version = typeof(EngineHost).Assembly.GetName().Version?.ToString(),
                writtenAt = DateTimeOffset.Now,
                configReloads = _configReloads,
                accounts = _engine.GetView().Select(v => new
                {
                    name = v.Config.Name,
                    provider = v.Config.Provider.ToString(),
                    health = v.Health.ToString(),
                    stale = v.IsStale,
                    failure = v.Failure.ToString(),
                    lastGood = v.LastGood?.AtUtc.ToLocalTime(),
                    windows = v.Display?.Windows.Count ?? 0
                })
            };
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            File.Move(tmp, path, true);
        }
        catch
        {
            // diagnostics only
        }
    }

    public void Dispose()
    {
        _tick.Stop();
        _statusTimer.Stop();
        _watcher?.Dispose();
        _engine.Dispose();
    }
}

/// <summary>Autostart via XDG: ~/.config/autostart/insight-token-watcher.desktop.</summary>
internal static class Autostart
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
    private static string FilePath => Path.Combine(Dir, "insight-token-watcher.desktop");

    public static bool IsEnabled() => File.Exists(FilePath);

    public static void Set(bool on)
    {
        if (on)
        {
            Directory.CreateDirectory(Dir);
            var exe = Environment.ProcessPath ?? "insight-token-watcher";
            File.WriteAllText(FilePath,
                "[Desktop Entry]\nType=Application\nName=Insight Token Watcher\n" +
                $"Exec=\"{exe}\"\nX-GNOME-Autostart-enabled=true\n");
        }
        else
        {
            try { File.Delete(FilePath); } catch { /* best effort */ }
        }
    }
}
