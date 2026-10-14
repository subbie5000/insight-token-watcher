using TokenWatch.Core;

namespace TokenWatch;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(18, 20, 24);
    public static readonly Color Card = Color.FromArgb(28, 31, 38);
    public static readonly Color CardBorder = Color.FromArgb(52, 56, 66);
    public static readonly Color Text = Color.FromArgb(230, 231, 235);
    public static readonly Color Muted = Color.FromArgb(148, 154, 166);
    public static readonly Color Track = Color.FromArgb(44, 48, 58);

    public static readonly Color Ok = Color.FromArgb(168, 225, 12);   // Lime (v6)
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

    public static readonly Font TitleFont = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font NameFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font BodyFont = new("Segoe UI", 9f);
    public static readonly Font SmallFont = new("Segoe UI", 8.25f);
    public static readonly Font NumFont = new("Consolas", 9f);

    public static Color ProviderColor(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => Color.FromArgb(217, 119, 87),   // Claude orange
        ProviderKind.KimiCode => Color.FromArgb(86, 156, 255),
        ProviderKind.Moonshot => Color.FromArgb(86, 156, 255),
        ProviderKind.Zai => Color.FromArgb(148, 103, 255),        // violet
        ProviderKind.DeepSeek => Color.FromArgb(72, 199, 176),    // teal
        ProviderKind.OpenAi => Color.FromArgb(116, 185, 165),     // OpenAI green-teal
        ProviderKind.OpenRouter => Color.FromArgb(230, 126, 80),  // OpenRouter coral
        ProviderKind.GenericOpenAi => Color.FromArgb(150, 150, 160),
        _ => Muted
    };
}
