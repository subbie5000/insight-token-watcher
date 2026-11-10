using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InsightTokenWatcher.Core;
using InsightTokenWatcher.Demo;
using SkiaSharp;

namespace InsightTokenWatcher.Linux;

/// <summary>Offscreen popup rendering: insight-token-watcher --render &lt;outDir&gt;.
/// Uses Avalonia.Headless with real Skia drawing – the same Render() path as on a desktop.</summary>
internal static class RenderHarness
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("--render <outDir> [--scale 1.5]");
            return 2;
        }
        var outDir = args[0];
        var scale = args.Length > 2 && args[1] == "--scale" ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        Directory.CreateDirectory(outDir);

        var builder = AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var now = new DateTimeOffset(DateTime.Now);
        var rows = DemoData.Build(now, 0);   // the same accounts as the Windows harness

        var win = new PopupWindow();
        win.Position = new PixelPoint(50, 50);
        win.Show();
        win.SetData(rows);
        win.Canvas.Now = now;
        Pump(10);

        var fullScrollable = win.Canvas.IsScrollable;
        var frame = win.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(outDir, "popup_full.png")))
            frame.Save(fs);

        win.Height = 320 * scale;
        Pump(4);
        win.Canvas.SetScrollForRender(180 * scale);
        Pump(6);
        frame = win.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(outDir, "popup_scrolled.png")))
            frame.Save(fs);

        // tray badge states, upscaled 6x for inspection
        using (var sheet = new SKBitmap(4 * 64, 5 * 64))
        using (var c = new SKCanvas(sheet))
        {
            c.Clear(new SKColor(24, 26, 32));
            var states = new (int crit, int errors, int warn, bool data, double? week)[]
            {
                (0, 0, 0, true, 0.0), (0, 0, 0, true, 0.35), (0, 0, 0, true, 0.62),
                (1, 0, 0, true, 0.78), (0, 0, 0, true, 1.0), (0, 0, 2, true, 0.12),
                (0, 0, 0, false, null)
            };
            for (var i = 0; i < states.Length; i++)
            {
                var (crit, errors, warn, data, week) = states[i];
                using var bmp = IconFactory.BadgePng(crit, errors, warn, data, 64, week);
                using var img = SKImage.FromBitmap(bmp);
                c.DrawImage(img, new SKRect(64 * (i / 5), 64 * (i % 5), 64 * (i / 5) + 64, 64 * (i % 5) + 64));
            }
            using var simg = SKImage.FromBitmap(sheet);
            using var sdata = simg.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(Path.Combine(outDir, "icons.png"));
            sdata.SaveTo(fs);
        }

        using (var iconFs = File.Create(Path.Combine(outDir, "app-icon.png")))
        {
            // launcher/menu icon: the brand ring – not the live "0 %" tray gauge
            using var iconImg = IconFactory.AppIconPng(256);
            using var iconData = iconImg.Encode(SKEncodedImageFormat.Png, 100);
            iconData.SaveTo(iconFs);
        }

        // ---- click self-test through the real pointer path (headless input) ----
        var canvas = win.Canvas;
        var pins = 0;
        var sorts = new List<string>();
        bool? leftPressedOnRelease = null;
        MouseButton? initialButton = null;
        canvas.PinClicked += () => pins++;
        canvas.SortChanged += (c, d) => sorts.Add(c == SortColumn.None ? "None" : $"{c}{(d ? "-desc" : "-asc")}");
        canvas.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            leftPressedOnRelease = e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed;
            initialButton = e.InitialPressMouseButton;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        void Click(Point p)
        {
            var at = canvas.TranslatePoint(p, win) ?? p;
            win.MouseMove(at);
            win.MouseDown(at, MouseButton.Left);
            win.MouseUp(at, MouseButton.Left);
            Pump(3);
        }

        // every spot of the popup must belong to the canvas for the pointer – also where nothing
        // but background is drawn (free header space, gap between two cards)
        string Hit(Point p) => win.InputHitTest(canvas.TranslatePoint(p, win) ?? p)?.GetType().Name ?? "null";
        var hits = $"header-empty={Hit(new Point(300, 20))} between-titles={Hit(new Point(150, 71))} card-gap={Hit(new Point(100, 200))}";

        var fullWidth = canvas.LogicalWidth;
        win.Height = canvas.DesiredHeight + 2 * PopupWindow.Frame;
        Pump(4);
        Click(canvas.PinButtonPoint());
        Click(canvas.ColumnTitlePoint(SortColumn.Long));
        Click(canvas.ColumnTitlePoint(SortColumn.Long));
        Click(canvas.ColumnTitlePoint(SortColumn.Long));
        Click(canvas.ColumnTitlePoint(SortColumn.Name));
        Click(canvas.ColumnTitlePoint(SortColumn.Model));
        var fableOrder = string.Join(" | ", canvas.RowNames());
        frame = win.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(outDir, "popup_sorted_fable.png")))
            frame.Save(fs);

        // sorted by week, mouse resting on the title (footer explains the cycle)
        canvas.SetSort(SortColumn.Long, false);
        var weekOrder = string.Join(" | ", canvas.RowNames());
        win.MouseMove(canvas.TranslatePoint(canvas.ColumnTitlePoint(SortColumn.Long), win) ?? default);
        Pump(6);
        frame = win.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(outDir, "popup_sorted_week.png")))
            frame.Save(fs);
        canvas.SetSort(SortColumn.Long, true);
        Pump(6);
        frame = win.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(outDir, "popup_sorted_week_desc.png")))
            frame.Save(fs);

        // nobody reports a model limit → two columns at the narrow width
        canvas.SetSort(SortColumn.None, false);
        win.MouseMove(new Point(1, 1));
        win.SetData(DemoData.Build(now, 0, fable: false));
        win.Canvas.Now = now;
        win.Height = canvas.DesiredHeight + 2 * PopupWindow.Frame;
        Pump(6);
        var twoColumns = $"width={win.Width} height={win.Height} canvas={canvas.Bounds.Width}x{canvas.Bounds.Height} scrollable={canvas.IsScrollable} (expect False)";
        // Headless renders only when a frame is captured, and after a WIDTH change the first
        // capture still shows the previous arrangement (one frame behind; measured: bar 161–340
        // instead of 161–300). A desktop render loop does not have this – take the second frame.
        win.CaptureRenderedFrame();
        frame = win.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(outDir, "popup_two_columns.png")))
            frame.Save(fs);

        File.WriteAllText(Path.Combine(outDir, "report.txt"),
            $"scale={scale} width={fullWidth} rows={rows.Count} scrollable={fullScrollable} (expect False)\n" +
            $"click: pinClicks={pins} (expect 1) sorts=[{string.Join(", ", sorts)}] (expect Long-asc, Long-desc, None, Name-asc, Model-asc)\n" +
            $"week asc:  {weekOrder}\n" +
            $"fable asc: {fableOrder}\n" +
            $"two columns: {twoColumns}\n" +
            $"release event: IsLeftButtonPressed={leftPressedOnRelease} InitialPressMouseButton={initialButton}\n" +
            $"pointer target: {hits} (expect PopupCanvas everywhere)\n" +
            $"tray icon bitmap: {OpaqueBounds(IconFactory.TrayIconPng(0, 0, 1, true, weekFraction: 0.4))}\n");

        return 0;
    }

    /// <summary>Where the icon actually has pixels – a tray scales the whole bitmap, so the
    /// gauge must fill it.</summary>
    private static string OpaqueBounds(MemoryStream png)
    {
        using var ms = png;
        using var bmp = SKBitmap.Decode(ms);
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 16)
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        return $"{bmp.Width}x{bmp.Height}, opaque ({minX},{minY})-({maxX},{maxY})";
    }

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(40);
        }
    }
}
