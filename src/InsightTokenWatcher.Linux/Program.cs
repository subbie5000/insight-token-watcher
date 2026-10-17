using Avalonia;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Linux;

internal static class Program
{
    private const string MutexName = "insight-token-watcher.single-instance";

    [STAThread]
    public static int Main(string[] args)
    {
        // Headless import – same format as the Windows build
        if (args.Length >= 2 && args[0].Equals("--import", StringComparison.OrdinalIgnoreCase))
            return ConfigImporter.Run(args[1]);

        // Offscreen render of the popup with demo data (visual checks)
        if (args.Length >= 1 && args[0].Equals("--render", StringComparison.OrdinalIgnoreCase))
            return RenderHarness.Run(args[1..]);

        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created) return 0; // already running

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
