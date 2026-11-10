using System.Drawing;

namespace InsightTokenWatcher.Core;

/// <summary>Where the popup goes on a multi-monitor desktop (pure geometry, so it can be tested).</summary>
public static class PopupPlacement
{
    /// <summary>
    /// Bounds for a popup the user parked at <paramref name="parked"/>: on the screen that shows
    /// most of its title band – the part the user grabbed and dropped –, pulled into that
    /// screen's working area if it would hang out. Null when no screen shows the title band any
    /// more (monitor unplugged); the caller then falls back to its anchor.
    ///
    /// The screen must NOT be chosen by the popup's full desired height: a tall popup dropped on
    /// a monitor above another reaches into the lower one on paper, and the first match in
    /// enumeration order (often the lower monitor) pulled the popup back down there at the next
    /// data refresh, seconds after the drop.
    /// </summary>
    public static Rectangle? Parked(Point parked, int width, int desiredHeight, int titleHeight,
        IReadOnlyList<(Rectangle Bounds, Rectangle WorkingArea)> screens, double maxHeightShare = 0.85)
    {
        var title = new Rectangle(parked.X, parked.Y, width, Math.Max(1, titleHeight));
        Rectangle? workingArea = null;
        long best = 0;
        foreach (var (bounds, wa) in screens)
        {
            var shown = Rectangle.Intersect(bounds, title);
            var area = (long)shown.Width * shown.Height;
            if (shown.IsEmpty || area <= best) continue;
            best = area;
            workingArea = wa;
        }
        if (workingArea is not { } w) return null;

        // the popup may be taller than what fits (or have grown since it was parked): cap the
        // height and shift it instead of cutting off the footer and the last rows
        var h = Math.Min(desiredHeight, (int)(w.Height * maxHeightShare));
        var x = Math.Clamp(parked.X, w.Left, Math.Max(w.Left, w.Right - width));
        var y = Math.Clamp(parked.Y, w.Top, Math.Max(w.Top, w.Bottom - h));
        return new Rectangle(x, y, width, h);
    }
}
