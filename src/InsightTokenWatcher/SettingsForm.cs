using Microsoft.Win32;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher;

internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly TrayAppContext _ctx;
    private readonly ListBox _list = new();
    private readonly CheckBox _chkBalloons = new();
    private readonly CheckBox _chkAutostart = new();
    private readonly Label _status = new();

    public SettingsForm(TrayAppContext ctx)
    {
        _ctx = ctx;
        // coordinates below are at 96 dpi; WinForms scales them for the actual DPI
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Insight Token Watcher – Einstellungen";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(580, 470);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BodyFont;

        Controls.Add(new Label
        {
            Text = "Konten",
            Font = Theme.TitleFont,
            Location = new Point(16, 12),
            AutoSize = true,
            ForeColor = Theme.Text
        });
        Controls.Add(new Label
        {
            Text = "Doppelklick = bearbeiten · jedes Konto wird im eigenen Intervall abgefragt",
            Location = new Point(16, 36),
            AutoSize = true,
            ForeColor = Theme.Muted
        });

        _list.Location = new Point(16, 60);
        _list.Size = new Size(548, 256);
        _list.BackColor = Theme.Card;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 40;
        _list.IntegralHeight = false;
        _list.DrawItem += DrawListItem;
        _list.DoubleClick += (_, _) => EditSelected();
        Controls.Add(_list);

        var y = 328;
        Controls.Add(MakeButton("Neu…", 16, y, (_, _) => AddNew(), primary: true));
        Controls.Add(MakeButton("Bearbeiten…", 122, y, (_, _) => EditSelected()));
        Controls.Add(MakeButton("Entfernen", 228, y, (_, _) => RemoveSelected()));
        Controls.Add(MakeButton("Testen", 334, y, (_, _) => TestSelected()));

        _chkBalloons.Text = "Warnung einblenden, wenn ein Konto kritisch wird";
        _chkBalloons.Location = new Point(16, y + 44);
        _chkBalloons.AutoSize = true;
        _chkBalloons.ForeColor = Theme.Text;
        _chkBalloons.Checked = _ctx.Store.Settings.ShowCritBalloons;
        Controls.Add(_chkBalloons);

        _chkAutostart.Text = "Mit Windows starten";
        _chkAutostart.Location = new Point(16, y + 70);
        _chkAutostart.AutoSize = true;
        _chkAutostart.ForeColor = Theme.Text;
        _chkAutostart.Checked = IsAutostart();
        Controls.Add(_chkAutostart);

        _status.Location = new Point(16, y + 100);
        _status.Size = new Size(440, 34);
        _status.ForeColor = Theme.Muted;
        Controls.Add(_status);

        Controls.Add(MakeButton("Schließen", 468, y + 102, (_, _) => Close()));

        RefreshList();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkChrome.Apply(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // owner-drawn item height is not auto-scaled – derive it from the (DPI-scaled) fonts
        _list.ItemHeight = Math.Max(40 * DeviceDpi / 96, Theme.NameFont.Height + Theme.SmallFont.Height + 10);
    }

    private static Button MakeButton(string text, int x, int y, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Theme.Ok : Theme.Card,
            ForeColor = primary ? Color.Black : Theme.Text,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = primary ? Theme.Ok : Theme.CardBorder;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Theme.Ok, 0.2f) : Theme.ButtonHover;
        b.Click += onClick;
        return b;
    }

    private void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var acc = (AccountConfig)_list.Items[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var r = e.Bounds;

        using (var bg = new SolidBrush(selected ? Theme.ButtonHover : Theme.Card))
            g.FillRectangle(bg, r);
        if (selected)
            using (var accent = new SolidBrush(Theme.Ok))
                g.FillRectangle(accent, r.X, r.Y + 6, 3, r.Height - 12);

        var dot = (int)(r.Height * 0.22);
        using (var b = new SolidBrush(acc.Enabled ? Theme.ProviderColor(acc.Provider) : Theme.Stale))
            g.FillEllipse(b, r.X + 14, r.Y + (r.Height - dot) / 2, dot, dot);

        var textX = r.X + 14 + dot + 12;
        var half = r.Height / 2;
        TextRenderer.DrawText(g, acc.Name, Theme.NameFont,
            new Rectangle(textX, r.Y + 3, r.Width - textX - 8, half),
            acc.Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var missingKey = string.IsNullOrEmpty(_ctx.Store.PlainKey(acc));
        var sub = $"{ProviderInfo.DisplayName(acc.Provider)} · alle {acc.PollSeconds} s" +
                  (acc.Source == ConfigImporter.ImportSource ? " · per Import verwaltet" : "") +
                  (acc.Enabled ? "" : " · deaktiviert");
        TextRenderer.DrawText(g, sub + (missingKey ? " · kein Schlüssel!" : ""), Theme.SmallFont,
            new Rectangle(textX, r.Y + half, r.Width - textX - 8, half - 3),
            missingKey ? Theme.Crit : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        using var pen = new Pen(Theme.Divider);
        g.DrawLine(pen, r.X + 8, r.Bottom - 1, r.Right - 8, r.Bottom - 1);
    }

    private void RefreshList()
    {
        var selectedId = (_list.SelectedItem as AccountConfig)?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _ctx.Store.Accounts
                     .OrderBy(a => Array.IndexOf(PollingEngine.DisplayOrder, a.Provider))
                     .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            _list.Items.Add(a);
        _list.EndUpdate();
        if (selectedId is not null)
            for (var i = 0; i < _list.Items.Count; i++)
                if (((AccountConfig)_list.Items[i]).Id == selectedId)
                    _list.SelectedIndex = i;
    }

    private AccountConfig? Selected() => _list.SelectedItem as AccountConfig;

    private void AddNew()
    {
        var acc = new AccountConfig { Name = "Neues Konto", Provider = ProviderKind.Anthropic };
        using var dlg = new AccountEditDialog(_ctx, acc, isNew: true);
        if (dlg.ShowDialog(this) == DialogResult.OK && TrySave(() => _ctx.Store.UpsertAccount(acc, dlg.PlainKeyIfChanged)))
            ApplyAndRefresh();
    }

    private void EditSelected()
    {
        var acc = Selected();
        if (acc is null)
        {
            _status.Text = "Bitte zuerst ein Konto markieren.";
            return;
        }
        using var dlg = new AccountEditDialog(_ctx, acc, isNew: false);
        if (dlg.ShowDialog(this) == DialogResult.OK && TrySave(() => _ctx.Store.UpsertAccount(acc, dlg.PlainKeyIfChanged)))
            ApplyAndRefresh();
    }

    private void RemoveSelected()
    {
        var acc = Selected();
        if (acc is null) return;
        if (MessageBox.Show(this, $"„{acc.Name}“ wirklich entfernen?", "Insight Token Watcher",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (TrySave(() => _ctx.Store.RemoveAccount(acc.Id)))
            ApplyAndRefresh();
    }

    private async void TestSelected()
    {
        var acc = Selected();
        if (acc is null) return;
        _status.ForeColor = Theme.Muted;
        _status.Text = $"Teste {acc.Name} …";
        var key = _ctx.Store.PlainKey(acc);
        if (string.IsNullOrEmpty(key))
        {
            _status.ForeColor = Theme.Crit;
            _status.Text = "Kein API-Schlüssel hinterlegt.";
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var snap = await ProviderInfo.Poller(acc.Provider).PollAsync(acc, key, http, default);
            _status.ForeColor = snap.Ok ? Theme.Ok : Theme.Crit;
            _status.Text = snap.Ok
                ? "OK – " + string.Join(" · ", snap.Windows.Select(w => w.IsBalance || w.IsInfoOnly
                    ? $"{w.Label} {Formatting.Money(w.Remaining, w.Unit)}"
                    : $"{w.Label} {Formatting.Percent(w.UsedFraction)}"))
                : "Fehler: " + snap.Error;
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.Crit;
            _status.Text = "Fehler: " + ex.Message;
        }
    }

    private bool TrySave(Action save)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Insight Token Watcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ApplyAndRefresh()
    {
        _ctx.Store.Settings.ShowCritBalloons = _chkBalloons.Checked;
        SetAutostart(_chkAutostart.Checked);
        TrySave(_ctx.Store.Save);
        RefreshList();
        _ctx.ReloadAccounts();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var changed = _ctx.Store.Settings.ShowCritBalloons != _chkBalloons.Checked ||
                      _ctx.Store.Settings.Autostart != _chkAutostart.Checked;
        _ctx.Store.Settings.ShowCritBalloons = _chkBalloons.Checked;
        SetAutostart(_chkAutostart.Checked);
        if (changed) TrySave(_ctx.Store.Save);
        base.OnFormClosing(e);
    }

    private const string AutostartValue = "InsightTokenWatcher";
    private const string LegacyAutostartValue = "TokenWatch";

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        foreach (var name in new[] { AutostartValue, LegacyAutostartValue })
            if (key?.GetValue(name) is string s &&
                s.Trim('"').Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            key.DeleteValue(LegacyAutostartValue, false); // renamed with the app
            if (on) key.SetValue(AutostartValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(AutostartValue, false);
            _ctx.Store.Settings.Autostart = on;
        }
        catch (Exception ex)
        {
            _status.Text = "Autostart-Fehler: " + ex.Message;
        }
    }
}
