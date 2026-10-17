using Avalonia;
using Avalonia.Layout;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Linux;

/// <summary>Account add/edit dialog – same fields as the Windows settings.</summary>
internal sealed class AccountDialog : Window
{
    private readonly TextBox _name = new() { Watermark = "z. B. Anthropic 1" };
    private readonly ComboBox _provider = new();
    private readonly ComboBox _auth = new();
    private readonly TextBox _key = new() { Watermark = "API-Key oder Token" };
    private readonly TextBox _baseUrl = new() { Watermark = "leer = Standard-Endpunkt" };
    private readonly TextBox _model = new() { Watermark = "leer = Standard-Modell" };
    private readonly NumericUpDown _poll = new() { Minimum = 10, Maximum = 3600, Value = 60 };
    private readonly CheckBox _enabled = new() { Content = "Konto aktiv", IsChecked = true };

    public AccountConfig? Result { get; private set; }

    public AccountDialog(AccountConfig? existing, string? existingKey)
    {
        Title = existing is null ? "Konto hinzufügen" : "Konto bearbeiten";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var k in Enum.GetValues<ProviderKind>())
            _provider.Items.Add(ProviderInfo.DisplayName(k));
        _provider.SelectedIndex = (int)(existing?.Provider ?? ProviderKind.Anthropic);
        _auth.Items.Add("x-api-key");
        _auth.Items.Add("Bearer (OAuth/Setup-Token)");
        _auth.SelectedIndex = existing?.Auth == AuthKind.Bearer ? 1 : 0;
        if (existing is not null)
        {
            _name.Text = existing.Name;
            _key.Text = existingKey ?? "";
            _baseUrl.Text = existing.BaseUrlOverride ?? "";
            _model.Text = existing.Model;
            _poll.Value = existing.PollSeconds;
            _enabled.IsChecked = existing.Enabled;
        }

        var ok = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => Save(existing);

        Content = new StackPanel { Spacing = 10, Margin = new Thickness(16), Children =
        {
            Label("Name"), _name,
            Label("Anbieter"), _provider,
            Label("Auth"), _auth,
            Label("API-Key / Token"), _key,
            Label("Basis-URL (optional)"), _baseUrl,
            Label("Modell (optional)"), _model,
            Label("Messintervall (Sekunden)"), _poll,
            _enabled,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { ok, cancel } }
        } };
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Ui.FaintBrush };

    private void Save(AccountConfig? existing)
    {
        var name = _name.Text?.Trim() ?? "";
        var key = _key.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
        {
            _name.Focus();
            return;
        }
        Result = existing ?? new AccountConfig();
        Result.Name = name;
        Result.Provider = (ProviderKind)_provider.SelectedIndex;
        Result.Auth = _auth.SelectedIndex == 1 ? AuthKind.Bearer : AuthKind.ApiKeyHeader;
        Result.BaseUrlOverride = string.IsNullOrWhiteSpace(_baseUrl.Text) ? null : _baseUrl.Text.Trim();
        Result.Model = _model.Text?.Trim() ?? "";
        Result.PollSeconds = (int)(_poll.Value ?? 60);
        Result.Enabled = _enabled.IsChecked == true;
        // the plain key rides along on a separate property; the host encrypts it via ConfigStore
        Tag = key;
        Close();
    }
}

/// <summary>Settings window: account list + options. Changes apply immediately.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly EngineHost _host;
    private readonly ListBox _list = new();
    private readonly CheckBox _balloons = new() { Content = "Warnmeldungen (Balloons)" };
    private readonly CheckBox _autostart = new() { Content = "Mit Anmeldung starten" };
    private readonly NumericUpDown _burnWindow = new() { Minimum = 15, Maximum = 480 };
    private readonly TextBlock _status = new() { Foreground = Ui.FaintBrush, FontSize = 11.5 };

    public SettingsWindow(EngineHost host)
    {
        _host = host;
        Title = "Insight Token Watcher – Einstellungen";
        Width = 480;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _balloons.IsChecked = host.Store.Settings.ShowCritBalloons;
        _autostart.IsChecked = Autostart.IsEnabled();
        _burnWindow.Value = host.Store.Settings.BurnWindowMinutes;

        var add = new Button { Content = "Hinzufügen" };
        var edit = new Button { Content = "Bearbeiten" };
        var remove = new Button { Content = "Entfernen" };
        add.Click += (_, _) => AddEdit(null);
        edit.Click += (_, _) => AddEdit(Selected());
        remove.Click += (_, _) => Remove();

        _balloons.Click += (_, _) => SaveSettings();
        _autostart.Click += (_, _) => SaveSettings();
        _burnWindow.ValueChanged += (_, _) => SaveSettings();

        Content = new StackPanel { Spacing = 10, Margin = new Thickness(16), Children =
        {
            _list,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { add, edit, remove } },
            _balloons, _autostart,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = "Prognose-Zeitfenster (Min):", Foreground = Ui.MutedBrush, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                _burnWindow
            } },
            _status
        } };
        ApplyList();
    }

    private AccountConfig? Selected() => _list.SelectedItem as AccountConfig;

    private void ApplyList()
    {
        _list.ItemsSource = _host.Store.Accounts
            .OrderBy(a => a.Provider).ThenBy(a => a.Name)
            .Select(a => $"{a.Name} · {ProviderInfo.DisplayName(a.Provider)}"
                         + (a.Source == ConfigImporter.ImportSource ? " · per Import verwaltet" : "")
                         + (a.Enabled ? "" : " · deaktiviert"))
            .ToList();
    }

    private void AddEdit(AccountConfig? acc)
    {
        var key = acc is null ? "" : _host.Store.PlainKey(acc);
        var dlg = new AccountDialog(acc, key);
        dlg.ShowDialog(this);
        Dispatcher.UIThread.Post(() =>
        {
            if (dlg.Result is not { } r) return;
            var plain = dlg.Tag as string;
            _host.Store.UpsertAccount(r, string.IsNullOrEmpty(plain) ? null : plain);
            _host.ReconfigureAccounts();
            ApplyList();
            _status.Text = "Gespeichert";
        });
    }

    private void Remove()
    {
        var acc = Selected();
        if (acc is null) return;
        _host.Store.RemoveAccount(acc.Id);
        _host.ReconfigureAccounts();
        ApplyList();
        _status.Text = "Entfernt";
    }

    private void SaveSettings()
    {
        _host.Store.Settings.ShowCritBalloons = _balloons.IsChecked == true;
        _host.Store.Settings.BurnWindowMinutes = (int)(_burnWindow.Value ?? 60);
        Autostart.Set(_autostart.IsChecked == true);
        _host.Store.Settings.Autostart = _autostart.IsChecked == true;
        _host.Store.Save();
        _host.ReconfigureAccounts();
        _status.Text = "Gespeichert";
    }
}
