using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace InsightTokenWatcher;

/// <summary>
/// "Glass": the popup as a translucent pane over the desktop. DWM blurs what lies behind the
/// window; <see cref="GlassSurface"/> gives the popup's dark surfaces an alpha below 1 so the
/// blur shows through, while text, bars and icons stay opaque.
///
/// The blur comes from the accent policy (SetWindowCompositionAttribute). It is undocumented,
/// but it is the only backdrop that works for a window that never gets activated: the
/// documented system backdrops (DWMWA_SYSTEMBACKDROP_TYPE, acrylic/mica) fall back to a solid
/// colour while the window is inactive – and a hover popup always is (measured on build 26200).
/// </summary>
internal static class Glass
{
    /// <summary>Switched on by the app (setting and <see cref="Supported"/>) before the popup is created.</summary>
    public static bool Enabled { get; set; }

    // Opacity of the surfaces over the blurred backdrop. The pane shows the most; cards are
    // more solid because they carry the small text, tinted cards the most (red and amber text).
    // The values follow the brightness behind the popup (Adapt): over a dark desktop the glass
    // is clearly translucent, over a white window it closes up so far that text keeps 4.5:1.
    public static float PaneAlpha { get; private set; }
    public static float CardAlpha { get; private set; }
    public static float TintSoftAlpha { get; private set; }
    public static float TintStrongAlpha { get; private set; }
    public static float TintBlockedAlpha { get; private set; }

    /// <summary>Relative luminance (0..1) the current opacities were computed for.</summary>
    public static double BackdropLuminance { get; private set; } = -1;

    static Glass() => Adapt(1); // until measured: assume the worst case, a white window

    private const double MinContrast = 4.6;
    private const float PaneMin = 0.52f, CardMin = 0.66f, TintSoftMin = 0.78f, TintStrongMin = 0.82f, TintBlockedMin = 0.82f;

    /// <summary>
    /// Sets the opacities for a backdrop of the given luminance: for each surface the lowest
    /// opacity at which its weakest text colour still has <see cref="MinContrast"/> – but never
    /// below a minimum, so the surfaces stay recognisable. Returns false when nothing changed
    /// noticeably (no repaint needed).
    /// </summary>
    public static bool Adapt(double luminance)
    {
        luminance = Math.Clamp(luminance, 0, 1);
        var grey = ToLevel(luminance);
        var strongTint = Over(30, Theme.Warn, Theme.Card);
        var softTint = Over(20, Theme.Warn, Theme.Card);
        var blockedTint = Over(40, Theme.Crit, Theme.Card);
        var pane = Solve(Theme.Bg, grey, PaneMin, GlassText.Faint);
        var card = Solve(Theme.Card, grey, CardMin, GlassText.Faint, GlassText.Crit);
        var soft = Math.Max(card, Solve(softTint, grey, TintSoftMin, GlassText.Crit));
        var strong = Math.Max(soft, Solve(strongTint, grey, TintStrongMin, GlassText.Crit));
        var blocked = Math.Max(card, Solve(blockedTint, grey, TintBlockedMin, GlassText.Crit));
        var changed = Math.Abs(pane - PaneAlpha) > 0.015f || Math.Abs(card - CardAlpha) > 0.015f ||
                      Math.Abs(soft - TintSoftAlpha) > 0.015f || Math.Abs(strong - TintStrongAlpha) > 0.015f ||
                      Math.Abs(blocked - TintBlockedAlpha) > 0.015f;
        if (!changed && BackdropLuminance >= 0) return false;
        (PaneAlpha, CardAlpha, TintSoftAlpha, TintStrongAlpha, TintBlockedAlpha) = (pane, card, soft, strong, blocked);
        BackdropLuminance = luminance;
        return true;
    }

    private static float Solve(Color surface, double grey, float min, params Color[] texts)
    {
        bool Enough(double alpha)
        {
            var under = Luminance(surface.R * alpha + grey * (1 - alpha), surface.G * alpha + grey * (1 - alpha),
                surface.B * alpha + grey * (1 - alpha));
            return texts.All(t => (Luminance(t.R, t.G, t.B) + 0.05) / (under + 0.05) >= MinContrast);
        }
        if (Enough(min)) return min;
        double lo = min, hi = 1;
        for (var i = 0; i < 14; i++)
        {
            var mid = (lo + hi) / 2;
            if (Enough(mid)) hi = mid; else lo = mid;
        }
        return (float)hi;
    }

    private static Color Over(int alpha, Color veil, Color under) => Color.FromArgb(
        under.R + (veil.R - under.R) * alpha / 255, under.G + (veil.G - under.G) * alpha / 255,
        under.B + (veil.B - under.B) * alpha / 255);

