using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using InsightTokenWatcher.Core;
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
        var scale = args.Length > 2 && args[1] == "--scale" ? double.Parse(args[2]) : 1.0;
        Directory.CreateDirectory(outDir);

        var builder = AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var now = new DateTimeOffset(DateTime.Now);
        var rows = DemoData.Rows(now, tick: 0);

        var win = new PopupWindow();
        win.Width = PopupCanvas.LogicalWidth * scale;
        win.Position = new PixelPoint(50, 50);
        win.Show();
        win.SetData(rows);
        win.Canvas.Now = now;
        Pump(10);

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
            var states = new (int crit, int errors, int warn, bool data)[]
            {
                (0, 0, 0, true), (1, 0, 0, true), (3, 0, 2, true), (0, 0, 2, true), (12, 0, 0, true), (0, 0, 0, false)
            };
            for (var i = 0; i < states.Length; i++)
            {
                var (crit, errors, warn, data) = states[i];
                using var bmp = IconFactory.BadgePng(crit, errors, warn, data, 64);
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
            using var iconImg = IconFactory.BadgePng(0, 0, 0, true, 256);
            using var iconData = iconImg.Encode(SKEncodedImageFormat.Png, 100);
            iconData.SaveTo(iconFs);
        }

        File.WriteAllText(Path.Combine(outDir, "report.txt"),
            $"scale={scale} width={win.Width} desiredHeight={win.Canvas.DesiredHeight} rows={rows.Count}\n");

        return 0;
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
