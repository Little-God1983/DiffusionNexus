using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DiffusionNexus.UI.Classes
{
    public static class SecureStorageHelper
    {
        private const int KeyDerivationIterations = 100000;
        private const int SaltSize = 32;
        private static readonly byte[] Pepper = new byte[] { 0x43, 0x95, 0x1F, 0x9D, 0xA1, 0xB3, 0x7E, 0x82 };

        public static string? EncryptString(string plainText)
        {
            try
            {
                if (string.IsNullOrEmpty(plainText))
                    return null;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var bytes = Encoding.UTF8.GetBytes(plainText);
                    var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                    return Convert.ToBase64String(protectedBytes);
                }
                else
                {
                    // Generate a random salt
                    var salt = new byte[SaltSize];
                    using (var rng = RandomNumberGenerator.Create())
                    {
                        rng.GetBytes(salt);
                    }

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Encryption failed: {ex.Message}");
                return null;
            }
        }

        public static string? DecryptString(string? cipherText)
        {
            try
            {
                if (string.IsNullOrEmpty(cipherText))
                    return null;

                var cipherBytes = Convert.FromBase64String(cipherText);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var decrypted = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decrypted);
                }
                else
                {
                    // Extract salt and IV
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Decryption failed: {ex.Message}");
                return null;
            }
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
    }
}
