using System.Drawing.Drawing2D;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Borderless hover popup above the tray. Hides itself when the pointer leaves.</summary>
internal sealed class PopupForm : Form
{
    private const int ContentWidth = 420;
    private readonly TrayAppContext _ctx;
    private readonly Panel _cardsPanel;
    private readonly Label _footer;
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 300 };
    private DateTime _lastInsideUtc = DateTime.MinValue;
    private IReadOnlyList<AccountView> _views = Array.Empty<AccountView>();
    private bool _dirty = true;
    private string _lastSignature = "";

    public PopupForm(TrayAppContext ctx)
    {
        _ctx = ctx;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Bg;
        Width = ContentWidth + 16;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;

        var s = Theme.DpiScale;
        var headerH = (int)(46 * s);
        var header = new Panel { Dock = DockStyle.Top, Height = headerH, BackColor = Theme.Bg };
        var title = new Label
        {
            Text = "TokenWatch",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point((int)(14 * s), (int)(8 * s))
        };
        var subtitle = new Label
        {
            Text = "LLM-Quoten live",
            Font = Theme.SmallFont,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point((int)(15 * s), (int)(27 * s))
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(MakeHeaderButton("⟳ Aktualisieren", (int)(212 * s), s, (_, _) => { _ctx.RefreshNow(); }));
        header.Controls.Add(MakeHeaderButton("⚙ Einstellungen", (int)(312 * s), s, (_, _) => { _ctx.ShowSettings(); }));
        Controls.Add(header);

        _cardsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 0, 0, (int)(8 * s))
        };
        Controls.Add(_cardsPanel);

        _footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = (int)(26 * s),
            Font = Theme.SmallFont,
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.Bg
        };
        Controls.Add(_footer);

        _cardsPanel.BringToFront();
        header.BringToFront();
        _footer.BringToFront();

        _hideTimer.Tick += (_, _) => CheckCursorOutside();
    }

    private Button MakeHeaderButton(string text, int x, float s, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Font = Theme.SmallFont,
            ForeColor = Theme.Muted,
            BackColor = Theme.Card,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(x, (int)(10 * s)),
            Size = new Size((int)(96 * s), (int)(25 * s)),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Theme.CardBorder;
        b.FlatAppearance.MouseOverBackColor = Theme.Track;
        b.Click += onClick;
        return b;
    }

    private static string Signature(IReadOnlyList<AccountView> views)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in views)
        {
            sb.Append(v.Config.Id).Append('|').Append(v.Health).Append('|').Append(v.Polling).Append('|');
            if (v.Snapshot is not null)
            {
                sb.Append(v.Snapshot.Ok).Append('|').Append(v.Snapshot.AtUtc.Ticks).Append('|');
                foreach (var w in v.Snapshot.Windows)
                    sb.Append(w.Key).Append(':').Append(w.UsedFraction).Append(':').Append(w.Remaining).Append(';');
            }
        }
        return sb.ToString();
    }

    public void UpdateData(IReadOnlyList<AccountView> views)
    {
        _views = views;
        _footer.Text = views.Count == 0
            ? "Keine Konten konfiguriert"
            : $"Stand: {DateTime.Now:HH:mm:ss} · {views.Count} Konten · Klick auf Karte = neu messen";
        var sig = Signature(views);
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            _dirty = true;
            if (Visible) Rebuild();
        }
    }

    public void Rebuild()
    {
        _dirty = false;
        _cardsPanel.SuspendLayout();
        _cardsPanel.Controls.Clear();
        var s = Theme.DpiScale;
        var y = (int)(6 * s);
        foreach (var v in _views)
        {
            var card = new AccountCardControl(v, _ctx)
            {
                Width = (int)((ContentWidth - 20) * s),
                Location = new Point((int)(8 * s), y)
            };
            _cardsPanel.Controls.Add(card);
            y += card.Height + (int)(8 * s);
        }
        _cardsPanel.ResumeLayout();

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var chrome = (int)((46 + 26) * s); // header + footer
        var wanted = y + chrome + (int)(14 * s);
        Height = Math.Min(wanted, (int)(screen.Height * 0.8));
        Width = (int)((ContentWidth + 16) * s);
    }

    public void ShowNear(Point cursor)
    {
        if (_dirty || !Visible) Rebuild();
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Clamp(cursor.X - Width / 2, screen.Left + 4, screen.Right - Width - 4);
        var y = screen.Bottom - Height - 4;
        if (cursor.Y < screen.Top + Height + 60) y = screen.Top + 48; // taskbar on top
        Location = new Point(x, y);
        if (!Visible)
        {
            Show();
            _hideTimer.Start();
        }
        _lastInsideUtc = DateTime.UtcNow;
    }

    public void HidePopup()
    {
        _hideTimer.Stop();
        Hide();
    }

    private void CheckCursorOutside()
    {
        var pos = Cursor.Position;
        var bounds = Bounds;
        // 48 px slack at the bottom: that is where the tray icon sits
        var inflated = new Rectangle(bounds.Left - 16, bounds.Top - 16,
            bounds.Width + 32, bounds.Height + 32 + 48);
        if (inflated.Contains(pos))
        {
            _lastInsideUtc = DateTime.UtcNow;
            return;
        }
        if ((DateTime.UtcNow - _lastInsideUtc).TotalMilliseconds > 500)
            HidePopup();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width > 2 && Height > 2)
            Region = new Region(AccountCardControl.Rounded(new Rectangle(0, 0, Width, Height), 12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawPath(pen, AccountCardControl.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 12));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }
}
