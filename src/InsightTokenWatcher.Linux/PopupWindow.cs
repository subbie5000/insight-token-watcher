using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Linux;

/// <summary>Borderless, topmost popup near the tray area. Linux has no hover-on-tray,
/// so it opens/closes on tray click (or via the menu) and auto-hides when it loses
/// focus unless pinned.</summary>
internal sealed class PopupWindow : Window
{
    private readonly Border _chrome;
    private readonly DispatcherTimer _repaint = new() { Interval = TimeSpan.FromSeconds(20) };

    public PopupCanvas Canvas { get; }

    /// <summary>User-parked position (drag). When set and visible on any screen, it wins over the corner anchor.</summary>
    public PixelPoint? PreferredLocation { get; set; }

    public PopupWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Width = PopupCanvas.LogicalWidth;

        Canvas = new PopupCanvas();
        _chrome = new Border
        {
            Background = Ui.BgBrush,
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Ui.CardBorder),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = Canvas
        };
        Content = _chrome;

        _repaint.Tick += (_, _) => { Canvas.Now = DateTimeOffset.Now; Canvas.InvalidateVisual(); };
        Deactivated += (_, _) => { if (!Canvas.Pinned) HidePopup(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePopup(); };
        Opened += (_, _) => _repaint.Start();
    }

    public void SetData(IReadOnlyList<AccountView> views)
    {
        Canvas.SetData(views);
        if (IsVisible) ApplyBounds();
    }

    public void ShowPopup(bool pinned)
    {
        Canvas.Pinned = pinned;
        ApplyBounds();
        Show();
        Activate();
    }

    public void HidePopup()
    {
        _repaint.Stop();
        Canvas.Pinned = false;
        Canvas.ResetHover();
        Hide();
    }

    private void ApplyBounds()
    {
        if (Canvas.IsDragging) return; // don't yank the window mid-drag

        // Avalonia: Width/Height are DIPs, screen rects and Position are physical pixels. The old
        // code mixed both, so on a HiDPI screen the popup hung off the right edge; it also kept a
        // parked spot even after the popup had grown past the screen bottom.
        var parked = PreferredLocation is { } p ? Screens.All.FirstOrDefault(s => s.Bounds.Contains(p)) : null;
        var screen = parked ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
        var scale = screen?.Scaling is > 0 and var sc ? sc : 1.0;

        var hDip = Math.Min(Canvas.DesiredHeight, wa.Height / scale * 0.85);
        var wPx = (int)Math.Ceiling(Width * scale);
        var hPx = (int)Math.Ceiling(hDip * scale);
        var margin = (int)(12 * scale);
        // no global cursor position on Linux – unparked, it sits at the bottom-right of the primary screen
        var target = parked is not null
            ? PreferredLocation!.Value
            : new PixelPoint(wa.Right - wPx - margin, wa.Bottom - hPx - margin);
        Position = new PixelPoint(
            Math.Clamp(target.X, wa.X, Math.Max(wa.X, wa.Right - wPx)),
            Math.Clamp(target.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - hPx)));
        Height = hDip;
    }
}
