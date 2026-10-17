using Avalonia;
using Avalonia.Controls;

namespace InsightTokenWatcher.Linux;

/// <summary>StatusNotifier tray icon + native menu. On desktops without a tray host the
/// icon simply never appears – the app still runs, which is why the popup also opens once
/// at startup.</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly TrayIcon _tray;

    public event Action? OverviewRequested;
    public event Action? SettingsRequested;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;

    public TrayHost()
    {
        var overview = new NativeMenuItem("Übersicht");
        overview.Click += (_, _) => OverviewRequested?.Invoke();
        var settings = new NativeMenuItem("Einstellungen…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var refresh = new NativeMenuItem("Jetzt messen");
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        var exit = new NativeMenuItem("Beenden");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _tray = new TrayIcon
        {
            Icon = IconFactory.TrayIcon(0, 0, 0, false),
            ToolTipText = "Insight Token Watcher",
            Menu = new NativeMenu { overview, settings, refresh, new NativeMenuItemSeparator(), exit }
        };
        _tray.Clicked += (_, _) => OverviewRequested?.Invoke();
        _tray.IsVisible = true;
    }

    public void UpdateIcon(int crit, int errors, int warn, bool anyData) =>
        _tray.Icon = IconFactory.TrayIcon(crit, errors, warn, anyData);

    public void Dispose()
    {
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
