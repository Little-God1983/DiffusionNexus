using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DiffusionNexus.UI.Classes
{
    public static class SecureStorageHelper
    {
        public static string EncryptString(string plainText)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var bytes = Encoding.UTF8.GetBytes(plainText);
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            else
            {
                using var aes = Aes.Create();
                aes.Key = GetCrossPlatformKey();
                aes.GenerateIV();
                using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                using var ms = new MemoryStream();
                ms.Write(aes.IV, 0, aes.IV.Length);
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                using (var sw = new StreamWriter(cs))
                {
                    sw.Write(plainText);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        public static string DecryptString(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;

            var cipherBytes = Convert.FromBase64String(cipherText);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var decrypted = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            else
            {
                using var aes = Aes.Create();
                aes.Key = GetCrossPlatformKey();
                var iv = new byte[aes.BlockSize / 8];
                Array.Copy(cipherBytes, 0, iv, 0, iv.Length);
                aes.IV = iv;
                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                using var ms = new MemoryStream(cipherBytes, iv.Length, cipherBytes.Length - iv.Length);
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                using var sr = new StreamReader(cs);
                return sr.ReadToEnd();
            }
        }

        private static byte[] GetCrossPlatformKey()
        {
            // Derive a key from a user specific value
            var user = Environment.UserName;
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(user));
        }
    }
}
