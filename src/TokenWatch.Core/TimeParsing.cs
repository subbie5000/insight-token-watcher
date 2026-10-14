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
    public static string Span(TimeSpan? ts)
    {
        if (ts is null) return "∞";
        var t = ts.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return "<1 min";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h {t.Minutes:D2} min";
        return $"{(int)t.TotalDays} d {t.Hours} h";
    }

    public static string Count(double? v)
    {
        if (v is null) return "–";
        var x = v.Value;
        if (Math.Abs(x) >= 1_000_000) return (x / 1_000_000).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " M";
        if (Math.Abs(x) >= 10_000) return (x / 1000).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " k";
        return x.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"));
    }

    public static string Money(double? v, string unit)
    {
        if (v is null) return "–";
        var cur = unit is "USD" or "$" ? "$" : unit is "CNY" or "¥" ? "¥" : unit;
        return cur + v.Value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static string Percent(double? frac)
    {
        if (frac is null) return "–";
        return (frac.Value * 100).ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " %";
    }
}
