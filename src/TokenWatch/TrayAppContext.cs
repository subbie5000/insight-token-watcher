using TokenWatch.Core;

namespace TokenWatch;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly ConfigStore _store = new();
    private readonly PollingEngine _engine = new();
    private readonly NotifyIcon _tray;
    private readonly PopupForm _popup;
    private readonly SynchronizationContext _ui;
    private SettingsForm? _settings;
    private FileSystemWatcher? _configWatcher;
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 1200 };
    private readonly System.Windows.Forms.Timer _iconTimer = new() { Interval = 5000 };
    private IntPtr _iconHandle = IntPtr.Zero;
    private string _iconKey = "";
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private int _configReloads;
    private DateTime _lastStatusWriteUtc = DateTime.MinValue;

    public TrayAppContext()
    {
        _popup = new PopupForm();
        // Creating the first control installs the WinForms SynchronizationContext on this thread.
        // Marshalling through it works even before any window handle exists (the old
        // popup.BeginInvoke silently dropped balloons and updates until the first hover).
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _popup.Canvas.RefreshAllClicked += () => _engine.ForceRefresh();
        _popup.Canvas.RowClicked += id => _engine.ForceRefresh(id);
        _popup.Canvas.SettingsClicked += () =>
        {
            _popup.HidePopup();
            ShowSettings();
        };
        _popup.Canvas.PinClicked += () =>
        {
            _popup.Canvas.Pinned = !_popup.Canvas.Pinned;
            _popup.Canvas.Invalidate();
        };

        _tray = new NotifyIcon
        {
            Text = "", // the hover popup replaces the native tooltip (which would overlap it)
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.MouseMove += (_, _) => OnTrayHover();
        _tray.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _popup.HidePopup();
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePinned();
        };
        _tray.BalloonTipClicked += (_, _) => ShowPinned();

        _engine.Updated += _ => _ui.Post(_ => OnEngineChanged(), null);
        _engine.CritEntered += (view, msg) => _ui.Post(_ => ShowCritBalloon(view, msg), null);

        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            if (!_store.TryReload()) return;
            _configReloads++;
            ReloadAccounts();
            WriteStatus(force: true);
        };
        _iconTimer.Tick += (_, _) =>
        {
            UpdateTrayVisual();
            WriteStatus();
        };
        WatchConfig();

        // a second start of the exe signals this event → open the overview
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _ui.Post(_ => ShowPinned(), null), null, Timeout.Infinite, false);

        UpdateTrayVisual();
        ReloadAccounts();
        _engine.Start();
        _iconTimer.Start();
    }

    public ConfigStore Store => _store;
    public PollingEngine Engine => _engine;

    /// <summary>
    /// Picks up config.json changes from outside (e.g. <c>TokenWatch.exe --import</c> from a
    /// token-sync job). The importer replaces the file atomically (write tmp + rename), which
    /// raises Renamed/Created – not Changed – so all three are watched.
    /// </summary>
    private void WatchConfig()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenWatch");
            Directory.CreateDirectory(dir);
            _configWatcher = new FileSystemWatcher(dir, "config.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            void Kick() => _ui.Post(_ =>
            {
                _reloadDebounce.Stop();
                _reloadDebounce.Start();
            }, null);
            _configWatcher.Changed += (_, _) => Kick();
            _configWatcher.Created += (_, _) => Kick();
            _configWatcher.Renamed += (_, _) => Kick();
        }
        catch
        {
            // optional – the settings dialog reloads anyway
        }
    }

    public void ReloadAccounts()
    {
        var accs = _store.Accounts
            .Where(a => a.Enabled)
            .Select(a => (a, _store.PlainKey(a)))
            .ToList();
        _engine.Reconfigure(accs);
        OnEngineChanged();
    }

    public void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(this);
        _settings.FormClosed += (_, _) => ReloadAccounts();
        _settings.Show();
    }

    public void RefreshNow() => _engine.ForceRefresh();

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Übersicht anheften", null, (_, _) => ShowPinned());
        menu.Items.Add("Jetzt aktualisieren", null, (_, _) => RefreshNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Einstellungen…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitThread());
        return menu;
    }

    private void OnTrayHover()
    {
        if (_popup.Visible)
        {
            _popup.KeepAlive();
            return;
        }
        _popup.SetData(_engine.GetView());
        _popup.ShowAt(Cursor.Position);
    }

    private void ShowPinned()
    {
        if (!_popup.Visible)
        {
            _popup.SetData(_engine.GetView());
            _popup.ShowAt(Cursor.Position);
        }
        _popup.Canvas.Pinned = true;
        _popup.Canvas.Invalidate();
    }

    private void TogglePinned()
    {
        if (_popup.Visible && _popup.Canvas.Pinned)
            _popup.HidePopup();
        else
            ShowPinned();
    }

    private void OnEngineChanged()
    {
        if (_popup.Visible) _popup.SetData(_engine.GetView());
        UpdateTrayVisual();
    }

    private void ShowCritBalloon(AccountView view, string message)
    {
        if (!_store.Settings.ShowCritBalloons) return;
        _tray.BalloonTipTitle = $"TokenWatch – {view.Config.Name}";
        _tray.BalloonTipText = message.Length > 250 ? message[..250] : message;
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(8000);
    }

    private void UpdateTrayVisual()
    {
        var views = _engine.GetView();
        int crit = 0, warn = 0;
        var anyData = false;
        foreach (var v in views)
        {
            if (v.Display is not null) anyData = true;
            if (v.Health == Health.Crit) crit++;
            else if (v.Health == Health.Warn && !v.IsStale) warn++;
        }
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var key = $"{crit}|{warn}|{anyData}|{size}";
        if (key == _iconKey) return;

        var icon = IconFactory.Create(crit, warn, anyData, size, out var handle);
        _tray.Icon = icon;
        IconFactory.Destroy(_iconHandle); // the shell keeps its own copy
        _iconHandle = handle;
        _iconKey = key;
    }

    /// <summary>
    /// Diagnostics for remote checks: %APPDATA%\TokenWatch\status.json with account names and
    /// states (never keys). Written after config reloads and at most once a minute.
    /// </summary>
    private void WriteStatus(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastStatusWriteUtc).TotalSeconds < 60) return;
        _lastStatusWriteUtc = DateTime.UtcNow;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenWatch");
            var path = Path.Combine(dir, "status.json");
            var status = new
            {
                pid = Environment.ProcessId,
                version = Application.ProductVersion,
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
            var json = System.Text.Json.JsonSerializer.Serialize(status,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        catch
        {
            // diagnostics only
        }
    }

    protected override void ExitThreadCore()
    {
        _showWait.Unregister(null);
        _showEvent.Dispose();
        _iconTimer.Stop();
        _reloadDebounce.Stop();
        _configWatcher?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.HidePopup();
        _popup.Dispose();
        _engine.Dispose();
        IconFactory.Destroy(_iconHandle);
        base.ExitThreadCore();
    }
}
