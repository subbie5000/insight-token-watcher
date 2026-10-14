using System.Collections.Concurrent;

namespace TokenWatch.Core;

public enum FailureKind
{
    None,
    AuthExpired,   // token expired – usually rotated by the owning tool soon
    AuthInvalid,   // key wrong/revoked/missing
    Network,       // timeout, DNS, connection refused
    Other
}

public sealed class AccountView
{
    public required AccountConfig Config { get; init; }

    /// <summary>Latest poll result (may be a failure).</summary>
    public AccountSnapshot? Snapshot { get; init; }

    /// <summary>Latest successful poll result.</summary>
    public AccountSnapshot? LastGood { get; init; }

    public IReadOnlyDictionary<string, WindowEvaluation> Evals { get; init; } =
        new Dictionary<string, WindowEvaluation>();

    public IReadOnlyList<Sample> PrimarySeries { get; init; } = Array.Empty<Sample>();
    public Health Health { get; init; }
    public bool Polling { get; init; }
    public FailureKind Failure { get; init; }
    public int ConsecutiveFailures { get; init; }

    /// <summary>What to display: the latest snapshot if it succeeded, otherwise the last good one.</summary>
    public AccountSnapshot? Display => Snapshot is { Ok: true } ? Snapshot : LastGood;

    /// <summary>The latest poll failed, but older valid values exist.</summary>
    public bool IsStale => Snapshot is { Ok: false } && LastGood is not null;
}

internal sealed class AccountRuntime
{
    public readonly object Gate = new();
    public required AccountConfig Config;
    public string PlainKey = "";
    public int Generation;
    public AccountSnapshot? Snapshot;
    public AccountSnapshot? LastGood;
    public readonly Dictionary<string, WindowSeries> Series = new();
    public readonly Dictionary<string, WindowEvaluation> Evals = new();
    public Health Health = Health.Unknown;
    public FailureKind Failure;
    public int ConsecutiveFailures;
    public int IdleStreak;
    public DateTimeOffset NextPollUtc;
    public bool Polling;
    public bool CritNotified;
}

/// <summary>Polls all accounts on their own interval, maintains history and evaluations.</summary>
public sealed class PollingEngine : IDisposable
{
    /// <summary>Fixed provider order for display (stable rows instead of jumping by severity).</summary>
    public static readonly ProviderKind[] DisplayOrder =
    {
        ProviderKind.Anthropic, ProviderKind.OpenAi, ProviderKind.KimiCode, ProviderKind.Moonshot,
        ProviderKind.Zai, ProviderKind.DeepSeek, ProviderKind.OpenRouter, ProviderKind.GenericOpenAi
    };

    private const int NetworkFailuresBeforeCrit = 3;
    private const int IdlePollsBeforeSlowdown = 10;

    private readonly ConcurrentDictionary<Guid, AccountRuntime> _runtimes = new();
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a thread-pool thread – UI must marshal.</summary>
    public event Action<Guid>? Updated;

    /// <summary>Raised once when an account becomes critical (thread-pool thread).</summary>
    public event Action<AccountView, string>? CritEntered;

    public PollingEngine()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Reconfigure(IReadOnlyList<(AccountConfig cfg, string plainKey)> accounts)
    {
        var seen = new HashSet<Guid>();
        var stagger = 0;
        foreach (var (cfg, key) in accounts)
        {
            seen.Add(cfg.Id);
            var created = false;
            var rt = _runtimes.GetOrAdd(cfg.Id, _ =>
            {
                created = true;
                return new AccountRuntime
                {
                    Config = cfg,
                    PlainKey = key,
                    NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(1 + stagger++ * 2)
                };
            });
            if (created) continue;

            lock (rt.Gate)
            {
                var providerChanged = rt.Config.Provider != cfg.Provider ||
                                      rt.Config.BaseUrlOverride != cfg.BaseUrlOverride;
                var credChanged = providerChanged || rt.PlainKey != key ||
                                  rt.Config.Model != cfg.Model || rt.Config.Auth != cfg.Auth;
                rt.Config = cfg;
                rt.PlainKey = key;
                if (!credChanged) continue;

                // New credentials: discard in-flight results. A rotated token for the same
                // account keeps its history (burn rate continuity); a provider change resets it.
                rt.Generation++;
                rt.NextPollUtc = DateTimeOffset.UtcNow;
                rt.ConsecutiveFailures = 0;
                if (rt.Snapshot is { Ok: false })
                    rt.Failure = FailureKind.None;
                if (providerChanged)
                {
                    rt.Snapshot = null;
                    rt.LastGood = null;
                    rt.Series.Clear();
                    rt.Evals.Clear();
                    rt.Health = Health.Unknown;
                    rt.IdleStreak = 0;
                }
            }
        }
        foreach (var id in _runtimes.Keys.Where(k => !seen.Contains(k)).ToList())
            _runtimes.TryRemove(id, out _);
    }

