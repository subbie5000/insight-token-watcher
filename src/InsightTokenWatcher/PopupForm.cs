using System.Runtime.InteropServices;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher;

/// <summary>Borderless, non-activating popup host near the tray. Auto-hides unless pinned.</summary>
internal sealed class PopupForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    private static readonly bool IsWin11 = Environment.OSVersion.Version.Build >= 22000;

    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _repaint = new() { Interval = 20000 }; // countdowns
    private Point _anchor;
    private DateTime _lastInsideUtc;
    private int _glassTick;

    /// <summary>User-parked position (drag). When set and visible on any screen, it wins over the tray anchor.</summary>
    public Point? PreferredLocation { get; set; }

    public PopupCanvas Canvas { get; } = new() { Dock = DockStyle.Fill };

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Canvas.DrawOwnBorder = !IsWin11;
        Controls.Add(Canvas);
        _hideTimer.Tick += (_, _) =>
        {
            CheckHide();
            // while visible: about once a second, has the desktop around the popup changed?
            if (Visible && Canvas.GlassActive && ++_glassTick % 7 == 0) AdaptGlass(around: true);
        };
        _repaint.Tick += (_, _) => Canvas.Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (no Alt-Tab entry)
            if (IsWin11) cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!IsWin11)
        {
            Canvas.GlassActive = false;
            return;
        }
        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = ColorTranslator.ToWin32(Theme.CardBorder);
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var log = "";
        Canvas.GlassActive = Glass.Enabled && Glass.Apply(Handle, out log); // refused → the opaque look stays
        GlassLog = log;
    }

    /// <summary>Switches the glass look. The backdrop belongs to the window, so it gets a new one.</summary>
    public void SetGlass(bool on)
    {
        if (Glass.Enabled == on) return;
        Glass.Enabled = on;
        if (!IsHandleCreated) return;
        HidePopup();
        RecreateHandle();
    }

    /// <summary>What the backdrop request returned (diagnostics).</summary>
    public string GlassLog { get; private set; } = "";

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsWin11 || Width <= 0 || Height <= 0) return;
        using var path = Gfx.Round(new RectangleF(0, 0, Width, Height), Theme.S(10));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    public void SetData(IReadOnlyList<AccountView> views)
    {
        Canvas.SetData(views);
        if (Visible) ApplyBounds();
    }

    /// <summary>Show anchored at the tray-icon position. Repositions only if not yet visible.</summary>
    public void ShowAt(Point anchor)
    {
        if (!Visible)
        {
            _anchor = anchor;
            ApplyBounds();
            // still hidden: the screen under the future bounds is exactly what will be behind the glass
            if (Canvas.GlassActive) AdaptGlass(around: false);
            Show();
        }
        _lastInsideUtc = DateTime.UtcNow;
        _hideTimer.Start();
        _repaint.Start();
    }

    public void KeepAlive() => _lastInsideUtc = DateTime.UtcNow;

    /// <summary>
    /// The user dropped the popup at <paramref name="dropped"/>. If it hangs out of the screen
    /// there, it is pulled in NOW, while the user is looking at it – not at the next data
    /// refresh seconds later. Returns where it really sits; that is what gets remembered.
    /// </summary>
    public Point Park(Point dropped)
    {
        PreferredLocation = dropped;
        ApplyBounds();
        PreferredLocation = Location;
        return Location;
    }

    /// <summary>
    /// Glass opacity follows the brightness behind the popup. Hidden popup: measured on its
    /// future area. Visible popup: on a ring just outside it (its own pixels must not count).
    /// </summary>
    private void AdaptGlass(bool around)
    {
        var b = Bounds;
        int off = Theme.S(12), th = Theme.S(14); // beyond the drop shadow
        var areas = around
            ? new[]
            {
                new Rectangle(b.Left, b.Top - off - th, b.Width, th), new Rectangle(b.Left, b.Bottom + off, b.Width, th),
                new Rectangle(b.Left - off - th, b.Top, th, b.Height), new Rectangle(b.Right + off, b.Top, th, b.Height)
            }
            : new[] { b };
        // unreadable screen (locked, secure desktop): keep what we have
        if (Glass.ScreenLuminance(areas) is { } luminance && Glass.Adapt(luminance))
            Canvas.GlassAlphasChanged();
    }

    public void HidePopup()
    {
        _hideTimer.Stop();
        _repaint.Stop();
        Canvas.Pinned = false;
        Canvas.ResetHover();
        Hide();
    }

    private void ApplyBounds()
    {
        if (Canvas.IsDragging) return; // don't yank the form mid-drag
        var w = Theme.S(Canvas.LogicalWidth);

        // parked by the user via drag: keep that spot (on the monitor it was dropped on) unless
        // the monitor is gone
        if (PreferredLocation is { } p)
        {
            var screens = Screen.AllScreens.Select(s => (s.Bounds, s.WorkingArea)).ToList();
            if (PopupPlacement.Parked(p, w, Canvas.DesiredHeight, Canvas.TitleBandHeight, screens) is { } parked)
            {
                SetBounds(parked.X, parked.Y, parked.Width, parked.Height);
                return;
            }
        }

        var screen = Screen.FromPoint(_anchor);
        var wa = screen.WorkingArea;
        var full = screen.Bounds;
        var h = Math.Min(Canvas.DesiredHeight, (int)(wa.Height * 0.85));
        var gap = Theme.S(10);

        int x, y;
        if (wa.Bottom < full.Bottom) { x = _anchor.X - w / 2; y = wa.Bottom - h - gap; }   // taskbar bottom
        else if (wa.Top > full.Top) { x = _anchor.X - w / 2; y = wa.Top + gap; }           // taskbar top
        else if (wa.Left > full.Left) { x = wa.Left + gap; y = _anchor.Y - h / 2; }        // taskbar left
        else if (wa.Right < full.Right) { x = wa.Right - w - gap; y = _anchor.Y - h / 2; } // taskbar right
        else { x = _anchor.X - w / 2; y = _anchor.Y - h - gap; }                           // auto-hide taskbar

        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - w - gap));
        y = Math.Clamp(y, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - h - gap));
        SetBounds(x, y, w, h);
    }

    private void CheckHide()
    {
        if (Canvas.Pinned || Canvas.IsDragging)
        {
            _lastInsideUtc = DateTime.UtcNow;
            return;
        }
        var p = Cursor.Position;
        // popup plus a bridge down to the tray icon, so moving from icon to popup never closes it
        var anchorBox = new Rectangle(_anchor.X - Theme.S(30), _anchor.Y - Theme.S(30), Theme.S(60), Theme.S(60));
        var zone = Rectangle.Union(Bounds, anchorBox);
        zone.Inflate(Theme.S(8), Theme.S(8));
        if (zone.Contains(p))
        {
            _lastInsideUtc = DateTime.UtcNow;
            return;
        }
        if ((DateTime.UtcNow - _lastInsideUtc).TotalMilliseconds > 450)
            HidePopup();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _repaint.Dispose();
        }
        base.Dispose(disposing);
    }
}
