using TokenWatch.Core;

namespace TokenWatch;

internal sealed class AccountEditDialog : Form
{
    private readonly TrayAppContext _ctx;
    private readonly AccountConfig _acc;
    private readonly bool _isNew;

    private readonly TextBox _name = new();
    private readonly ComboBox _provider = new();
    private readonly ComboBox _auth = new();
    private readonly TextBox _key = new();
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _model = new();
    private readonly NumericUpDown _interval = new();
    private readonly CheckBox _enabled = new();
    private Label _authLabel = new();
    private Label _modelLabel = new();
    private readonly Label _urlHint = new();
    private readonly Label _result = new();
    private readonly Button _testBtn = new();

    /// <summary>null = leave key unchanged</summary>
    public string? PlainKeyIfChanged { get; private set; }

    public AccountEditDialog(TrayAppContext ctx, AccountConfig acc, bool isNew)
    {
        _ctx = ctx;
        _acc = acc;
        _isNew = isNew;

        // coordinates below are at 96 dpi; WinForms scales them for the actual DPI
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = isNew ? "Konto hinzufügen" : $"Konto bearbeiten – {acc.Name}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 380);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BodyFont;

        var y = 16;
        AddRow("Name:", Field(_name, acc.Name, y), ref y);

        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (ProviderKind k in Enum.GetValues<ProviderKind>())
            _provider.Items.Add(ProviderInfo.DisplayName(k));
        _provider.SelectedIndex = (int)acc.Provider;
        _provider.SelectedIndexChanged += (_, _) => UpdateProviderUi();
        AddRow("Anbieter:", Field(_provider, null, y), ref y);

        _auth.DropDownStyle = ComboBoxStyle.DropDownList;
        _auth.Items.Add("API-Key (x-api-key)");
        _auth.Items.Add("OAuth / Bearer-Token");
        _auth.SelectedIndex = (int)acc.Auth;
        _authLabel = AddRow("Auth-Typ:", Field(_auth, null, y), ref y);

        _key.UseSystemPasswordChar = true;
        _key.PlaceholderText = isNew ? "API-Schlüssel einfügen" : "unverändert lassen, wenn leer";
        AddRow("API-Schlüssel:", Field(_key, null, y), ref y);

        AddRow("Basis-URL (optional):", Field(_baseUrl, acc.BaseUrlOverride ?? "", y), ref y);
        _urlHint.Location = new Point(140, y + 2);
        _urlHint.Size = new Size(360, 32);
        _urlHint.ForeColor = Theme.Muted;
        _urlHint.Font = Theme.SmallFont;
        Controls.Add(_urlHint);
        y += 38;

        _model.PlaceholderText = "claude-haiku-4-5-20251001";
        _modelLabel = AddRow("Modell (Ping):", Field(_model, acc.Model, y), ref y);

        _interval.Minimum = 30;
        _interval.Maximum = 3600;
        _interval.Increment = 30;
        _interval.Value = Math.Clamp(acc.PollSeconds, 30, 3600);
        AddRow("Intervall (Sek.):", Field(_interval, null, y), ref y);

        _enabled.Text = "Aktiviert";
        _enabled.Checked = acc.Enabled;
        _enabled.Location = new Point(140, y);
        _enabled.AutoSize = true;
        _enabled.ForeColor = Theme.Text;
        Controls.Add(_enabled);
        y += 34;

        _testBtn.Text = "Verbindung testen";
        _testBtn.Location = new Point(140, y);
        _testBtn.Size = new Size(150, 28);
        _testBtn.FlatStyle = FlatStyle.Flat;
        _testBtn.BackColor = Theme.Card;
        _testBtn.ForeColor = Theme.Text;
        _testBtn.FlatAppearance.BorderColor = Theme.CardBorder;
        _testBtn.Click += async (_, _) => await TestAsync();
        Controls.Add(_testBtn);

        _result.Location = new Point(140, y + 34);
        _result.Size = new Size(360, 40);
        _result.ForeColor = Theme.Muted;
        _result.Font = Theme.SmallFont;
        Controls.Add(_result);

        var ok = new Button
        {
            Text = "Speichern",
            DialogResult = DialogResult.None,
            Location = new Point(330, 336),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Ok,
            ForeColor = Color.Black
        };
        ok.FlatAppearance.BorderColor = Theme.Ok;
        ok.Click += OnSave;
        var cancel = new Button
        {
            Text = "Abbrechen",
            DialogResult = DialogResult.Cancel,
            Location = new Point(420, 336),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Card,
            ForeColor = Theme.Text
        };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        UpdateProviderUi();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkChrome.Apply(this);
    }

