namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents an epoch/checkpoint file in the Epochs subfolder.
/// Supports model weight files: .safetensors, .pt, .pth, .gguf
/// </summary>
public class EpochFileItem
{
    /// <summary>
    /// Supported epoch file extensions.
    /// </summary>
    public static readonly string[] SupportedExtensions = [".safetensors", ".pt", ".pth", ".gguf"];

    /// <summary>
    /// File name with extension.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// File name without extension (for display).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the file.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Formatted file size for display (e.g., "1.5 GB").
    /// </summary>
    public string FileSizeDisplay { get; set; } = string.Empty;

    /// <summary>
    /// File extension (e.g., ".safetensors").
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// When the file was created/added.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the file was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Whether this file is currently selected (for multi-select operations).
    /// </summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// Checks if a file extension is a supported epoch file type.
    /// </summary>
    /// <param name="extension">The file extension (with or without leading dot).</param>
    /// <returns>True if supported.</returns>
    public static bool IsSupportedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;

        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return SupportedExtensions.Contains(ext.ToLowerInvariant());
    }

    /// <summary>
    /// Checks if a file path is a supported epoch file.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if the file has a supported extension.</returns>
    public static bool IsEpochFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Creates an EpochFileItem from a file path.
    /// </summary>
    /// <param name="filePath">Path to the epoch file.</param>
    /// <returns>A new EpochFileItem instance.</returns>
    public static EpochFileItem FromFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fileInfo = new FileInfo(filePath);
        
        return new EpochFileItem
        {
            FileName = fileInfo.Name,
            DisplayName = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            FileSizeDisplay = fileInfo.Exists ? FormatFileSize(fileInfo.Length) : "0 B",
            Extension = fileInfo.Extension.ToLowerInvariant(),
            CreatedAt = fileInfo.Exists ? fileInfo.CreationTime : DateTime.Now,
            ModifiedAt = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.Now,
            IsSelected = false
        };
    }

    /// <summary>
    /// Formats a file size in bytes to a human-readable string.
    /// </summary>
    /// <param name="bytes">Size in bytes.</param>
    /// <returns>Formatted size string (e.g., "1.5 GB").</returns>
    public static string FormatFileSize(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F2} GB",
            >= MB => $"{bytes / (double)MB:F2} MB",
            >= KB => $"{bytes / (double)KB:F2} KB",
            _ => $"{bytes} B"
        };
    }
}
