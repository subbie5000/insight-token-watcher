using System.Drawing.Imaging;
using System.Text;
using InsightTokenWatcher.Core;
using InsightTokenWatcher.Demo;

namespace InsightTokenWatcher;

/// <summary>
/// Offscreen rendering for visual checks without touching the tray:
///   InsightTokenWatcher.exe --render &lt;outDir&gt; [--scale 1.25]
/// Writes popup_full.png, popup_scrolled.png (scrolled + 5 simulated poll updates),
/// popup_hover.png, popup_sorted_*.png, popup_two_columns.png (nobody reports a model
/// limit), icons.png, tooltip.txt and report.txt. Uses synthetic demo data only.
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

        using var canvas = new PopupCanvas();
        canvas.SetData(DemoData.Build(now, 0));
        canvas.Size = new Size(Theme.S(canvas.LogicalWidth), 2000); // the width follows the data (model column)
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

        // sorting through the real mouse path: WOCHE asc → desc → default, then KONTO
        canvas.ResetHover();
        var sorts = new List<string>();
        canvas.SortChanged += (c, d) => sorts.Add(c == SortColumn.None ? "None" : $"{c}-{(d ? "desc" : "asc")}");
        canvas.SimulateClick(canvas.ColumnTitlePoint(SortColumn.Long));
        Save(canvas, full, Path.Combine(outDir, "popup_sorted_week.png"));
        report.AppendLine("week asc:  " + string.Join(" | ", canvas.RowNames()));
        canvas.SimulateClick(canvas.ColumnTitlePoint(SortColumn.Long));
        Save(canvas, full, Path.Combine(outDir, "popup_sorted_week_desc.png"));
        report.AppendLine("week desc: " + string.Join(" | ", canvas.RowNames()));
        canvas.SimulateClick(canvas.ColumnTitlePoint(SortColumn.Long));
        report.AppendLine("default:   " + string.Join(" | ", canvas.RowNames()));
        canvas.SimulateClick(canvas.ColumnTitlePoint(SortColumn.Name));
        report.AppendLine("name asc:  " + string.Join(" | ", canvas.RowNames()));
        canvas.SimulateClick(canvas.ColumnTitlePoint(SortColumn.Short));
        report.AppendLine("5h asc:    " + string.Join(" | ", canvas.RowNames()));
        canvas.SimulateClick(canvas.ColumnTitlePoint(SortColumn.Model));
        Save(canvas, full, Path.Combine(outDir, "popup_sorted_fable.png"));
        report.AppendLine("fable asc: " + string.Join(" | ", canvas.RowNames()));
        report.AppendLine($"clicks: [{string.Join(", ", sorts)}] (expect Long-asc, Long-desc, None, Name-asc, Short-asc, Model-asc)");
        canvas.SetSort(SortColumn.None, false);

        // nobody reports a model limit → two columns at the narrow width
        canvas.SetData(DemoData.Build(now, 0, fable: false));
        canvas.Size = new Size(Theme.S(canvas.LogicalWidth), canvas.DesiredHeight);
        report.AppendLine($"two columns: width={canvas.Width} height={canvas.DesiredHeight}");
        Save(canvas, canvas.DesiredHeight, Path.Combine(outDir, "popup_two_columns.png"));

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
        var states = new List<(int crit, int warn, bool data, double? week)>
        {
            (0, 0, true, 0.0), (0, 0, true, 0.35), (0, 0, true, 0.62), (1, 0, true, 0.78),
            (0, 0, true, 1.0), (0, 2, true, 0.12), (0, 0, false, null)
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
                using var icon = IconFactory.Render(states[r].crit, states[r].warn, states[r].data, sizes[c], states[r].week);
                var px = sizes[c] * zoom;
                g.DrawImage(icon, new Rectangle(c * cell + 8, r * cell + 8, px, px));
            }
        sheet.Save(path, ImageFormat.Png);
    }
}
