using System.Drawing.Imaging;
using System.Text;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>
/// Offscreen rendering for visual checks without touching the tray:
///   TokenWatch.exe --render &lt;outDir&gt; [--scale 1.25]
/// Writes popup_full.png, popup_scrolled.png (scrolled + 5 simulated poll updates),
/// popup_hover.png, icons.png, tooltip.txt and report.txt. Uses synthetic demo data only.
/// </summary>
internal static class RenderHarness
{
    public static int Run(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "render";
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == "--scale" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s))
                Theme.ForceScale(s);
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        var now = DateTimeOffset.UtcNow;

        using var canvas = new PopupCanvas { Width = Theme.S(PopupCanvas.LogicalWidth) };
        canvas.Size = new Size(Theme.S(PopupCanvas.LogicalWidth), 2000);
        canvas.SetData(DemoData.Build(now, 0));
        var full = canvas.DesiredHeight;
        report.AppendLine($"scale={Theme.DpiScale} width={canvas.Width} desiredHeight={full} rows={canvas.RowCount} content={canvas.ContentHeight}");
        Save(canvas, full, Path.Combine(outDir, "popup_full.png"));

        // constrained height → scroll to the bottom → 5 simulated poll updates must keep the position
        var limited = Theme.S(430);
        canvas.Size = new Size(canvas.Width, limited);
        canvas.ScrollTo(float.MaxValue);
        var before = canvas.ScrollOffset;
        for (var i = 1; i <= 5; i++)
            canvas.SetData(DemoData.Build(now.AddMinutes(i), i));
        report.AppendLine($"scroll before={before} after 5 updates={canvas.ScrollOffset} (must be equal)");
        Save(canvas, limited, Path.Combine(outDir, "popup_scrolled.png"));

        canvas.Size = new Size(canvas.Width, full);
        canvas.ScrollTo(0);
        canvas.SimulateHover(1);
        Save(canvas, full, Path.Combine(outDir, "popup_hover.png"));
        File.WriteAllText(Path.Combine(outDir, "tooltip.txt"), canvas.TooltipFor(1));

        SaveIcons(Path.Combine(outDir, "icons.png"));
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        return 0;
    }

    private static void Save(PopupCanvas canvas, int height, string path)
    {
        // 24bpp: GDI text (TextRenderer) would punch alpha holes into a 32bpp ARGB bitmap
        using var bmp = new Bitmap(canvas.Width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            canvas.Render(g, new Size(canvas.Width, height));
        bmp.Save(path, ImageFormat.Png);
    }

    private static void SaveIcons(string path)
    {
        var states = new List<(int crit, int warn, bool data)>
        {
            (0, 0, true), (1, 0, true), (3, 2, true), (0, 2, true), (12, 0, true), (0, 0, false)
        };
        int[] sizes = { 16, 20, 24, 32 };
        const int zoom = 6;
        var cell = 32 * zoom + 16;
        using var sheet = new Bitmap(cell * sizes.Length, cell * states.Count, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(45, 45, 52)); // taskbar-ish
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (var r = 0; r < states.Count; r++)
            for (var c = 0; c < sizes.Length; c++)
            {
                using var icon = IconFactory.Render(states[r].crit, states[r].warn, states[r].data, sizes[c]);
                var px = sizes[c] * zoom;
                g.DrawImage(icon, new Rectangle(c * cell + 8, r * cell + 8, px, px));
            }
        sheet.Save(path, ImageFormat.Png);
    }
}

/// <summary>Synthetic accounts covering every visual state (no real data).</summary>
internal static class DemoData
{
    public static IReadOnlyList<AccountView> Build(DateTimeOffset now, int tick)
    {
        var list = new List<AccountView>
        {
            Stale("Anthropic 1", ProviderKind.Anthropic, now, FailureKind.AuthExpired,
                "HTTP 401 – keine Rate-Limit-Header. {\"type\":\"error\",\"error\":{\"message\":\"OAuth access token has expired.\"}}",
                W("5h", "5-Std-Fenster", 300, 0.0, now.AddHours(4.9), 0),
                W("7d", "7-Tage-Fenster", 10080, 1.0, now.AddHours(100), 0, "abgelehnt")),
            Quota("Anthropic 2", ProviderKind.Anthropic, now,
                W("5h", "5-Std-Fenster", 300, 0.12, now.AddHours(3.6), 0.0004),
                W("7d", "7-Tage-Fenster", 10080, 1.0, now.AddHours(27), 0, "abgelehnt")),
            Quota("Anthropic 3", ProviderKind.Anthropic, now,
                W("5h", "5-Std-Fenster", 300, 0.22 + tick * 0.002, now.AddHours(4.05), 0.0011),
                W("7d", "7-Tage-Fenster", 10080, 0.90, now.AddHours(114), 0.0001, "Warnstufe")),
            Quota("Anthropic 4", ProviderKind.Anthropic, now,
                W("5h", "5-Std-Fenster", 300, 0.03, now.AddHours(4.8), 0),
                W("7d", "7-Tage-Fenster", 10080, 0.08, now.AddHours(146), 0)),
            Quota("Kimi Code", ProviderKind.KimiCode, now,
                W("week", "Wochenkontingent", 10080, 0.45, now.AddHours(145), 0.00002, total: 100),
                W("300m", "5-Std-Fenster", 300, 0.20, now.AddHours(4.1), 0.0006, total: 100)),
            Quota("GLM Coding Plan", ProviderKind.Zai, now,
                W("c1", "5-Std-Kontingent", 300, 0.585, now.AddHours(2.6), 0.00244, total: 28000, note: "Plan: max"),
                W("c2", "Wochen-Kontingent", 10080, 0.563, now.AddHours(94), 0.00005, total: 140000, note: "Plan: max")),
            Balance("DeepSeek", ProviderKind.DeepSeek, now, 23.42, 0.0006, "Bezahlt 23,42 $ · Gratis 0,00 $"),
            BalanceWithExtra("OpenRouter", ProviderKind.OpenRouter, now, 0.59, "Gekauft 10,00 $ · Verbraucht 9,41 $"),
            Error("Groq", ProviderKind.GenericOpenAi,
                "HTTP 401 – keine x-ratelimit-*-Header. {\"error\":{\"message\":\"Invalid API Key\"}}")
        };
        return list;
    }

