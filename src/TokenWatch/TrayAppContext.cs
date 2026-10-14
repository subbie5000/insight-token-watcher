using TokenWatch.Core;

namespace TokenWatch;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly ConfigStore _store = new();
    private readonly PollingEngine _engine = new();
    private readonly NotifyIcon _tray;
    private readonly PopupForm _popup;
    private SettingsForm? _settings;
    private readonly System.Windows.Forms.Timer _tooltipTimer = new() { Interval = 5000 };
    private FileSystemWatcher? _configWatcher;
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 1500 };

    public TrayAppContext()
    {
        _popup = new PopupForm(this);

        _tray = new NotifyIcon
        {
            Text = "TokenWatch",
            Icon = IconFactory.Get(Health.Unknown, null),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.MouseMove += (_, _) => ShowPopup();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };
        _tray.BalloonTipClicked += (_, _) => ShowPopup();

        _engine.Updated += OnEngineUpdated;
        _engine.CritEntered += OnCritEntered;
        _tooltipTimer.Tick += (_, _) => UpdateTrayVisual();

        // Apply external config.json changes live (e.g. TokenWatch.exe --import from a sync job)
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadAccounts(); };
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenWatch");
            Directory.CreateDirectory(dir);
            _configWatcher = new FileSystemWatcher(dir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += (_, _) =>
            {
                _reloadDebounce.Stop();
                _reloadDebounce.Start();
            };
        }
        catch { /* Watcher ist optional – Einstellungen-Dialog lädt ohnehin neu */ }

        ReloadAccounts();
        _engine.Start();
        _tooltipTimer.Start();
    }

    public ConfigStore Store => _store;
    public PollingEngine Engine => _engine;

    public void ReloadAccounts()
    {
        var accs = _store.Accounts
            .Where(a => a.Enabled)
            .Select(a => (a, _store.PlainKey(a)))
            .ToList();
        _engine.Reconfigure(accs);
        UpdateTrayVisual();
        _popup.Rebuild();
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
        menu.Items.Add("Übersicht anzeigen", null, (_, _) => ShowPopup());
        menu.Items.Add("Jetzt aktualisieren", null, (_, _) => { RefreshNow(); ShowPopup(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Einstellungen…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitThread());
        return menu;
    }

    public void ShowPopup()
    {
        var views = _engine.GetView();
        _popup.UpdateData(views);
        _popup.ShowNear(Cursor.Position);
    }

    private void TogglePopup()
    {
        if (_popup.Visible) _popup.HidePopup();
        else ShowPopup();
    }

    private void OnEngineUpdated(Guid _)
    {
        try
        {
            _popup.BeginInvoke(() =>
            {
                _popup.UpdateData(_engine.GetView());
                UpdateTrayVisual();
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnCritEntered(AccountView view, string message)
    {
        try
        {
            _popup.BeginInvoke(() =>
            {
                if (!_store.Settings.ShowCritBalloons) return;
                _tray.BalloonTipTitle = $"TokenWatch – {view.Config.Name}";
                _tray.BalloonTipText = message.Length > 250 ? message[..250] : message;
                _tray.BalloonTipIcon = ToolTipIcon.Warning;
                _tray.ShowBalloonTip(8000);
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void UpdateTrayVisual()
    {
        var views = _engine.GetView();
        // views are sorted by severity (Crit first)
        var worst = views.Count == 0 ? Health.Unknown : views[0].Health;
        double? worstFrac = views.Count > 0
            ? views[0].Snapshot?.Windows.FirstOrDefault()?.UsedFraction
            : null;
        _tray.Icon = IconFactory.Get(worst, worstFrac);

        var lines = new List<string> { "TokenWatch" };
        foreach (var v in views.Take(6))
        {
            var status = v.Snapshot?.Ok == false ? "FEHLER" : Theme.HealthLabel(v.Health);
            var main = v.Snapshot?.Windows.FirstOrDefault();
            var val = main is null ? "" :
                main.IsBalance ? $" · {Formatting.Money(main.Remaining, main.Unit)}" : $" · {Formatting.Percent(main.UsedFraction)}";
            lines.Add($"{v.Config.Name}: {status}{val}");
        }
        if (views.Count == 0) lines.Add("Keine Konten – Rechtsklick → Einstellungen");
        var text = string.Join("\n", lines);
        _tray.Text = text.Length > 127 ? text[..124] + "…" : text;
    }

    protected override void ExitThreadCore()
    {
        _tooltipTimer.Stop();
        _configWatcher?.Dispose();
        _tray.Visible = false;
        _popup.HidePopup();
        _popup.Dispose();
        _engine.Dispose();
        base.ExitThreadCore();
    }
}
