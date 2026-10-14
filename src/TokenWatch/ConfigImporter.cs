using System.Text.Json;
using TokenWatch.Core;

namespace TokenWatch;

/// <summary>Headless import: TokenWatch.exe --import accounts.json
/// Reads a plaintext file, encrypts keys via DPAPI into config.json
/// and writes &lt;file&gt;.result.json as proof. Does NOT start the GUI.</summary>
internal static class ConfigImporter
{
    public static int Run(string path)
    {
        var resultFile = path + ".result.json";
        try
        {
            var file = PlainAccountFile.Load(path);
            var store = new ConfigStore();
            var added = 0;
            var updated = 0;
            var errors = new List<string>();

            foreach (var dto in file.Accounts)
            {
                var (cfg, key, error) = file.ToConfig(dto);
                if (error is not null) { errors.Add(error); continue; }

                var existing = store.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(cfg.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    cfg.Id = existing.Id; // gleiche ID => DPAPI-Entropie bleibt stabil
                    cfg.ApiKeyProtected = existing.ApiKeyProtected;
                    store.UpsertAccount(cfg, key);
                    updated++;
                }
                else
                {
                    cfg.Id = Guid.NewGuid();
                    store.UpsertAccount(cfg, key);
                    added++;
                }
            }

            File.WriteAllText(resultFile, JsonSerializer.Serialize(new
            {
                ok = errors.Count == 0,
                added,
                updated,
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
                errors = new[] { ex.GetType().Name + ": " + ex.Message }
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
