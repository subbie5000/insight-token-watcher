using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace InsightTokenWatcher.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new TrayHost();
            var engine = new EngineHost(host);
            var popup = new PopupWindow();
            var settings = new SettingsWindow(engine);

            if (engine.Store.Settings.PopupX is { } px && engine.Store.Settings.PopupY is { } py)
                popup.PreferredLocation = new PixelPoint(px, py);

            engine.ViewUpdated += views =>
                Dispatcher.UIThread.Post(() => { if (popup.IsVisible) popup.SetData(views); });

            void TogglePopup()
            {
                if (popup.IsVisible) popup.HidePopup();
                else
                {
                    popup.SetData(engine.Engine.GetView());
                    popup.ShowPopup(pinned: false);
                }
            }

            host.OverviewRequested += TogglePopup;
            host.SettingsRequested += () => Dispatcher.UIThread.Post(() => settings.Show());
            host.RefreshRequested += () => engine.ForceRefresh();
            host.ExitRequested += () =>
            {
                popup.HidePopup();
                settings.Close();
                host.Dispose();
                engine.Dispose();
                desktop.Shutdown();
            };

            popup.Canvas.PinClicked += () =>
            {
                popup.Canvas.Pinned = !popup.Canvas.Pinned;
                popup.Canvas.InvalidateVisual();
            };
            popup.Canvas.PopupMoved += loc =>
            {
                popup.PreferredLocation = loc;
                engine.Store.Settings.PopupX = loc.X;
                engine.Store.Settings.PopupY = loc.Y;
                engine.Store.Save();
            };
            popup.Canvas.RefreshAllClicked += () => engine.ForceRefresh();
            popup.Canvas.RowClicked += id => engine.ForceRefresh(id);
            popup.Canvas.SettingsClicked += () =>
            {
                popup.HidePopup();
                Dispatcher.UIThread.Post(() => settings.Show());
            };

            // Linux has no hover-on-tray: show the popup once at startup so the app is
            // discoverable even on desktops without a StatusNotifier host.
            popup.SetData(engine.Engine.GetView());
            popup.ShowPopup(pinned: false);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
