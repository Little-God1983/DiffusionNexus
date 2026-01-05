using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Represents a downloadable file for a model version.
/// </summary>
public sealed record CivitaiModelFile
{
    /// <summary>The file ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>The filename.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The file size in KB.</summary>
    [JsonPropertyName("sizeKB")]
    public double SizeKB { get; init; }

    /// <summary>The file type (Model, Training Data, etc.).</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Whether this is the primary file for the version.</summary>
    [JsonPropertyName("primary")]
    public bool? Primary { get; init; }

    /// <summary>File metadata (precision, size, format).</summary>
    [JsonPropertyName("metadata")]
    public CivitaiFileMetadata? Metadata { get; init; }

    /// <summary>Status of the pickle scan.</summary>
    [JsonPropertyName("pickleScanResult")]
    public CivitaiScanResult PickleScanResult { get; init; }

    /// <summary>Message from pickle scan.</summary>
    [JsonPropertyName("pickleScanMessage")]
    public string? PickleScanMessage { get; init; }

    /// <summary>Status of the virus scan.</summary>
    [JsonPropertyName("virusScanResult")]
    public CivitaiScanResult VirusScanResult { get; init; }

    /// <summary>When the file was scanned.</summary>
    [JsonPropertyName("scannedAt")]
    public DateTimeOffset? ScannedAt { get; init; }

    /// <summary>File hashes.</summary>
    [JsonPropertyName("hashes")]
    public CivitaiFileHashes? Hashes { get; init; }

    /// <summary>The download URL for this file.</summary>
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// File metadata about format and precision.
/// </summary>
public sealed record CivitaiFileMetadata
{
    [JsonPropertyName("fp")]
    public CivitaiFloatingPoint? Fp { get; init; }

    [JsonPropertyName("size")]
    public CivitaiFileSize? Size { get; init; }

    [JsonPropertyName("format")]
    public CivitaiFileFormat? Format { get; init; }
}

/// <summary>
/// File hash values in multiple formats.
/// </summary>
public sealed record CivitaiFileHashes
{
    [JsonPropertyName("AutoV1")]
    public string? AutoV1 { get; init; }

    [JsonPropertyName("AutoV2")]
    public string? AutoV2 { get; init; }

    [JsonPropertyName("SHA256")]
    public string? SHA256 { get; init; }

    [JsonPropertyName("CRC32")]
    public string? CRC32 { get; init; }

    [JsonPropertyName("BLAKE3")]
    public string? BLAKE3 { get; init; }
}
