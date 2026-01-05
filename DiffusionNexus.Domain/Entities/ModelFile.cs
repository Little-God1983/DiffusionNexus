using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a downloadable file for a model version.
/// A version can have multiple files (e.g., fp16, fp32, pruned).
/// </summary>
public class ModelFile
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>Civitai file ID.</summary>
    public int? CivitaiId { get; set; }

    /// <summary>Parent model version ID.</summary>
    public int ModelVersionId { get; set; }

    /// <summary>The filename.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>File size in KB.</summary>
    public double SizeKB { get; set; }

    /// <summary>File size in bytes for exact matching when files are moved.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>File type (e.g., "Model", "Training Data").</summary>
    public string FileType { get; set; } = "Model";

    /// <summary>Whether this is the primary file for the version.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>File format.</summary>
    public FileFormat Format { get; set; } = FileFormat.Unknown;

    /// <summary>Floating point precision.</summary>
    public FilePrecision Precision { get; set; } = FilePrecision.Unknown;

    /// <summary>File size type (full/pruned).</summary>
    public FileSizeType SizeType { get; set; } = FileSizeType.Unknown;

    /// <summary>Download URL.</summary>
    public string? DownloadUrl { get; set; }

    #region Security Scans

    public ScanResult PickleScanResult { get; set; } = ScanResult.Pending;
    public string? PickleScanMessage { get; set; }
    public ScanResult VirusScanResult { get; set; } = ScanResult.Pending;
    public DateTimeOffset? ScannedAt { get; set; }

    #endregion

    #region Hashes

    public string? HashAutoV1 { get; set; }
    public string? HashAutoV2 { get; set; }
    public string? HashSHA256 { get; set; }
    public string? HashCRC32 { get; set; }
    public string? HashBLAKE3 { get; set; }

    #endregion

    #region Local File Tracking

    /// <summary>Local file path where this file is stored.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Whether the local file exists and is valid.</summary>
    public bool IsLocalFileValid { get; set; }

    /// <summary>When the local file was last verified.</summary>
    public DateTimeOffset? LocalFileVerifiedAt { get; set; }

    #endregion

    #region Navigation Properties

    public ModelVersion? ModelVersion { get; set; }

    #endregion

    #region Computed Properties

    /// <summary>File size in MB.</summary>
    public double SizeMB => SizeKB / 1024.0;

    /// <summary>File size in GB.</summary>
    public double SizeGB => SizeMB / 1024.0;

    /// <summary>Gets a display-friendly file size string.</summary>
    public string SizeDisplay => SizeGB >= 1 
        ? $"{SizeGB:F2} GB" 
        : SizeMB >= 1 
            ? $"{SizeMB:F1} MB" 
            : $"{SizeKB:F0} KB";

    /// <summary>Whether security scans passed.</summary>
    public bool IsSecure =>
        PickleScanResult == ScanResult.Success && VirusScanResult == ScanResult.Success;

    #endregion
}
