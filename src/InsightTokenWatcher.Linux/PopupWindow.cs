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

        var maxH = 0.0;
        foreach (var s in Screens.All) maxH = Math.Max(maxH, s.WorkingArea.Height);
        var h = Math.Min(Canvas.DesiredHeight, maxH * 0.85);

        if (PreferredLocation is { } p && Screens.All.Any(s => s.Bounds.Contains(p)))
        {
            Position = p;
            Height = h;
            return;
        }

        // no global cursor position on Linux – anchor at the bottom-right of the primary screen
        var wa = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
        Position = new PixelPoint(wa.Right - (int)Width - 12, wa.Bottom - (int)h - 12);
        Height = h;
    }
}
