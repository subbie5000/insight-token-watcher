namespace InsightTokenWatcher.Core;

/// <summary>
/// Why an account cannot be used right now. <paramref name="FreeAtUtc"/>: when it works again
/// (the reset of the limit that was reached); null when no time is known – an empty balance
/// stays empty until it is topped up.
/// </summary>
public sealed record Block(QuotaWindow Window, DateTimeOffset? FreeAtUtc);

public static class Usability
{
    /// <summary>
    /// "Blocked" is a fact about now, not a forecast: requests fail until something changes.
    /// <list type="bullet">
    /// <item>A general quota window has reached its limit and its reset is still ahead. A
    /// model limit (Fable) does not count – every other model still runs.</item>
    /// <item>A balance that cannot pay for requests (<see cref="QuotaWindow.Unusable"/>).</item>
    /// </list>
    /// Judged by the values the popup shows: a limit that was reached stays reached when a
    /// later poll fails. Null = usable as far as known (also: no data yet).
    /// </summary>
    public static Block? Blocked(AccountView v, DateTimeOffset now)
    {
        if (v.Display is not { } snap) return null;
        Block? block = null;
        foreach (var w in PollingEngine.GeneralWindows(snap))
        {
            if (w.IsInfoOnly) continue;
            DateTimeOffset? free;
            if (w.IsBalance)
            {
                if (!w.Unusable) continue;
                free = null;
            }
            else
            {
                if (w.UsedFraction is not >= 0.999) continue;
                if (w.ResetAtUtc <= now) continue; // the reset has passed: the window is fresh again
                free = w.ResetAtUtc;
            }
            // several limits reached: the account works again when the last of them resets
            if (block is null || (block.FreeAtUtc is { } known && (free is null || free > known)))
                block = new Block(w, free);
        }
        return block;
    }
}
