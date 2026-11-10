using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using InsightTokenWatcher.Demo;

namespace InsightTokenWatcher;

/// <summary>
/// On-screen check of the glass look, which no offscreen render can show (DWM draws the
/// backdrop):
///   InsightTokenWatcher.exe --glass-probe &lt;outDir&gt; [--scenes shapes,sheet,white,dark,solid]
/// Opens a test backdrop in the corner of the primary screen, puts the popup (demo data) on
/// top without activating it – like the real hover popup –, saves a screenshot of exactly that
/// area per scene and closes again. Needs an unlocked interactive session. Scenes: crisp colour
/// shapes on a dark desktop; the same with a white document behind the lower third; a white
/// window; a dark desktop; "solid" = shapes with the glass switched off.
/// </summary>
internal static class GlassProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, GaRoot = 2;

    /// <summary>
    /// The screenshot must show nothing but the probe's own two windows: every grid point of the
    /// area has to belong to the backdrop or the popup. Anything else (another window on top, a
    /// notification) and no picture is taken – it would contain the user's screen content.
    /// Returns the window classes found in the way (never titles).
    /// </summary>
    private static List<string> ForeignWindows(Rectangle area, IntPtr back, IntPtr popup)
    {
        var found = new List<string>();
        var step = Theme.S(20);
        for (var y = area.Top + 2; y < area.Bottom; y += step)
            for (var x = area.Left + 2; x < area.Right; x += step)
            {
                var root = GetAncestor(WindowFromPoint(new Point(x, y)), GaRoot);
                if (root == back || root == popup) continue;
                var cls = new StringBuilder(128);
                GetClassName(root, cls, cls.Capacity);
                var name = (cls.Length == 0 ? "?" : cls.ToString()) + $"@{x - area.Left},{y - area.Top}";
                if (found.Count < 6) found.Add(name);
            }
        return found;
    }

    public static int Run(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "probe";
        var modes = "shapes";
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == "--scenes") modes = args[i + 1];
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        report.AppendLine($"build={Environment.OSVersion.Version.Build} supported={Glass.Supported}");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            foreach (var name in modes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Glass.Enabled = name != "solid";
                report.AppendLine($"{name}: {Shoot(name, Path.Combine(outDir, $"probe_{name}.png"))}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("FAILED " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        File.WriteAllText(Path.Combine(outDir, "done.txt"), "done");
        return 0;
    }

    private static string Shoot(string scene, string path)
    {
        using var popup = new PopupForm();
        popup.SetData(DemoData.Build(DateTimeOffset.UtcNow, 0));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var margin = Theme.S(64);
        var w = Theme.S(popup.Canvas.LogicalWidth);
        var h = Math.Min(popup.Canvas.DesiredHeight, wa.Height - 2 * margin - Theme.S(24));
        // top left: notifications live in the bottom right corner and would block the screenshot
        var area = new Rectangle(wa.Left + Theme.S(12), wa.Top + Theme.S(12), w + 2 * margin, h + 2 * margin);

        using var back = new ProbeBackdrop { Bounds = area, Scene = scene };
        back.Show();
        SetWindowPos(back.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        for (var i = 0; i < 12; i++) // the backdrop must be on screen before the popup measures it
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
        popup.PreferredLocation = new Point(area.X + margin, area.Y + margin);
        popup.Canvas.Pinned = true; // no auto-hide while the mouse is elsewhere
        popup.ShowAt(new Point(area.X + area.Width / 2, area.Bottom));
        popup.Height = h;

        void Pump(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
        }

        // both on top of everything else, the popup above the backdrop – without taking the focus
        SetWindowPos(back.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetWindowPos(popup.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        Pump(60); // let DWM compose the backdrop

        var foreign = ForeignWindows(area, back.Handle, popup.Handle);
        for (var attempt = 0; attempt < 8 && foreign.Count > 0; attempt++) // e.g. a notification: wait for it to go
        {
            Pump(30);
            foreign = ForeignWindows(area, back.Handle, popup.Handle);
        }
        string shot;
        if (foreign.Count > 0)
        {
            shot = "BLOCKED (other windows in the area: " + string.Join(", ", foreign) + ") – no screenshot taken";
        }
        else
        {
            using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            bmp.Save(path, ImageFormat.Png);
            shot = "screenshot ok";
        }
        var alphas = string.Create(CultureInfo.InvariantCulture,
            $"luminance={Glass.BackdropLuminance:0.000} alphas={Glass.PaneAlpha:0.00}/{Glass.CardAlpha:0.00}/{Glass.TintSoftAlpha:0.00}/{Glass.TintStrongAlpha:0.00}/{Glass.TintBlockedAlpha:0.00}");
        var info = $"{shot}; {alphas}; {popup.GlassLog} glassActive={popup.Canvas.GlassActive} popup={popup.Bounds} " +
                   $"backdrop={back.Bounds} visible={IsWindowVisible(back.Handle)} paints={back.Paints} " +
                   $"foreground={(GetForegroundWindow() == popup.Handle ? "popup" : "other")}";
        popup.Hide();
        back.Hide();
        Application.DoEvents();
        return info;
    }

    /// <summary>What the popup sits on during the probe: dark desktop, crisp colour shapes that must
    /// blur behind the glass, and a white sheet with text – the hardest case for legibility.</summary>
    private sealed class ProbeBackdrop : Form
    {
        public ProbeBackdrop()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public int Paints { get; private set; }
        public string Scene { get; init; } = "shapes";

        protected override void OnPaint(PaintEventArgs e)
        {
            Paints++;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = ClientRectangle;
            using var caption = new Font("Segoe UI Semibold", Theme.S(13), FontStyle.Regular, GraphicsUnit.Pixel);
            const string title = "Insight Token Watcher · Glas-Test · schließt sich automatisch";
            if (Scene == "white")
            {
                g.Clear(Color.White);
                TextRenderer.DrawText(g, title, caption, new Point(Theme.S(16), Theme.S(14)), Color.Black);
                return;
            }
            using (var bg = new LinearGradientBrush(r, Color.FromArgb(14, 21, 34), Color.FromArgb(26, 18, 51), 60f))
                g.FillRectangle(bg, r);
            TextRenderer.DrawText(g, title, caption, new Point(Theme.S(16), Theme.S(14)), Color.White);
            if (Scene == "dark") return;
            void Disc(float cx, float cy, float radius, Color c)
            {
                using var b = new SolidBrush(c);
                g.FillEllipse(b, r.Width * cx - radius, r.Height * cy - radius, 2 * radius, 2 * radius);
            }
            Disc(0.10f, 0.36f, Theme.S(105), Color.FromArgb(76, 125, 255));
            Disc(0.95f, 0.22f, Theme.S(118), Color.FromArgb(56, 214, 245));
            Disc(0.92f, 0.62f, Theme.S(120), Color.FromArgb(169, 146, 255));
            Disc(0.08f, 0.60f, Theme.S(96), Color.FromArgb(45, 212, 191));

            if (Scene != "sheet") return;

            // a white document behind the lower third
            var sheet = new Rectangle(r.Width / 5, r.Height * 2 / 3, r.Width * 3 / 5, r.Height / 3 - Theme.S(20));
            g.FillRectangle(Brushes.White, sheet);
            using var body = new Font("Segoe UI", Theme.S(13), FontStyle.Regular, GraphicsUnit.Pixel);
            for (var i = 0; i < 8; i++)
                TextRenderer.DrawText(g, "Weißes Dokument hinter dem Popup – der schwierigste Fall für die Lesbarkeit.", body,
                    new Point(sheet.X + Theme.S(14), sheet.Y + Theme.S(12) + i * Theme.S(22)), Color.Black);
        }
    }
}
