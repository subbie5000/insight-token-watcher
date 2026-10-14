using Microsoft.Win32;
using TokenWatch.Core;

namespace TokenWatch;

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
        Text = "TokenWatch – Einstellungen";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 420);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BodyFont;

        var lbl = new Label
        {
            Text = "Konten (Abfrage jeweils im eigenen Intervall, Standard 60 s):",
            Location = new Point(16, 14),
            AutoSize = true,
            ForeColor = Theme.Muted
        };
        Controls.Add(lbl);

        _list.Location = new Point(16, 38);
        _list.Size = new Size(528, 220);
        _list.BackColor = Theme.Card;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.DoubleClick += (_, _) => EditSelected();
        Controls.Add(_list);

        var y = 268;
        Controls.Add(MakeButton("Neu…", 16, y, (_, _) => AddNew()));
        Controls.Add(MakeButton("Bearbeiten…", 120, y, (_, _) => EditSelected()));
        Controls.Add(MakeButton("Entfernen", 240, y, (_, _) => RemoveSelected()));
        Controls.Add(MakeButton("Testen", 360, y, (_, _) => TestSelected()));

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
        _status.Size = new Size(528, 18);
        _status.ForeColor = Theme.Muted;
        Controls.Add(_status);

        var close = MakeButton("Schließen", 448, y + 124, (_, _) => Close());
        Controls.Add(close);

        RefreshList();
    }

    private Button MakeButton(string text, int x, int y, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(96, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Card,
            ForeColor = Theme.Text
        };
        b.FlatAppearance.BorderColor = Theme.CardBorder;
        b.Click += onClick;
        return b;
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var a in _ctx.Store.Accounts)
        {
            var keyState = string.IsNullOrEmpty(_ctx.Store.PlainKey(a)) ? " – kein Schlüssel!" : "";
            var off = a.Enabled ? "" : " (deaktiviert)";
            _list.Items.Add($"{a.Name} · {ProviderInfo.DisplayName(a.Provider)} · alle {a.PollSeconds} s{keyState}{off}");
        }
    }

    private AccountConfig? Selected()
    {
        var i = _list.SelectedIndex;
        return i >= 0 && i < _ctx.Store.Accounts.Count ? _ctx.Store.Accounts[i] : null;
    }

    private void AddNew()
    {
        var acc = new AccountConfig { Name = "Neues Konto", Provider = ProviderKind.Anthropic };
        using var dlg = new AccountEditDialog(_ctx, acc, isNew: true);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _ctx.Store.UpsertAccount(acc, dlg.PlainKeyIfChanged);
            ApplyAndRefresh();
        }
    }

    private void EditSelected()
    {
        var acc = Selected();
        if (acc is null) { _status.Text = "Bitte zuerst ein Konto markieren."; return; }
        using var dlg = new AccountEditDialog(_ctx, acc, isNew: false);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _ctx.Store.UpsertAccount(acc, dlg.PlainKeyIfChanged);
            ApplyAndRefresh();
        }
    }

    private void RemoveSelected()
    {
        var acc = Selected();
        if (acc is null) return;
        if (MessageBox.Show(this, $"„{acc.Name}“ wirklich entfernen?", "TokenWatch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _ctx.Store.RemoveAccount(acc.Id);
        ApplyAndRefresh();
    }

    private async void TestSelected()
    {
        var acc = Selected();
        if (acc is null) return;
        _status.Text = $"Teste {acc.Name}…";
        var key = _ctx.Store.PlainKey(acc);
        if (string.IsNullOrEmpty(key))
        {
            _status.Text = "Kein API-Schlüssel hinterlegt.";
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var snap = await ProviderInfo.Poller(acc.Provider).PollAsync(acc, key, http, default);
            _status.Text = snap.Ok
                ? $"OK – {snap.Windows.Count} Fenster: " + string.Join(" · ",
                    snap.Windows.Select(w => w.IsBalance
                        ? $"{w.Label} {Formatting.Money(w.Remaining, w.Unit)}"
                        : $"{w.Label} {Formatting.Percent(w.UsedFraction)}"))
                : "Fehler: " + snap.Error;
        }
        catch (Exception ex)
        {
            _status.Text = "Fehler: " + ex.Message;
        }
    }

    private void ApplyAndRefresh()
    {
        _ctx.Store.Settings.ShowCritBalloons = _chkBalloons.Checked;
        SetAutostart(_chkAutostart.Checked);
        _ctx.Store.Save();
        RefreshList();
        _ctx.ReloadAccounts();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _ctx.Store.Settings.ShowCritBalloons = _chkBalloons.Checked;
        SetAutostart(_chkAutostart.Checked);
        _ctx.Store.Save();
        base.OnFormClosing(e);
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue("TokenWatch") is string s &&
               s.Trim('"').Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            if (on) key.SetValue("TokenWatch", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("TokenWatch", false);
            _ctx.Store.Settings.Autostart = on;
        }
        catch (Exception ex)
        {
            _status.Text = "Autostart-Fehler: " + ex.Message;
        }
    }
}
