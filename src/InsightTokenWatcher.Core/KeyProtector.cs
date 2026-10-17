using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace InsightTokenWatcher.Core;

/// <summary>Protects API keys at rest. Windows: DPAPI LocalMachine (decryptable by any
/// process on the machine, not on others) – chosen over CurrentUser so an import under a
/// service account and the tray app under the interactive user read the same config.
/// Other platforms (no DPAPI): a clearly-marked plain fallback; the config file itself is
/// chmod 600. Format is "v2:" + base64, so Linux configs never look like DPAPI blobs.</summary>
public interface IKeyProtector
{
    string Protect(string plain, Guid id);
    string Unprotect(string cipher, Guid id);
}

internal sealed class DpapiKeyProtector : IKeyProtector
{
    public string Protect(string plain, Guid id) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy(id), DataProtectionScope.LocalMachine));

    public string Unprotect(string cipher, Guid id)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher),
                Entropy(id), DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static byte[] Entropy(Guid id)
    {
        var g = id.ToByteArray();
        var salt = "TokenWatch.v1"u8.ToArray();
        var e = new byte[g.Length + salt.Length];
        Buffer.BlockCopy(g, 0, e, 0, g.Length);
        Buffer.BlockCopy(salt, 0, e, g.Length, salt.Length);
        return e;
    }
}

internal sealed class PlainKeyProtector : IKeyProtector
{
    public string Protect(string plain, Guid id) =>
        "v2:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

    public string Unprotect(string cipher, Guid id)
    {
        try
        {
            return cipher.StartsWith("v2:")
                ? Encoding.UTF8.GetString(Convert.FromBase64String(cipher[3..]))
                : "";
        }
        catch
        {
            return "";
        }
    }
}

public static class KeyProtector
{
    private static readonly IKeyProtector Impl =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new DpapiKeyProtector()
            : new PlainKeyProtector();

    public static string Protect(string plain, Guid id) => Impl.Protect(plain, id);
    public static string Unprotect(string cipher, Guid id) => Impl.Unprotect(cipher, id);
}
