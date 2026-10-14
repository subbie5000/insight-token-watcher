using System.Collections.Concurrent;

namespace TokenWatch.Core;

public sealed class AccountView
{
    public required AccountConfig Config { get; init; }
    public AccountSnapshot? Snapshot { get; init; }
    public IReadOnlyDictionary<string, WindowEvaluation> Evals { get; init; } =
        new Dictionary<string, WindowEvaluation>();
    public IReadOnlyList<Sample> PrimarySeries { get; init; } = Array.Empty<Sample>();
    public Health Health { get; init; }
    public bool Polling { get; init; }
}

internal sealed class AccountRuntime
{
    public required AccountConfig Config;
    public string PlainKey = "";
    public AccountSnapshot? Snapshot;
    public readonly Dictionary<string, WindowSeries> Series = new();
    public readonly Dictionary<string, WindowEvaluation> Evals = new();
    public Health Health = Health.Unknown;
    public DateTimeOffset NextPollUtc;
    public bool Polling;
    public DateTimeOffset? LastNotifiedCrit;
}

/// <summary>Polls all accounts on their own interval, maintains history and evaluations.</summary>
public sealed class PollingEngine : IDisposable
{
    private readonly ConcurrentDictionary<Guid, AccountRuntime> _runtimes = new();
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<Guid>? Updated;        // Threadpool-Thread! UI muss marshallen.
    public event Action<AccountView, string>? CritEntered;

    public PollingEngine()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Reconfigure(IReadOnlyList<(AccountConfig cfg, string plainKey)> accounts)
    {
        var seen = new HashSet<Guid>();
        var stagger = 0;
        foreach (var (cfg, key) in accounts)
        {
            seen.Add(cfg.Id);
            var rt = _runtimes.AddOrUpdate(cfg.Id,
                _ => new AccountRuntime
                {
                    Config = cfg,
                    PlainKey = key,
                    NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(2 + stagger++ * 3)
                },
                (_, old) =>
                {
                    var credChanged = old.PlainKey != key || old.Config.Provider != cfg.Provider ||
                                      old.Config.BaseUrlOverride != cfg.BaseUrlOverride || old.Config.Model != cfg.Model;
                    old.Config = cfg;
                    old.PlainKey = key;
                    if (credChanged)
                    {
                        old.Snapshot = null;
                        old.Series.Clear();
                        old.Evals.Clear();
                        old.Health = Health.Unknown;
                        old.NextPollUtc = DateTimeOffset.UtcNow;
                    }
                    return old;
                });
            _ = rt;
        }
        foreach (var id in _runtimes.Keys.Where(k => !seen.Contains(k)).ToList())
            _runtimes.TryRemove(id, out _);
    }

    public void ForceRefresh(Guid? id = null)
    {
        foreach (var rt in _runtimes.Values)
            if (id is null || rt.Config.Id == id.Value)
                rt.NextPollUtc = DateTimeOffset.MinValue;
    }

    public IReadOnlyList<AccountView> GetView()
    {
        return _runtimes.Values
            .Select(rt =>
            {
                lock (rt.Series)
                {
                    var first = rt.Snapshot?.Windows.FirstOrDefault();
                    return new AccountView
                    {
                        Config = rt.Config,
                        Snapshot = rt.Snapshot,
                        Evals = new Dictionary<string, WindowEvaluation>(rt.Evals),
                        PrimarySeries = first is not null && rt.Series.TryGetValue(first.Key, out var s)
                            ? s.Samples.ToList()
                            : (IReadOnlyList<Sample>)Array.Empty<Sample>(),
                        Health = rt.Health,
                        Polling = rt.Polling
                    };
                }
            })
            .OrderBy(v => v.Health switch { Health.Crit => 0, Health.Warn => 1, Health.Unknown => 2, _ => 3 })
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
                if (!rt.Config.Enabled || rt.Polling || now < rt.NextPollUtc) continue;
                rt.Polling = true;
                rt.NextPollUtc = now.AddSeconds(Math.Clamp(rt.Config.PollSeconds, 30, 3600));
                _ = Task.Run(() => PollOneAsync(rt, ct), CancellationToken.None);
            }
            try { await Task.Delay(1000, ct); }
            catch (TaskCanceledException) { }
        }
    }

    private async Task PollOneAsync(AccountRuntime rt, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(rt.PlainKey))
            {
                Apply(rt, AccountSnapshot.Failure(rt.Config.Id, "Kein API-Schlüssel hinterlegt (Einstellungen)."));
                return;
            }
            var poller = ProviderInfo.Poller(rt.Config.Provider);
            var snap = await poller.PollAsync(rt.Config, rt.PlainKey, _http, ct);
            Apply(rt, snap);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Apply(rt, AccountSnapshot.Failure(rt.Config.Id, ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            rt.Polling = false;
        }
    }

    private void Apply(AccountRuntime rt, AccountSnapshot snap)
    {
        var now = snap.AtUtc;
        rt.Snapshot = snap;
        if (snap.Ok)
        {
            lock (rt.Series)
            {
                foreach (var w in snap.Windows)
                {
                    if (!rt.Series.TryGetValue(w.Key, out var series))
                        rt.Series[w.Key] = series = new WindowSeries();
                    series.Add(new Sample(now, w.UsedFraction ?? 0, w.Remaining), w.IsBalance);
                    rt.Evals[w.Key] = Evaluator.Evaluate(w, series.Samples, now);
                }
            }
            rt.Health = snap.Windows.Count == 0 ? Health.Unknown
                : snap.Windows.Select(w => rt.Evals.TryGetValue(w.Key, out var e) ? e.Health : Health.Unknown)
                    .Aggregate(Health.Ok, (a, b) => (Health)Math.Max((int)a, (int)b));
        }
        else
        {
            rt.Health = Health.Crit;
        }

        if (rt.Health == Health.Crit && rt.LastNotifiedCrit is null)
        {
            rt.LastNotifiedCrit = now;
            var worst = snap.Ok
                ? snap.Windows.FirstOrDefault(w => rt.Evals.TryGetValue(w.Key, out var e) && e.Health == Health.Crit)
                : null;
            var msg = snap.Ok
                ? (worst is not null && rt.Evals.TryGetValue(worst.Key, out var ev) ? $"{worst.Label}: {ev.Verdict}" : "Kritisch")
                : "Fehler: " + snap.Error;
            CritEntered?.Invoke(ToView(rt), msg);
        }
        else if (rt.Health != Health.Crit)
        {
            rt.LastNotifiedCrit = null;
        }

        Updated?.Invoke(rt.Config.Id);
    }

    private static AccountView ToView(AccountRuntime rt) => new()
    {
        Config = rt.Config,
        Snapshot = rt.Snapshot,
        Evals = new Dictionary<string, WindowEvaluation>(rt.Evals),
        Health = rt.Health,
        Polling = rt.Polling
    };

    public void Dispose()
    {
        _cts?.Cancel();
        _http.Dispose();
    }
}
