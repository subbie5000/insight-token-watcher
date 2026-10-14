using System.Security.Cryptography;
using System.Text;

namespace TokenWatch.Core;

/// <summary>DPAPI (LocalMachine) – decryptable by any process on the same machine,
/// but not on other machines. Chosen over CurrentUser so that an import (service account)
/// and the tray app (interactive user) can read the same config.</summary>
public static class KeyProtector
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string Protect(string plain, Guid id) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy(id), DataProtectionScope.LocalMachine));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string Unprotect(string protectedB64, Guid id)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedB64),
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
