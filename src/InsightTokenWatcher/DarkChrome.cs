using System.Runtime.InteropServices;

namespace InsightTokenWatcher;

/// <summary>Dark window chrome for dialogs (Windows 10 20H1+ / 11).</summary>
internal static class DarkChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_CAPTION_COLOR = 35;

    public static void Apply(Form form)
    {
        try
        {
            var on = 1;
            if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                var caption = ColorTranslator.ToWin32(Theme.Bg);
                DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
        }
        catch
        {
            // cosmetic only
        }
    }
}
