using System.Security.Cryptography;
using System.Text;

namespace AzerothCorePanel.Services;

/// <summary>
/// Encrypts secrets (the SOAP password) with Windows DPAPI for the current user, so settings.json never holds
/// them in plain text and only this Windows account can read them back.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AzerothCorePanel.Soap");

    public static string Protect(string secret)
    {
        if (secret.Length == 0)
        {
            return string.Empty;
        }

        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Returns the secret, or an empty string when it cannot be decrypted (other user or PC).</summary>
    public static string Unprotect(string protectedSecret)
    {
        if (protectedSecret.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