    private Control Field(Control c, string? value, int y)
    {
        c.Location = new Point(140, y);
        c.Width = 360;
        if (c is TextBox t)
        {
            t.BackColor = Theme.Card;
            t.ForeColor = Theme.Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            if (value is not null) t.Text = value;
        }
        else if (c is ComboBox cb)
        {
            cb.BackColor = Theme.Card;
            cb.ForeColor = Theme.Text;
            cb.FlatStyle = FlatStyle.Flat;
        }
        else if (c is NumericUpDown n)
        {
            n.BackColor = Theme.Card;
            n.ForeColor = Theme.Text;
        }
        return c;
    }

    private Label AddRow(string label, Control field, ref int y)
    {
        var l = new Label
        {
            Text = label,
            Location = new Point(14, y + 3),
            AutoSize = true,
            ForeColor = Theme.Muted
        };
        Controls.Add(l);
        Controls.Add(field);
        y += 30;
        return l;
    }

    private void UpdateProviderUi()
    {
        var kind = (ProviderKind)_provider.SelectedIndex;
        var anthropic = kind == ProviderKind.Anthropic;
        var needsModel = anthropic || kind == ProviderKind.GenericOpenAi;
        _auth.Visible = anthropic;
        _authLabel.Visible = anthropic;
        _model.Visible = needsModel;
        _modelLabel.Visible = needsModel;
        _modelLabel.Text = anthropic ? "Modell (Ping):" : "Modell (Ping) *:";
        _urlHint.Text = ProviderInfo.BaseUrlHint(kind);
    }

    private async Task TestAsync()
    {
        var cfg = Snapshot(false);
        var key = _key.Text.Trim();
        if (string.IsNullOrEmpty(key)) key = _ctx.Store.PlainKey(_acc);
        if (string.IsNullOrEmpty(key))
        {
            _result.Text = "Bitte zuerst einen API-Schlüssel eintragen.";
            return;
        }
        _testBtn.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = "Abfrage läuft…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var snap = await ProviderInfo.Poller(cfg.Provider).PollAsync(cfg, key, http, default);
            if (snap.Ok)
            {
                _result.ForeColor = Theme.Ok;
                _result.Text = "OK: " + string.Join(" · ", snap.Windows.Select(w =>
                    w.IsBalance
                        ? $"{w.Label} {Formatting.Money(w.Remaining, w.Unit)}"
                        : $"{w.Label} {Formatting.Percent(w.UsedFraction)}"));
            }
            else
            {
                _result.ForeColor = Theme.Crit;
                _result.Text = snap.Error;
            }
        }
        catch (Exception ex)
        {
            _result.ForeColor = Theme.Crit;
            _result.Text = ex.Message;
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }

    private AccountConfig Snapshot(bool _)
    {
        return new AccountConfig
        {
            Id = _acc.Id,
            Name = _name.Text.Trim(),
            Provider = (ProviderKind)_provider.SelectedIndex,
            Auth = (AuthKind)_auth.SelectedIndex,
            ApiKeyProtected = _acc.ApiKeyProtected,
            BaseUrlOverride = string.IsNullOrWhiteSpace(_baseUrl.Text) ? null : _baseUrl.Text.Trim(),
            Model = _model.Text.Trim(),
            PollSeconds = (int)_interval.Value,
            Enabled = _enabled.Checked
        };
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _result.ForeColor = Theme.Crit;
            _result.Text = "Name fehlt.";
            return;
        }
        var key = _key.Text.Trim();
        if (_isNew && string.IsNullOrEmpty(key))
        {
            _result.ForeColor = Theme.Crit;
            _result.Text = "API-Schlüssel fehlt.";
            return;
        }

        var s = Snapshot(true);
        _acc.Name = s.Name;
        _acc.Provider = s.Provider;
        _acc.Auth = s.Auth;
        _acc.BaseUrlOverride = s.BaseUrlOverride;
        _acc.Model = s.Model;
        _acc.PollSeconds = s.PollSeconds;
        _acc.Enabled = s.Enabled;
        PlainKeyIfChanged = string.IsNullOrEmpty(key) ? null : key;

        DialogResult = DialogResult.OK;
        Close();
    }
}