    private static double Linear(double v)
    {
        v /= 255;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    internal static double Luminance(double r, double g, double b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    /// <summary>The grey level (0..255) with the given relative luminance.</summary>
    internal static double ToLevel(double luminance) =>
        255 * (luminance <= 0.0031308 ? luminance * 12.92 : 1.055 * Math.Pow(luminance, 1 / 2.4) - 0.055);

    /// <summary>
    /// How bright the screen is in the given areas: the 95th percentile of the luminance, so a
    /// white window counts even if it only covers part of them. The pixels are read into memory
    /// for this one number and discarded. Null when the screen cannot be read (locked, secure
    /// desktop) or no area lies on a screen.
    /// </summary>
    public static double? ScreenLuminance(IEnumerable<Rectangle> areas)
    {
        var values = new List<double>();
        var screen = SystemInformation.VirtualScreen;
        try
        {
            foreach (var area in areas)
            {
                var r = Rectangle.Intersect(area, screen);
                if (r.Width < 2 || r.Height < 2) continue;
                using var bmp = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                var data = bmp.LockBits(new Rectangle(Point.Empty, r.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    var row = new int[r.Width];
                    var step = Math.Max(1, Math.Min(r.Width, r.Height) / 6);
                    for (var y = 0; y < r.Height; y += step)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, r.Width);
                        for (var x = 0; x < r.Width; x += step)
                            values.Add(Luminance((row[x] >> 16) & 0xFF, (row[x] >> 8) & 0xFF, row[x] & 0xFF));
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            return null;
        }
        if (values.Count == 0) return null;
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))];
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);

    /// <summary>
    /// Windows 11 22H2 (build 22621) and later: older builds lag when a window with this blur is
    /// moved. Off when the user disabled transparency effects, in high-contrast mode and in
    /// remote sessions – there the popup keeps its opaque look.
    /// </summary>
    public static bool Supported => _supported ??= Detect();
    private static bool? _supported;

    private static bool Detect()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22621) return false;
            if (SystemInformation.HighContrast || SystemInformation.TerminalServerSession) return false;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Requests the blurred backdrop for a window. True when both calls succeeded.</summary>
    public static bool Apply(IntPtr hwnd, out string log)
    {
        // the whole client area counts as frame: DWM then honours the alpha channel we paint
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var extend = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = 0x01000000 // AABBGGRR: no tint of its own – ours comes from the surfaces
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        int ok;
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new CompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            ok = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        log = $"extend=0x{extend:X} accent={ok}";
        return extend == 0 && ok != 0;
    }
}

/// <summary>Popup text colours on glass (see <see cref="Theme.GlassPalette"/>).</summary>
internal static class GlassText
{
    public static readonly Color Muted = Color.FromArgb(198, 204, 214);
    public static readonly Color Faint = Color.FromArgb(179, 186, 198);
    public static readonly Color Stale = Color.FromArgb(160, 167, 180);
    public static readonly Color Warn = Color.FromArgb(255, 189, 82);
    public static readonly Color Crit = Color.FromArgb(255, 145, 139);
}

/// <summary>
/// Frame buffer for the glass look: a 32-bit DIB the popup paints into with its normal (opaque)
/// drawing code. <see cref="Present"/> then derives the alpha channel from each pixel's colour –
/// pixels in a surface colour (pane, card, tinted card, bar track) become translucent with that
/// surface's opacity, everything else (text, bars, icons) stays opaque – and copies the buffer
/// to the window including that alpha. Text keeps its ClearType rendering, because it is drawn
/// onto an opaque surface first.
/// </summary>
internal sealed class GlassSurface : IDisposable
{
    // Colour distance to the nearest surface: up to Near a pixel is that surface; from Far on it
    // is content. In between (anti-aliased edges of text and shapes) the alpha ramps – no seams.
    private const double Near = 10, Far = 48;
    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Colors;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private IntPtr _dc, _bitmap, _previous, _bits;
    private Size _size;
    private int[] _pixels = Array.Empty<int>();
    private readonly byte[] _alpha;   // 32×32×32 colours → alpha

    /// <param name="ramps">Surface colours with their opacity, as lines through colour space:
    /// each ramp lists colours that blend into each other on screen (pane → card → hovered card;
    /// pane → tinted card). A pixel takes the opacity of the nearest point on a ramp.</param>
    public GlassSurface(IReadOnlyList<IReadOnlyList<(Color color, float alpha)>> ramps)
    {
        _alpha = BuildTable(ramps);
    }

