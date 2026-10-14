using System.Globalization;
using System.Text.RegularExpressions;

namespace TokenWatch.Core;

public static class TimeParsing
{
    /// <summary>RFC3339 including nanoseconds (.NET only handles 7 fractional digits) or epoch s/ms.</summary>
    public static DateTimeOffset? ParseFlexible(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            return FromEpochGuess(num);

        var m = Regex.Match(s, @"^(?<head>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\.(?<frac>\d+)(?<tz>Z|[+-]\d{2}:?\d{2})?$");
        if (m.Success)
        {
            var frac = m.Groups["frac"].Value;
            if (frac.Length > 7) frac = frac[..7];
            s = m.Groups["head"].Value + "." + frac + (m.Groups["tz"].Success ? m.Groups["tz"].Value : "Z");
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;
        return null;
    }

    public static DateTimeOffset FromEpochGuess(long value)
    {
        // > 10^12 => Millisekunden, sonst Sekunden
        return value > 1_000_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }
}

public static class Formatting
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>
    /// Rounded (not truncated) so that two texts computed seconds apart show the same value:
    /// minutes below a day, hours from one day on.
    /// </summary>
    public static string Span(TimeSpan? ts)
    {
        if (ts is null) return "∞";
        var t = ts.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 30) return "<1 min";
        var minutes = (long)Math.Round(t.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        if (minutes < 24 * 60) return $"{minutes / 60} h {minutes % 60:D2} min";
        var hours = (long)Math.Round(t.TotalHours);
        return $"{hours / 24} d {hours % 24} h";
    }

    public static string Count(double? v)
    {
        if (v is null) return "–";
        var x = v.Value;
        if (Math.Abs(x) >= 1_000_000) return (x / 1_000_000).ToString("0.0", De) + " M";
        if (Math.Abs(x) >= 10_000) return (x / 1000).ToString("0.0", De) + " k";
        return x.ToString("0.##", De);
    }

    /// <summary>German number format with the currency sign after the amount: "23,42 $".</summary>
    public static string Money(double? v, string unit)
    {
        if (v is null) return "–";
        var cur = unit is "USD" or "$" ? "$" : unit is "CNY" or "¥" ? "¥" : unit is "EUR" or "€" ? "€" : unit;
        return v.Value.ToString("N2", De) + " " + cur;
    }

    public static string Percent(double? frac)
    {
        if (frac is null) return "–";
        return (frac.Value * 100).ToString("0.#", De) + " %";
    }
}
