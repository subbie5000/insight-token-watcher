using System.Drawing.Drawing2D;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Borderless hover popup above the tray. Hides itself when the pointer leaves.</summary>
internal sealed class PopupForm : Form
{
    private const int ContentWidth = 400;
    private readonly TrayAppContext _ctx;
    private readonly Panel _cardsPanel;
    private readonly Label _footer;
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 300 };
    private DateTime _lastInsideUtc = DateTime.MinValue;
    private IReadOnlyList<AccountView> _views = Array.Empty<AccountView>();

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

        var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var title = new Label
        {
            Text = "TokenWatch",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(12, 10)
        };
        header.Controls.Add(title);
        header.Controls.Add(MakeHeaderButton("⟳ Aktualisieren", 210, (_, _) => { _ctx.RefreshNow(); }));
        header.Controls.Add(MakeHeaderButton("⚙ Einstellungen", 305, (_, _) => { _ctx.ShowSettings(); }));
        Controls.Add(header);

        _cardsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Bg
        };
        Controls.Add(_cardsPanel);

        _footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
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

    private Button MakeHeaderButton(string text, int x, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Font = Theme.SmallFont,
            ForeColor = Theme.Muted,
            BackColor = Theme.Card,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(x, 9),
            Size = new Size(94, 24),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Theme.CardBorder;
        b.FlatAppearance.MouseOverBackColor = Theme.Track;
        b.Click += onClick;
        return b;
    }

    public void UpdateData(IReadOnlyList<AccountView> views)
    {
        _views = views;
        _footer.Text = views.Count == 0
            ? "Keine Konten konfiguriert"
            : $"Stand: {DateTime.Now:HH:mm:ss} · {views.Count} Konten";
        if (Visible) Rebuild();
    }

    public void Rebuild()
    {
        _cardsPanel.SuspendLayout();
        _cardsPanel.Controls.Clear();
        var y = 6;
        foreach (var v in _views)
        {
            var card = new AccountCardControl(v, _ctx)
            {
                Width = ContentWidth - 20,
                Location = new Point(8, y)
            };
            _cardsPanel.Controls.Add(card);
            y += card.Height + 8;
        }
        _cardsPanel.ResumeLayout();

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var wanted = y + 40 + 24 + 12; // cards + header + footer + margin
        Height = Math.Min(wanted, (int)(screen.Height * 0.8));
    }

    public void ShowNear(Point cursor)
    {
        if (_views.Count != _cardsPanel.Controls.Count || !Visible) Rebuild();
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Clamp(cursor.X - Width / 2, screen.Left + 4, screen.Right - Width - 4);
        var y = screen.Bottom - Height - 4;
        if (cursor.Y < screen.Top + Height + 60) y = screen.Top + 48; // Taskleiste oben
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
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
