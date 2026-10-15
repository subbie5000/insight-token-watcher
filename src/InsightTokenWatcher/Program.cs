namespace InsightTokenWatcher;

internal static class Program
{
    private const string MutexName = @"Local\InsightTokenWatcher-6F2A9C1E-single-instance";

    /// <summary>Signalled by a second start of the exe: the running instance opens its overview.</summary>
    public const string ShowEventName = @"Local\InsightTokenWatcher-6F2A9C1E-show";

    private static Mutex? _mutex;

    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode: import configuration and exit (no tray, no GUI).
        // InsightTokenWatcher.exe --import accounts.json
        if (args.Length >= 2 && args[0].Equals("--import", StringComparison.OrdinalIgnoreCase))
            return ConfigImporter.Run(args[1]);

        // Offscreen rendering of the popup with demo data (visual checks).
        // InsightTokenWatcher.exe --render <outDir> [--scale 1.5]
        if (args.Length >= 1 && args[0].Equals("--render", StringComparison.OrdinalIgnoreCase))
            return RenderHarness.Run(args[1..]);

        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // Already running (e.g. started again from the Start menu): show its overview instead
            // of a "already running" message box, then leave quietly.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // the other instance is still starting up – nothing to do
            }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var ctx = new TrayAppContext();
        Application.Run(ctx);

        _mutex.ReleaseMutex();
        return 0;
    }
}
