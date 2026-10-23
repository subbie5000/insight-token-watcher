using System.Reflection;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher;

/// <summary>Self-test for the reported bug: toggling "Aktiviert" in the edit dialog and saving
/// must persist Enabled to config.json. Runs the exact SettingsForm edit path
/// (AccountEditDialog → OnSave → UpsertAccount → Save) without showing any window.
/// Exit 0 = persisted correctly; exit 2 = bug reproduced. Writes a proof file.</summary>
internal static class UiSaveRepro
{
    public static int Run(string accountName, string? appDirOverride = null)
    {
        if (appDirOverride is not null) ConfigStore.AppDirOverride = appDirOverride;
        var report = new List<string>();
        int rc = 0;
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();

            var store = new ConfigStore();
            var acc = store.Accounts.FirstOrDefault(a =>
                a.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
            if (acc is null)
            {
                report.Add("Konto nicht gefunden: " + accountName);
                report.Add("Vorhanden: " + string.Join(", ", store.Accounts.Select(a => a.Name)));
                rc = 3;
            }
            else
            {
                var before = acc.Enabled;
                var target = !before;
                report.Add($"vorher: Enabled={before}");

                // 1) Dialog bauen (exakt wie SettingsForm.EditSelected)
                using var dlg = new AccountEditDialog(null!, acc, isNew: false);

                // 2) Checkbox im Dialog togglen (wie ein Klick)
                var cbField = typeof(AccountEditDialog).GetField("_enabled",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                ((CheckBox)cbField.GetValue(dlg)!).Checked = target;
                report.Add($"Checkbox gesetzt auf: {target}");

                // 3) OnSave aufrufen (wie Klick auf Speichern)
                var onSave = typeof(AccountEditDialog).GetMethod("OnSave",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                onSave.Invoke(dlg, new object?[] { null, EventArgs.Empty });
                report.Add($"nach OnSave: acc.Enabled={acc.Enabled} (DialogResult={dlg.DialogResult})");

                // 4) Persistieren (exakt wie SettingsForm)
                store.UpsertAccount(acc, null);

                // 5) Datei frisch lesen – was die App beim nächsten Öffnen sieht
                var reread = new ConfigStore();
                var after = reread.Accounts.First(a => a.Id == acc.Id).Enabled;
                report.Add($"Datei neu gelesen: Enabled={after}");
                rc = after == target ? 0 : 2;
                report.Add(rc == 0 ? "ERGEBNIS: korrekt persistiert" : "ERGEBNIS: BUG – Wert zurückgesprungen");

                // 6) zurücksetzen
                acc.Enabled = before;
                store.UpsertAccount(acc, null);
                var back = new ConfigStore().Accounts.First(a => a.Id == acc.Id).Enabled;
                report.Add($"zurückgesetzt: Enabled={back}");
            }
        }
        catch (Exception ex)
        {
            report.Add("EXCEPTION: " + ex);
            rc = 1;
        }
        File.WriteAllText(
            Path.Combine(ConfigStore.AppDir, "test-toggle.result.txt"), string.Join("\n", report));
        foreach (var line in report) Console.Out.WriteLine(line);
        return rc;
    }
}
