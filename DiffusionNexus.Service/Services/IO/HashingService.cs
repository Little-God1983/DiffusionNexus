using System.Security.Cryptography;

namespace DiffusionNexus.Service.Services.IO;

/// <summary>
/// Computes file hashes for integrity verification.
/// </summary>
public class HashingService
{
    /// <summary>
    /// Supported hash algorithms.
    /// </summary>
    public enum HashAlgorithmType
    {
        MD5,
        SHA256,
    }

    /// <summary>
    /// Computes the hash for the provided stream.
    /// </summary>
    public async Task<string> ComputeAsync(Stream stream, HashAlgorithmType type = HashAlgorithmType.MD5, CancellationToken cancellationToken = default)
    {
        using HashAlgorithm algorithm = type switch
        {
            HashAlgorithmType.SHA256 => SHA256.Create(),
            _ => MD5.Create(),
        };

        byte[] buffer = await algorithm.ComputeHashAsync(stream, cancellationToken);
        return BitConverter.ToString(buffer).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the hash of a file on disk.
    /// </summary>
    public string ComputeFileHash(string filePath, HashAlgorithmType type = HashAlgorithmType.MD5)
    {
        using FileStream stream = File.OpenRead(filePath);
        return ComputeAsync(stream, type).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Compares two hash strings ignoring case.
    /// </summary>
    public bool Compare(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}