    public void ForceRefresh(Guid? id = null)
    {
        foreach (var rt in _runtimes.Values)
            if (id is null || rt.Config.Id == id.Value)
                lock (rt.Gate)
                    rt.NextPollUtc = DateTimeOffset.MinValue;
    }

    public IReadOnlyList<AccountView> GetView()
    {
        return _runtimes.Values
            .Select(rt => { lock (rt.Gate) return ToViewLocked(rt); })
            .OrderBy(v => Array.IndexOf(DisplayOrder, v.Config.Provider))
            .ThenBy(v => v.Config.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var rt in _runtimes.Values)
            {
                lock (rt.Gate)
                {
                    if (!rt.Config.Enabled || rt.Polling || now < rt.NextPollUtc) continue;
                    rt.Polling = true;
                }
                _ = Task.Run(() => PollOneAsync(rt, ct), CancellationToken.None);
            }
            try { await Task.Delay(1000, ct); }
            catch (TaskCanceledException) { }
        }
    }

    private async Task PollOneAsync(AccountRuntime rt, CancellationToken ct)
    {
        AccountConfig cfg;
        string key;
        int generation;
        lock (rt.Gate)
        {
            cfg = rt.Config;
            key = rt.PlainKey;
            generation = rt.Generation;
        }
        Updated?.Invoke(cfg.Id); // shows the "updating" state

        AccountSnapshot snap;
        try
        {
            snap = string.IsNullOrEmpty(key)
                ? AccountSnapshot.Failure(cfg.Id, "Kein API-Schlüssel hinterlegt (Einstellungen).")
                : await ProviderInfo.Poller(cfg.Provider).PollAsync(cfg, key, _http, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // shutting down
        }
        catch (Exception ex)
        {
            snap = AccountSnapshot.Failure(cfg.Id, ex.GetType().Name + ": " + ex.Message);
        }
        Ingest(rt, snap, generation);
    }

    /// <summary>Test hook: feed a result for an account as if a poll had returned it.</summary>
    internal void IngestFor(Guid accountId, AccountSnapshot snap)
    {
        if (!_runtimes.TryGetValue(accountId, out var rt)) return;
        int generation;
        lock (rt.Gate) generation = rt.Generation;
        Ingest(rt, snap, generation);
    }

    private void Ingest(AccountRuntime rt, AccountSnapshot snap, int generation)
    {
        AccountView? critView = null;
        string critMessage = "";

        lock (rt.Gate)
        {
            rt.Polling = false;
            if (generation != rt.Generation)
                return; // result belongs to replaced credentials

            var now = snap.AtUtc;
            rt.Snapshot = snap;
            Health health;

            if (snap.Ok)
            {
                var changed = rt.LastGood is null || !SameValues(rt.LastGood, snap);
                rt.IdleStreak = changed ? 0 : rt.IdleStreak + 1;
                rt.LastGood = snap;
                rt.Failure = FailureKind.None;
                rt.ConsecutiveFailures = 0;

                var keys = new HashSet<string>();
                foreach (var w in snap.Windows)
                {
                    keys.Add(w.Key);
                    if (!rt.Series.TryGetValue(w.Key, out var series))
                        rt.Series[w.Key] = series = new WindowSeries();
                    series.Add(new Sample(now, w.UsedFraction ?? 0, w.Remaining), w.IsBalance);
                    rt.Evals[w.Key] = Evaluator.Evaluate(w, series.Samples, now);
                }
                foreach (var gone in rt.Evals.Keys.Where(k => !keys.Contains(k)).ToList())
                {
                    rt.Evals.Remove(gone);
                    rt.Series.Remove(gone);
                }

                health = snap.Windows.Count == 0
                    ? Health.Unknown
                    : snap.Windows
                        .Select(w => rt.Evals.TryGetValue(w.Key, out var e) ? e.Health : Health.Unknown)
                        .Aggregate(Health.Ok, (a, b) => (Health)Math.Max((int)a, (int)b));
            }
            else
            {
                rt.ConsecutiveFailures++;
                rt.Failure = ClassifyFailure(snap.Error);
                rt.IdleStreak = 0;
                health = rt.Failure switch
                {
                    // expired tokens get rotated by their owner – not an alarm
                    FailureKind.AuthExpired when rt.LastGood is not null => Health.Unknown,
                    // brief network hiccups keep the last known state
                    FailureKind.Network when rt.ConsecutiveFailures < NetworkFailuresBeforeCrit && rt.LastGood is not null
                        => rt.Health,
                    _ => Health.Crit
                };
            }

            rt.Health = health;
            rt.NextPollUtc = DateTimeOffset.UtcNow + NextDelay(rt);

            if (health == Health.Crit && !rt.CritNotified)
            {
                rt.CritNotified = true;
                critView = ToViewLocked(rt);
                if (snap.Ok)
                {
                    var worst = snap.Windows.FirstOrDefault(w =>
                        rt.Evals.TryGetValue(w.Key, out var e) && e.Health == Health.Crit);
                    critMessage = worst is not null && rt.Evals.TryGetValue(worst.Key, out var ev)
                        ? $"{worst.Label}: {ev.Verdict}"
                        : "Kritisch";
                }
                else
                {
                    critMessage = "Fehler: " + snap.Error;
                }
            }
            else if (health != Health.Crit)
            {
                rt.CritNotified = false;
            }
        }

        if (critView is not null) CritEntered?.Invoke(critView, critMessage);
        Updated?.Invoke(snap.AccountId);
    }

    internal static FailureKind ClassifyFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return FailureKind.Other;
        var e = error.ToLowerInvariant();
        if (e.Contains("expired") || e.Contains("abgelaufen")) return FailureKind.AuthExpired;
        if (e.Contains("kein api-schlüssel") || e.Contains("http 401") || e.Contains("http 403") ||
            e.Contains("authentication_error") || e.Contains("unauthorized") || e.Contains("invalid api key") ||
            e.Contains("api-fehler 401") || e.Contains("incorrect"))
            return FailureKind.AuthInvalid;
        if (e.StartsWith("httprequestexception") || e.StartsWith("taskcanceledexception") ||
            e.StartsWith("socketexception") || e.StartsWith("timeoutexception") ||
            e.Contains("http 502") || e.Contains("http 503") || e.Contains("http 504"))
            return FailureKind.Network;
        return FailureKind.Other;
    }

    private static TimeSpan NextDelay(AccountRuntime rt)
    {
        var baseS = Math.Clamp(rt.Config.PollSeconds, 30, 3600);
        if (rt.Snapshot is { Ok: false })
        {
            var s = rt.Failure switch
            {
                FailureKind.Network => Math.Min(baseS, 60) * Math.Min(rt.ConsecutiveFailures, 5), // backoff ≤ 5 min
                FailureKind.AuthExpired or FailureKind.AuthInvalid => Math.Max(baseS, 300),
                _ => Math.Max(baseS, 120)
            };
            return TimeSpan.FromSeconds(s);
        }
        // nothing changed for a while → poll less often (≤ 5 min); any change restores the base rate
        if (rt.IdleStreak >= IdlePollsBeforeSlowdown)
            return TimeSpan.FromSeconds(Math.Max(baseS, Math.Min(baseS * 5, 300)));
        return TimeSpan.FromSeconds(baseS);
    }

    private static bool SameValues(AccountSnapshot a, AccountSnapshot b)
    {
        if (a.Windows.Count != b.Windows.Count) return false;
        for (var i = 0; i < a.Windows.Count; i++)
        {
            var x = a.Windows[i];
            var y = b.Windows.FirstOrDefault(w => w.Key == x.Key);
            if (y is null) return false;
            if (Math.Abs((x.UsedFraction ?? -1) - (y.UsedFraction ?? -1)) > 1e-6) return false;
            if (Math.Abs((x.Remaining ?? -1) - (y.Remaining ?? -1)) > 1e-6) return false;
        }
        return true;
    }

    private static AccountView ToViewLocked(AccountRuntime rt)
    {
        var display = rt.Snapshot is { Ok: true } ? rt.Snapshot : rt.LastGood;
        var first = display?.Windows.FirstOrDefault();
        return new AccountView
        {
            Config = rt.Config,
            Snapshot = rt.Snapshot,
            LastGood = rt.LastGood,
            Evals = new Dictionary<string, WindowEvaluation>(rt.Evals),
            PrimarySeries = first is not null && rt.Series.TryGetValue(first.Key, out var s)
                ? s.Samples.ToList()
                : Array.Empty<Sample>(),
            Health = rt.Health,
            Polling = rt.Polling,
            Failure = rt.Failure,
            ConsecutiveFailures = rt.ConsecutiveFailures
        };
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _http.Dispose();
    }
}
