using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Platform-aware secure storage implementation.
/// Uses DPAPI on Windows, AES with PBKDF2 on other platforms.
/// </summary>
public sealed class SecureStorageService : ISecureStorage
{
    private const int KeyDerivationIterations = 100000;
    private const int SaltSize = 32;
    private static readonly byte[] Pepper = [0x43, 0x95, 0x1F, 0x9D, 0xA1, 0xB3, 0x7E, 0x82];

    /// <inheritdoc />
    public string? Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return EncryptWindows(plainText);
            }
            else
            {
                return EncryptCrossPlatform(plainText);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Encryption failed: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public string? Decrypt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return null;
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(cipherText);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return DecryptWindows(cipherBytes);
            }
            else
            {
                return DecryptCrossPlatform(cipherBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Decryption failed: {ex.Message}");
            return null;
        }
    }

    #region Windows DPAPI

    private static string EncryptWindows(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string DecryptWindows(byte[] cipherBytes)
    {
        var decrypted = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    #endregion

    #region Cross-Platform AES

    private static string EncryptCrossPlatform(string plainText)
    {
        // Generate a random salt
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        // Derive a key using PBKDF2
        var key = GetCrossPlatformKey(salt);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var ms = new MemoryStream();
        // Write salt and IV first
        ms.Write(salt, 0, salt.Length);
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var encryptor = aes.CreateEncryptor())
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs, Encoding.UTF8))
        {
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    private static string DecryptCrossPlatform(byte[] cipherBytes)
    {
        // Extract salt
        var salt = new byte[SaltSize];
        Array.Copy(cipherBytes, 0, salt, 0, SaltSize);

        using var aes = Aes.Create();
        var iv = new byte[aes.BlockSize / 8];
        Array.Copy(cipherBytes, SaltSize, iv, 0, iv.Length);

        var key = GetCrossPlatformKey(salt);
        aes.Key = key;
        aes.IV = iv;

        using var ms = new MemoryStream(cipherBytes, SaltSize + iv.Length, cipherBytes.Length - SaltSize - iv.Length);
        using var decryptor = aes.CreateDecryptor();
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static byte[] GetCrossPlatformKey(byte[] salt)
    {
        var userSpecific = string.Concat(
            Environment.UserName,
            Environment.UserDomainName,
            Environment.ProcessPath ?? string.Empty
        );

        var combinedSalt = new byte[salt.Length + Pepper.Length];
        Array.Copy(salt, 0, combinedSalt, 0, salt.Length);
        Array.Copy(Pepper, 0, combinedSalt, salt.Length, Pepper.Length);

        using var deriveBytes = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(userSpecific),
            combinedSalt,
            KeyDerivationIterations,
            HashAlgorithmName.SHA256);

        return deriveBytes.GetBytes(32); // 256 bits for AES-256
    }

    #endregion
}
