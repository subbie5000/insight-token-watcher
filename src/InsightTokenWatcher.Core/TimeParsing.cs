using System.Globalization;
using System.Text.RegularExpressions;

namespace InsightTokenWatcher.Core;

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
        // the magnitude tells the unit: s ≈ 1.7e9, ms ≈ 1.7e12, µs ≈ 1.7e15, ns ≈ 1.7e18.
        // Clamped: one odd header must not throw and take the whole poll down with it.
        var abs = value == long.MinValue ? long.MaxValue : Math.Abs(value); // Math.Abs(MinValue) throws
        var ms = abs > 100_000_000_000_000_000L ? value / 1_000_000
            : abs > 100_000_000_000_000L ? value / 1000
            : abs > 1_000_000_000_000L ? value
            : value * 1000;
        const long maxMs = 253_402_300_799_999L; // 9999-12-31
        return DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, -62_135_596_800_000L, maxMs));
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

    /// <summary>Whole percent for table cells. Never rounds onto the two states the colours
    /// stand for: "0 %" only when nothing is used, "100 %" only when the limit is reached.</summary>
    public static string PercentShort(double? frac)
    {
        if (frac is not { } f) return "–";
        if (f <= 0) return "0 %";
        if (f >= 0.999) return "100 %";
        if (f < 0.005) return "<1 %";
        return Math.Min(99, (int)Math.Round(f * 100)).ToString(De) + " %";
    }
}