    private static readonly Dictionary<QuotaWindow, double> Rates = new();

    private static QuotaWindow W(string key, string label, double minutes, double used, DateTimeOffset reset,
        double ratePerMin, string? note = null, double? total = null)
    {
        var w = new QuotaWindow
        {
            Key = key,
            Label = label,
            WindowMinutes = minutes,
            UsedFraction = used,
            ResetAtUtc = reset,
            Total = total,
            Remaining = total is null ? null : Math.Round(total.Value * (1 - used)),
            Unit = total is null ? "Quota" : "Requests",
            Note = note
        };
        Rates[w] = ratePerMin;
        return w;
    }

    private static (AccountSnapshot snap, Dictionary<string, WindowEvaluation> evals, Health health, List<Sample> series)
        Evaluate(Guid id, DateTimeOffset now, IEnumerable<QuotaWindow> windows)
    {
        var snap = new AccountSnapshot { AccountId = id, AtUtc = now, Ok = true };
        var evals = new Dictionary<string, WindowEvaluation>();
        var health = Health.Ok;
        List<Sample> primary = new();
        foreach (var w in windows)
        {
            Rates.TryGetValue(w, out var rate);
            var samples = new List<Sample>();
            for (var m = 60; m >= 0; m -= 5)
            {
                var t = now.AddMinutes(-m);
                samples.Add(w.IsBalance
                    ? new Sample(t, 0, (w.Remaining ?? 0) + rate * m)
                    : new Sample(t, Math.Max(0, (w.UsedFraction ?? 0) - rate * m), null));
            }
            snap.Windows.Add(w);
            var ev = Evaluator.Evaluate(w, samples, now);
            evals[w.Key] = ev;
            if (ev.Health > health) health = ev.Health;
            if (primary.Count == 0) primary = samples;
        }
        return (snap, evals, health, primary);
    }

    private static AccountView Quota(string name, ProviderKind p, DateTimeOffset now, params QuotaWindow[] windows)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        var (snap, evals, health, series) = Evaluate(cfg.Id, now, windows);
        return new AccountView
        {
            Config = cfg, Snapshot = snap, LastGood = snap, Evals = evals, Health = health, PrimarySeries = series
        };
    }

    private static AccountView Stale(string name, ProviderKind p, DateTimeOffset now, FailureKind kind, string error,
        params QuotaWindow[] windows)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        var (good, evals, _, series) = Evaluate(cfg.Id, now.AddMinutes(-37), windows);
        return new AccountView
        {
            Config = cfg,
            Snapshot = AccountSnapshot.Failure(cfg.Id, error),
            LastGood = good,
            Evals = evals,
            Health = Health.Unknown,
            Failure = kind,
            ConsecutiveFailures = 2,
            PrimarySeries = series
        };
    }

    private static AccountView Balance(string name, ProviderKind p, DateTimeOffset now, double amount, double ratePerMin,
        string note)
    {
        var w = new QuotaWindow
        {
            Key = "balance", Label = "Guthaben", IsBalance = true, Remaining = amount, Unit = "USD", Note = note
        };
        Rates[w] = ratePerMin;
        return Quota(name, p, now, w);
    }

    private static AccountView BalanceWithExtra(string name, ProviderKind p, DateTimeOffset now, double amount, string note)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        var bal = new QuotaWindow { Key = "balance", Label = "Guthaben", IsBalance = true, Remaining = amount, Unit = "USD", Note = note };
        var key = new QuotaWindow
        {
            Key = "key-info", Label = "Key (kein Limit gesetzt)", IsInfoOnly = true,
            Note = "Tag 0,00 $ · Woche 0,00 $ · Monat 0,00 $"
        };
        var (snap, evals, health, series) = Evaluate(cfg.Id, now, new[] { key, bal });
        return new AccountView
        {
            Config = cfg, Snapshot = snap, LastGood = snap, Evals = evals, Health = health, PrimarySeries = series
        };
    }

    private static AccountView Error(string name, ProviderKind p, string error)
    {
        var cfg = new AccountConfig { Name = name, Provider = p };
        return new AccountView
        {
            Config = cfg,
            Snapshot = AccountSnapshot.Failure(cfg.Id, error),
            Health = Health.Crit,
            Failure = FailureKind.AuthInvalid,
            ConsecutiveFailures = 1
        };
    }
}
