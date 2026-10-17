using Avalonia.Media;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Linux;

/// <summary>Same visual language as the Windows app (v6 dark): identical palette values.</summary>
internal static class Ui
{
    public static readonly Color Bg = Color.FromRgb(17, 19, 23);
    public static readonly Color Card = Color.FromRgb(28, 31, 38);
    public static readonly Color CardBorder = Color.FromRgb(52, 56, 66);
    public static readonly Color RowHover = Color.FromRgb(27, 30, 37);
    public static readonly Color Divider = Color.FromRgb(34, 37, 45);
    public static readonly Color Text = Color.FromRgb(232, 233, 237);
    public static readonly Color Muted = Color.FromRgb(162, 168, 180);
    public static readonly Color Faint = Color.FromRgb(128, 134, 146);
    public static readonly Color Stale = Color.FromRgb(104, 110, 122);
    public static readonly Color Track = Color.FromRgb(40, 44, 53);
    public static readonly Color HoverBorder = Color.FromRgb(88, 94, 110);
    public static readonly Color ButtonHover = Color.FromRgb(38, 42, 51);

    public static readonly Color Ok = Color.FromRgb(168, 225, 12);   // lime (v6)
    public static readonly Color Warn = Color.FromRgb(245, 181, 46);
    public static readonly Color Crit = Color.FromRgb(240, 82, 82);
    public static readonly Color Unknown = Color.FromRgb(110, 118, 130);

    public static readonly IBrush BgBrush = new SolidColorBrush(Bg);
    public static readonly IBrush CardBrush = new SolidColorBrush(Card);
    public static readonly IBrush ButtonHoverBrush = new SolidColorBrush(ButtonHover);
    public static readonly IBrush RowHoverBrush = new SolidColorBrush(RowHover);
    public static readonly IBrush TextBrush = new SolidColorBrush(Text);
    public static readonly IBrush MutedBrush = new SolidColorBrush(Muted);
    public static readonly IBrush FaintBrush = new SolidColorBrush(Faint);
    public static readonly IBrush StaleBrush = new SolidColorBrush(Stale);
    public static readonly IBrush TrackBrush = new SolidColorBrush(Track);
    public static readonly IBrush OkBrush = new SolidColorBrush(Ok);
    public static readonly IBrush WarnBrush = new SolidColorBrush(Warn);
    public static readonly IBrush CritBrush = new SolidColorBrush(Crit);
    public static readonly IBrush UnknownBrush = new SolidColorBrush(Unknown);

    public static Color ForHealth(Health h) => h switch
    {
        Health.Ok => Ok,
        Health.Warn => Warn,
        Health.Crit => Crit,
        _ => Unknown
    };

    public static IBrush ForHealthBrush(Health h) => h switch
    {
        Health.Ok => OkBrush,
        Health.Warn => WarnBrush,
        Health.Crit => CritBrush,
        _ => UnknownBrush
    };

    public static Color ProviderColor(ProviderKind k) => k switch
    {
        ProviderKind.Anthropic => Color.FromRgb(209, 165, 138),
        ProviderKind.KimiCode or ProviderKind.Moonshot => Color.FromRgb(125, 175, 255),
        ProviderKind.Zai => Color.FromRgb(140, 210, 190),
        ProviderKind.DeepSeek => Color.FromRgb(120, 160, 250),
        ProviderKind.OpenAi => Color.FromRgb(110, 200, 160),
        ProviderKind.OpenRouter => Color.FromRgb(180, 160, 250),
        _ => Muted
    };

    // Fonts
    public const double FTitle = 15.5;
    public const double FName = 13.5;
    public const double FBody = 12.5;
    public const double FSmall = 11.5;
    public const double FTiny = 11;
    public const double FCaps = 10.5;
    public const double FValue = 13.5;
    public const double FIconSmall = 10.5;
}
