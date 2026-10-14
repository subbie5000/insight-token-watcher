namespace TokenWatch;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode: import configuration and exit (no tray, no GUI).
        // TokenWatch.exe --import accounts.json
        if (args.Length >= 2 && args[0].Equals("--import", StringComparison.OrdinalIgnoreCase))
            return ConfigImporter.Run(args[1]);

        _mutex = new Mutex(true, @"Local\TokenWatch-6F2A9C1E-single-instance", out var created);
        if (!created)
        {
            MessageBox.Show("TokenWatch läuft bereits (System-Tray).", "TokenWatch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
