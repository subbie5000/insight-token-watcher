using System.Text.Json;
using InsightTokenWatcher.Core;

namespace InsightTokenWatcher.Core;

/// <summary>Headless import: InsightTokenWatcher.exe --import accounts.json
/// Reads a plaintext file, encrypts keys via DPAPI into config.json (one atomic write)
/// and writes &lt;file&gt;.result.json as proof. Does NOT start the GUI.
/// Accounts are matched by name; settings the user changed (enabled, interval) survive
/// unless the file sets them explicitly. With "prune": true, previously imported accounts
/// missing from the file are removed – manually added accounts are never touched.</summary>
public static class ConfigImporter
{
    public const string ImportSource = "import";

    public static int Run(string path)
    {
        var resultFile = path + ".result.json";
        try
        {
            var file = PlainAccountFile.Load(path);
            var store = new ConfigStore();
            int added = 0, updated = 0, removed = 0;
            var errors = new List<string>();
            var keptIds = new HashSet<Guid>();

            foreach (var dto in file.Accounts)
            {
                var (cfg, key, error) = file.ToConfig(dto);
                if (error is not null)
                {
                    errors.Add(error);
                    continue;
                }
                cfg.Source = ImportSource;

                // stable external id first (survives renames at the source), then name
                var existing = (cfg.ExternalId is not null
                                   ? store.Accounts.FirstOrDefault(a => a.ExternalId == cfg.ExternalId)
                                   : null)
                               ?? store.Accounts.FirstOrDefault(a =>
                                   a.Name.Equals(cfg.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    cfg.Id = existing.Id; // same id keeps the DPAPI entropy and the engine history
                    cfg.ApiKeyProtected = existing.ApiKeyProtected;
                    if (dto.Enabled is null) cfg.Enabled = existing.Enabled;
                    if (dto.PollSeconds is null) cfg.PollSeconds = existing.PollSeconds;
                    var keyChanged = store.PlainKey(existing) != key;
                    store.UpsertAccount(cfg, keyChanged ? key : null, save: false);
                    updated++;
                }
                else
                {
                    cfg.Id = Guid.NewGuid();
                    store.UpsertAccount(cfg, key, save: false);
                    added++;
                }
                keptIds.Add(cfg.Id);
            }

            if (file.Prune && errors.Count == 0)
            {
                foreach (var stale in store.Accounts
                             .Where(a => a.Source == ImportSource && !keptIds.Contains(a.Id))
                             .ToList())
                {
                    store.RemoveAccount(stale.Id, save: false);
                    removed++;
                }
            }

            store.Save();

            File.WriteAllText(resultFile, JsonSerializer.Serialize(new
            {
                ok = errors.Count == 0,
                added,
                updated,
                removed,
                errors,
                totalAccounts = store.Accounts.Count
            }, new JsonSerializerOptions { WriteIndented = true }));
            return errors.Count == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(new
            {
                ok = false,
                added = 0,
                updated = 0,
                removed = 0,
                errors = new[] { ex.GetType().Name + ": " + ex.Message }
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
