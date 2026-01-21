namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Centralized definition of supported media file types.
/// Single source of truth for image, video, and caption file extensions.
/// </summary>
public static class SupportedMediaTypes
{
    /// <summary>
    /// Supported image file extensions.
    /// </summary>
    public static readonly string[] ImageExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
        ".gif"
    ];

    /// <summary>
    /// Supported video file extensions.
    /// </summary>
    public static readonly string[] VideoExtensions =
    [
        ".mp4",
        ".mov",
        ".webm",
        ".avi",
        ".mkv",
        ".wmv",
        ".flv",
        ".m4v"
    ];

    /// <summary>
    /// Supported caption file extensions.
    /// </summary>
    public static readonly string[] CaptionExtensions =
    [
        ".txt",
        ".caption"
    ];

    /// <summary>
    /// Combined media extensions (images + videos).
    /// </summary>
    public static readonly string[] MediaExtensions = [.. ImageExtensions, .. VideoExtensions];

    /// <summary>
    /// Display string for image extensions (e.g., ".png, .jpg, .jpeg, .webp, .bmp, .gif").
    /// </summary>
    public static string ImageExtensionsDisplay { get; } = string.Join(", ", ImageExtensions);

    /// <summary>
    /// Display string for video extensions (e.g., ".mp4, .mov, .webm, .avi, .mkv, .wmv, .flv, .m4v").
    /// </summary>
    public static string VideoExtensionsDisplay { get; } = string.Join(", ", VideoExtensions);

    /// <summary>
    /// Display string for caption extensions (e.g., ".txt, .caption").
    /// </summary>
    public static string CaptionExtensionsDisplay { get; } = string.Join(", ", CaptionExtensions);

    /// <summary>
    /// HashSet for efficient image extension lookups (case-insensitive).
    /// </summary>
    public static readonly HashSet<string> ImageExtensionSet =
        new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HashSet for efficient video extension lookups (case-insensitive).
    /// </summary>
    public static readonly HashSet<string> VideoExtensionSet =
        new(VideoExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HashSet for efficient caption extension lookups (case-insensitive).
    /// </summary>
    public static readonly HashSet<string> CaptionExtensionSet =
        new(CaptionExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HashSet for efficient media extension lookups (case-insensitive).
    /// </summary>
    public static readonly HashSet<string> MediaExtensionSet =
        new(MediaExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a file path represents an image file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized image extension.</returns>
    public static bool IsImageFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ImageExtensionSet.Contains(ext);
    }

    /// <summary>
    /// Checks if a file path represents a video file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized video extension.</returns>
    public static bool IsVideoFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return VideoExtensionSet.Contains(ext);
    }

    /// <summary>
    /// Checks if a file path represents a media file (image or video).
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized media extension.</returns>
    public static bool IsMediaFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return MediaExtensionSet.Contains(ext);
    }

    /// <summary>
    /// Checks if a file path represents a caption file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized caption extension.</returns>
    public static bool IsCaptionFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return CaptionExtensionSet.Contains(ext);
    }
}
