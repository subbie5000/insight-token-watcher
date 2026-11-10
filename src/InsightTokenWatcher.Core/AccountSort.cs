namespace InsightTokenWatcher.Core;

/// <summary>Which column orders the popup (click on a column title).</summary>
public enum SortColumn
{
    /// <summary>Default: fixed provider order, then name.</summary>
    None,
    Name,

    /// <summary>Short window (≤ 1 day, e.g. 5 h).</summary>
    Short,

    /// <summary>Long window (&gt; 1 day, e.g. the week).</summary>
    Long,

    /// <summary>Model-scoped week (e.g. Fable).</summary>
    Model
}

/// <summary>
/// Popup order by column. Names sort naturally ("Anthropic 2" before "Anthropic 10").
/// The window columns answer "which account runs out first": accounts projected to run dry
/// within the column's horizon come first – a quota window before its reset (blocked ones at
/// the very top), a balance whose runway ends within 5 h / a week – soonest first. Then the
/// accounts that make it, fullest first (balances that last count as free capacity). Rows
/// without data for the column (errors, loading, info-only, missing window) always stay at
/// the end in default order. Descending flips everything except that tail.
/// </summary>
public static class AccountSort
{
    public static readonly IComparer<string> NaturalOrder = new NaturalComparer();

    /// <summary>Title click cycle: another column → ascending; same column → descending → default.</summary>
    public static (SortColumn column, bool descending) Next(SortColumn current, bool descending, SortColumn clicked)
    {
        if (clicked == SortColumn.None) return (SortColumn.None, false);
        if (current != clicked) return (clicked, false);
        return descending ? (SortColumn.None, false) : (clicked, true);
    }

    /// <summary>Orders <paramref name="views"/> (expected in default order – the sort is stable,
    /// so ties keep it).</summary>
    public static List<AccountView> Apply(IEnumerable<AccountView> views, SortColumn column, bool descending,
        DateTimeOffset now)
    {
        var list = views.ToList();
        switch (column)
        {
            case SortColumn.Name:
                return descending
                    ? list.OrderByDescending(v => v.Config.Name, NaturalOrder).ToList()
                    : list.OrderBy(v => v.Config.Name, NaturalOrder).ToList();
            case SortColumn.Short:
            case SortColumn.Long:
            case SortColumn.Model:
                var scope = WindowLayout.ColumnScope(list);
                return list
                    .Select(v => (view: v, key: EmptyKey(v, column, now, scope)))
                    .OrderBy(x => x.key.Group == 2 ? 2 : descending ? 1 - x.key.Group : x.key.Group)
                    .ThenBy(x => descending ? -x.key.Value : x.key.Value)
                    .ThenBy(x => x.view.IsStale ? 1 : 0) // on a tie, confirmed values before old ones
                    .Select(x => x.view)
                    .ToList();
            default:
                return list;
        }
    }

    /// <summary>
    /// Group 0 = runs dry within the column's horizon; Value = minutes until empty (0 = blocked now).
    /// Group 1 = makes it (reset first, no consumption measured, or a long runway); Value = −used
    /// fraction (balances: 0, i.e. free capacity).
    /// Group 2 = no data for this column.
    /// Uses the values the popup shows (the last good snapshot); stale values get no projection.
    /// </summary>
    internal static (int Group, double Value) EmptyKey(AccountView v, SortColumn column, DateTimeOffset now,
        string? columnScope = null)
    {
        if (v.Display is not { } snap) return (2, 0);
        var layout = WindowLayout.Classify(snap.Windows, columnScope);
        var w = column switch
        {
            SortColumn.Short => layout.Short,
            SortColumn.Model => layout.Model,
            _ => layout.Long
        };
        if (w is null || w.UsedFraction is not { } used)
        {
            // a balance says nothing about a model limit
            if (column == SortColumn.Model || layout.Span is not { IsBalance: true } balance) return (2, 0);
            if (!v.IsStale && v.Evals.TryGetValue(balance.Key, out var bev) &&
                bev is { HasRate: true, TimeToEmpty: { } runway })
            {
                var left = runway - (now - snap.AtUtc);
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                var horizon = TimeSpan.FromMinutes(column == SortColumn.Short ? 300 : 10080);
                if (left <= horizon) return (0, left.TotalMinutes);
            }
            return (1, 0);
        }

        TimeSpan? ttr = w.ResetAtUtc is { } r ? r - now : null;
        if (ttr <= TimeSpan.Zero) return (1, 0); // reset already passed: the window is fresh again
        if (used >= 0.999) return (0, 0);

        if (!v.IsStale && v.Evals.TryGetValue(w.Key, out var ev) && ev is { HasRate: true, TimeToEmpty: { } tte })
        {
            var left = tte - (now - snap.AtUtc);
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            if (ttr is null || left < ttr) return (0, left.TotalMinutes);
        }
        return (1, -used);
    }

    /// <summary>Digit runs compare by value, text runs culture-aware and case-insensitive.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int si = i, sj = j;
                int c;
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0');
                    var nb = b[sj..j].TrimStart('0');
                    c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                }
                else
                {
                    while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
                    c = string.Compare(a[si..i], b[sj..j], StringComparison.CurrentCultureIgnoreCase);
                }
                if (c != 0) return c;
            }
            var rest = (a.Length - i).CompareTo(b.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(a, b);
        }
    }
}
