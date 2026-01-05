namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Service for encrypting and decrypting sensitive data.
/// Uses platform-specific secure storage (DPAPI on Windows, AES on other platforms).
/// </summary>
public interface ISecureStorage
{
    /// <summary>
    /// Encrypts a string value.
    /// </summary>
    /// <param name="plainText">The plain text to encrypt.</param>
    /// <returns>Base64-encoded encrypted string, or null if input is empty.</returns>
    string? Encrypt(string? plainText);

    /// <summary>
    /// Decrypts a previously encrypted string.
    /// </summary>
    /// <param name="cipherText">The Base64-encoded encrypted string.</param>
    /// <returns>The decrypted plain text, or null if input is empty or decryption fails.</returns>
    string? Decrypt(string? cipherText);
}