    /// <summary>
    /// Alpha per colour. The surfaces are not isolated points: card corners are anti-aliased
    /// against the pane, shadows darken towards black, and the neutral greys (pane, card, hover,
    /// bar track) all lie on one line. So opacity is interpolated ALONG each ramp; single points
    /// or lines from the pane to every surface made neighbours steal each other's opacity
    /// (measured: pane 0.92 instead of 0.80, card 0.82 instead of 0.87).
    /// </summary>
    internal static byte[] BuildTable(IReadOnlyList<IReadOnlyList<(Color color, float alpha)>> ramps)
    {
        var segments = new List<(double[] a, double[] b, double alphaA, double alphaB)>();
        static double[] V(Color c) => new double[] { c.R, c.G, c.B };
        foreach (var ramp in ramps)
            for (var i = 1; i < ramp.Count; i++)
                segments.Add((V(ramp[i - 1].color), V(ramp[i].color), ramp[i - 1].alpha, ramp[i].alpha));

        var table = new byte[32 * 32 * 32];
        for (var r = 0; r < 32; r++)
            for (var g = 0; g < 32; g++)
                for (var b = 0; b < 32; b++)
                {
                    double[] c = { r * 8 + 4, g * 8 + 4, b * 8 + 4 };
                    double best = double.MaxValue, bestAlpha = 1;
                    foreach (var (p, q, alphaP, alphaQ) in segments)
                    {
                        double dx = q[0] - p[0], dy = q[1] - p[1], dz = q[2] - p[2];
                        var len2 = dx * dx + dy * dy + dz * dz;
                        var t = len2 <= 0 ? 0 : Math.Clamp(((c[0] - p[0]) * dx + (c[1] - p[1]) * dy + (c[2] - p[2]) * dz) / len2, 0, 1);
                        double ex = c[0] - (p[0] + t * dx), ey = c[1] - (p[1] + t * dy), ez = c[2] - (p[2] + t * dz);
                        var d = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                        if (d >= best) continue;
                        best = d;
                        bestAlpha = alphaP + (alphaQ - alphaP) * t;
                    }
                    var ramp = Math.Clamp((best - Near) / (Far - Near), 0, 1);
                    table[(r << 10) | (g << 5) | b] = (byte)Math.Round(255 * (bestAlpha + (1 - bestAlpha) * ramp));
                }
        return table;
    }

    /// <summary>Opacity (0..255) the table assigns to a colour (render harness).</summary>
    public int AlphaOf(Color c) => _alpha[((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3)];

    /// <summary>Graphics over the buffer (dispose it before <see cref="Present"/>).</summary>
    public Graphics Begin(Size size)
    {
        if (size != _size || _dc == IntPtr.Zero)
        {
            Release();
            var info = new BitmapInfo
            {
                Size = 40, Width = size.Width, Height = -size.Height, // negative = top-down
                Planes = 1, BitCount = 32
            };
            _dc = CreateCompatibleDC(IntPtr.Zero);
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_dc == IntPtr.Zero || _bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Glass buffer could not be created.");
            _previous = SelectObject(_dc, _bitmap);
            _size = size;
            _pixels = new int[size.Width * size.Height];
        }
        return Graphics.FromHdc(_dc);
    }

    /// <summary>Applies the alpha channel and copies the frame to <paramref name="target"/>.</summary>
    public void Present(IntPtr target)
    {
        ApplyAlpha();
        BitBlt(target, 0, 0, _size.Width, _size.Height, _dc, 0, 0, SRCCOPY);
    }

    /// <summary>The finished frame as premultiplied ARGB pixels (render harness).</summary>
    public int[] Frame()
    {
        ApplyAlpha();
        return _pixels;
    }

    private void ApplyAlpha()
    {
        GdiFlush();
        var n = _pixels.Length;
        Marshal.Copy(_bits, _pixels, 0, n);
        var table = _alpha;
        var px = _pixels;
        for (var i = 0; i < n; i++)
        {
            var p = px[i];
            int b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
            int a = table[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
            // premultiplied, as DWM expects it
            px[i] = a == 255
                ? p | unchecked((int)0xFF000000)
                : (a << 24) | (r * a / 255 << 16) | (g * a / 255 << 8) | (b * a / 255);
        }
        Marshal.Copy(px, 0, _bits, n);
    }

    private void Release()
    {
        if (_dc == IntPtr.Zero) return;
        if (_previous != IntPtr.Zero) SelectObject(_dc, _previous);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        DeleteDC(_dc);
        _dc = _bitmap = _previous = _bits = IntPtr.Zero;
        _size = Size.Empty;
    }

    public void Dispose() => Release();
}
