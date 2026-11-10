namespace InsightTokenWatcher.Core;

/// <summary>What the tray ring shows, shared by the Windows and the Linux app.</summary>
public static class TrayGauge
{
    /// <summary>
    /// Mean used fraction of the general week windows of all Anthropic accounts; null without
    /// data. Model-scoped weeks (e.g. Fable) do not count – a row of unused Fable windows would
    /// otherwise halve the value.
    /// </summary>
    public static double? WeekFraction(IEnumerable<AccountView> views)
    {
        var fractions = views
            .Where(v => v.Config.Provider == ProviderKind.Anthropic)
            .SelectMany(v => v.Display?.Windows ?? Enumerable.Empty<QuotaWindow>())
            .Where(w => w.Scope is null && (w.WindowMinutes ?? 0) >= WindowLayout.DayMinutes && w.UsedFraction is not null)
            .Select(w => w.UsedFraction!.Value)
            .ToList();
        return fractions.Count > 0 ? fractions.Average() : null;
    }
}
