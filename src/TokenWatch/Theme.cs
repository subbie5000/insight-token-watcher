using System.Drawing.Text;
using TokenWatch.Core;

namespace TokenWatch;

internal static class Theme
{
    // ---- palette (dark, v6 lime accent) ----
    public static readonly Color Bg = Color.FromArgb(17, 19, 23);
    public static readonly Color Card = Color.FromArgb(28, 31, 38);
    public static readonly Color CardBorder = Color.FromArgb(52, 56, 66);
    public static readonly Color RowHover = Color.FromArgb(27, 30, 37);
    public static readonly Color Divider = Color.FromArgb(34, 37, 45);
    public static readonly Color Text = Color.FromArgb(232, 233, 237);
    public static readonly Color Muted = Color.FromArgb(162, 168, 180);  // ≈ 7.5:1 on Bg
    public static readonly Color Faint = Color.FromArgb(128, 134, 146);  // ≈ 5:1 on Bg (WCAG AA for small text)
    public static readonly Color Stale = Color.FromArgb(104, 110, 122);
    public static readonly Color Track = Color.FromArgb(40, 44, 53);
    public static readonly Color HoverBorder = Color.FromArgb(88, 94, 110);
    public static readonly Color ButtonHover = Color.FromArgb(38, 42, 51);

    public static readonly Color Ok = Color.FromArgb(168, 225, 12);   // lime (v6)
    public static readonly Color Warn = Color.FromArgb(245, 181, 46);
    public static readonly Color Crit = Color.FromArgb(240, 82, 82);
    public static readonly Color Unknown = Color.FromArgb(110, 118, 130);

    public static Color ForHealth(Health h) => h switch
    {
        Health.Ok => Ok,
        Health.Warn => Warn,
        Health.Crit => Crit,
        _ => Unknown
    };

    public static string HealthLabel(Health h) => h switch
    {
        Health.Ok => "OK",
        Health.Warn => "KNAPP",
        Health.Crit => "KRITISCH",
        _ => "–"
    };

    public static Color ProviderColor(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => Color.FromArgb(217, 119, 87),   // Claude orange
        ProviderKind.KimiCode => Color.FromArgb(86, 156, 255),
        ProviderKind.Moonshot => Color.FromArgb(86, 156, 255),
        ProviderKind.Zai => Color.FromArgb(148, 103, 255),        // violet
        ProviderKind.DeepSeek => Color.FromArgb(72, 199, 176),    // teal
        ProviderKind.OpenAi => Color.FromArgb(116, 185, 165),     // green-teal
        ProviderKind.OpenRouter => Color.FromArgb(230, 126, 80),  // coral
        ProviderKind.GenericOpenAi => Color.FromArgb(150, 150, 160),
        _ => Muted
    };

    public static string ProviderShort(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => "Anthropic",
        ProviderKind.KimiCode => "Kimi Code",
        ProviderKind.Moonshot => "Moonshot",
        ProviderKind.Zai => "GLM",
        ProviderKind.DeepSeek => "DeepSeek",
        ProviderKind.OpenAi => "OpenAI",
        ProviderKind.OpenRouter => "OpenRouter",
        ProviderKind.GenericOpenAi => "OpenAI-kompatibel",
        _ => k.ToString()
    };

    // ---- DPI ----
    private static float? _dpiScale;

    /// <summary>System DPI factor (96 dpi = 1.0). Can be forced for offscreen rendering.</summary>
    public static float DpiScale
    {
        get
        {
            if (_dpiScale is null)
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                _dpiScale = Math.Max(1f, g.DpiX / 96f);
            }
            return _dpiScale.Value;
        }
    }

    public static void ForceScale(float scale) => _dpiScale = scale;

    public static int S(float logicalPx) => (int)Math.Round(logicalPx * DpiScale);

    // ---- popup fonts (pixel units, scaled manually → identical on every DPI) ----
    private static readonly Dictionary<string, Font> PxFonts = new();

    private static Font Px(float logicalPx, bool semibold = false)
    {
        var key = $"{logicalPx}|{semibold}|{DpiScale}";
        lock (PxFonts)
        {
            if (!PxFonts.TryGetValue(key, out var f))
                PxFonts[key] = f = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI",
                    logicalPx * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }
    }

    public static Font FTitle => Px(16, true);
    public static Font FName => Px(13.5f, true);
    public static Font FBody => Px(12.5f);
    public static Font FSmall => Px(11.5f);
    public static Font FTiny => Px(11f);
    public static Font FCaps => Px(10.5f, true);
    public static Font FValue => Px(13.5f, true);
    public static Font FBig => Px(17f, true);

    private static string? _iconFamily;

    /// <summary>"Segoe Fluent Icons" (Win 11) or "Segoe MDL2 Assets" (Win 10); null if neither exists.</summary>
    public static string? IconFamily
    {
        get
        {
            if (_iconFamily is null)
            {
                using var fc = new InstalledFontCollection();
                var names = fc.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _iconFamily = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                    : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets"
                    : "";
            }
            return _iconFamily.Length == 0 ? null : _iconFamily;
        }
    }

    private static Font? _fIcon, _fIconSmall;
    public static Font FIcon => _fIcon ??= IconFamily is { } fam
        ? new Font(fam, 14f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        : Px(15);
    public static Font FIconSmall => _fIconSmall ??= IconFamily is { } fam
        ? new Font(fam, 10.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        : Px(10.5f);

    public const string GlyphRefresh = "\uE72C";
    public const string GlyphSettings = "\uE713";
    public const string GlyphPin = "\uE718";
    public const string GlyphPinned = "\uE840";
    public const string GlyphClock = "\uE823";   // "Recent": clock with arrow = time until reset
    public const string GlyphWarning = "\uE7BA";

    // ---- dialog fonts (points; dialogs use AutoScaleMode.Dpi) ----
    public static readonly Font TitleFont = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font NameFont = new("Segoe UI Semibold", 9.5f);
    public static readonly Font BodyFont = new("Segoe UI", 9f);
    public static readonly Font SmallFont = new("Segoe UI", 8.25f);
}
