namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a file in the Presentation subfolder for showcasing trained models.
/// Categorizes files as Media (images/videos), Documents, or Raw/Design files.
/// </summary>
public class PresentationFileItem
{
    /// <summary>
    /// Supported image extensions for gallery display.
    /// </summary>
    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tiff", ".tif"];

    /// <summary>
    /// Supported video extensions for gallery display.
    /// </summary>
    public static readonly string[] VideoExtensions =
        [".mp4", ".webm", ".mov", ".avi", ".mkv"];

    /// <summary>
    /// Supported document/text extensions.
    /// </summary>
    public static readonly string[] DocumentExtensions =
    [
        ".txt", ".md", ".markdown",
        ".json", ".xml", ".yaml", ".yml",
        ".pdf",
        ".doc", ".docx",
        ".rtf",
        ".csv", ".tsv",
        ".log", ".ini", ".cfg", ".conf",
        ".html", ".htm"
    ];

    /// <summary>
    /// Supported raw/design file extensions (Photoshop, GIMP, etc.).
    /// </summary>
    public static readonly string[] RawDesignExtensions =
    [
        ".psd",           // Adobe Photoshop
        ".xcf",           // GIMP
        ".ai",            // Adobe Illustrator
        ".svg",           // Scalable Vector Graphics
        ".eps",           // Encapsulated PostScript
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng",  // Camera RAW formats
        ".kra",           // Krita
        ".blend",         // Blender
        ".afphoto",       // Affinity Photo
        ".afdesign"       // Affinity Designer
    ];

    /// <summary>
    /// All supported extensions combined.
    /// </summary>
    public static readonly string[] AllSupportedExtensions =
        [.. ImageExtensions, .. VideoExtensions, .. DocumentExtensions, .. RawDesignExtensions];

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
    /// Formatted file size for display (e.g., "1.5 MB").
    /// </summary>
    public string FileSizeDisplay { get; set; } = string.Empty;

    /// <summary>
    /// File extension (e.g., ".png").
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// Category of the file (Image, Video, Document, RawDesign, Other).
    /// </summary>
    public PresentationFileCategory Category { get; set; }

    /// <summary>
    /// When the file was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the file was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Checks if a file is an image.
    /// </summary>
    public static bool IsImageFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ImageExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks if a file is a video.
    /// </summary>
    public static bool IsVideoFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return VideoExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks if a file is displayable media (image or video for gallery).
    /// </summary>
    public static bool IsMediaFile(string filePath)
    {
        return IsImageFile(filePath) || IsVideoFile(filePath);
    }

    /// <summary>
    /// Checks if a file is a document.
    /// </summary>
    public static bool IsDocumentFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return DocumentExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks if a file is a raw/design file.
    /// </summary>
    public static bool IsRawDesignFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return RawDesignExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks if a file is supported by the Presentation tab.
    /// </summary>
    public static bool IsSupportedFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return AllSupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Gets the category for a file based on its extension.
    /// </summary>
    public static PresentationFileCategory GetCategory(string filePath)
    {
        if (IsImageFile(filePath)) return PresentationFileCategory.Image;
        if (IsVideoFile(filePath)) return PresentationFileCategory.Video;
        if (IsDocumentFile(filePath)) return PresentationFileCategory.Document;
        if (IsRawDesignFile(filePath)) return PresentationFileCategory.RawDesign;
        return PresentationFileCategory.Other;
    }

    /// <summary>
    /// Gets a display icon text for the file category.
    /// Uses text labels instead of emojis for better font compatibility.
    /// </summary>
    public static string GetCategoryIcon(PresentationFileCategory category) => category switch
    {
        PresentationFileCategory.Image => "[IMG]",
        PresentationFileCategory.Video => "[VID]",
        PresentationFileCategory.Document => "[DOC]",
        PresentationFileCategory.RawDesign => "[RAW]",
        _ => "[FILE]"
    };

    /// <summary>
    /// Creates a PresentationFileItem from a file path.
    /// </summary>
    public static PresentationFileItem FromFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fileInfo = new FileInfo(filePath);

        return new PresentationFileItem
        {
            FileName = fileInfo.Name,
            DisplayName = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            FileSizeDisplay = fileInfo.Exists ? FormatFileSize(fileInfo.Length) : "0 B",
            Extension = fileInfo.Extension.ToLowerInvariant(),
            Category = GetCategory(filePath),
            CreatedAt = fileInfo.Exists ? fileInfo.CreationTime : DateTime.Now,
            ModifiedAt = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.Now
        };
    }

    /// <summary>
    /// Formats a file size in bytes to a human-readable string.
    /// </summary>
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

/// <summary>
/// Categories for presentation files.
/// </summary>
public enum PresentationFileCategory
{
    /// <summary>Image files for gallery display.</summary>
    Image,
    /// <summary>Video files for gallery display.</summary>
    Video,
    /// <summary>Document/text files.</summary>
    Document,
    /// <summary>Raw/design files (PSD, XCF, etc.).</summary>
    RawDesign,
    /// <summary>Other unsupported files.</summary>
    Other
}
