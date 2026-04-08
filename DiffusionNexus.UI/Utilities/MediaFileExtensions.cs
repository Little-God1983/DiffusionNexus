using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// UI-layer utility class for media file type detection and extension handling.
/// Delegates to <see cref="SupportedMediaTypes"/> as the single source of truth.
/// Provides additional UI-specific functionality like thumbnail detection.
/// </summary>
public static class MediaFileExtensions
{
    /// <summary>
    /// Supported image file extensions.
    /// </summary>
    public static string[] ImageExtensions => SupportedMediaTypes.ImageExtensions;

    /// <summary>
    /// Supported video file extensions.
    /// </summary>
    public static string[] VideoExtensions => SupportedMediaTypes.VideoExtensions;

    /// <summary>
    /// Supported caption file extensions.
    /// </summary>
    public static string[] CaptionExtensions => SupportedMediaTypes.CaptionExtensions;

    /// <summary>
    /// Combined media extensions (images + videos).
    /// </summary>
    public static string[] MediaExtensions => SupportedMediaTypes.MediaExtensions;

    /// <summary>
    /// Checks if a file is an image file based on its extension.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized image extension.</returns>
    public static bool IsImageFile(string filePath) => SupportedMediaTypes.IsImageFile(filePath);

    /// <summary>
    /// Checks if a file is a video file based on its extension.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized video extension.</returns>
    public static bool IsVideoFile(string filePath) => SupportedMediaTypes.IsVideoFile(filePath);

    /// <summary>
    /// Checks if a file is a media file (image or video).
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized media extension.</returns>
    public static bool IsMediaFile(string filePath) => SupportedMediaTypes.IsMediaFile(filePath);

    /// <summary>
    /// Checks if a file is a caption file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized caption extension.</returns>
    public static bool IsCaptionFile(string filePath) => SupportedMediaTypes.IsCaptionFile(filePath);

    /// <summary>
    /// Name of the hidden subfolder used to store video thumbnails.
    /// </summary>
    public const string ThumbnailsSubfolder = ".thumbnails";

    /// <summary>
    /// Checks if a file is a video thumbnail (ends with _thumb.webp, _thumb.jpg, or _thumb.png),
    /// regardless of whether it lives in the <c>.thumbnails/</c> subfolder or the legacy same-directory location.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file is a video thumbnail.</returns>
    public static bool IsVideoThumbnailFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return fileName.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase) &&
               (ext == ".webp" || ext == ".jpg" || ext == ".png");
    }

    /// <summary>
    /// Gets the expected thumbnail path for a video file.
    /// Checks the <c>.thumbnails/</c> subfolder first, then falls back to the legacy same-directory location
    /// for backwards compatibility.
    /// </summary>
    /// <param name="videoPath">Path to the video file.</param>
    /// <returns>Expected thumbnail path (prefers <c>.thumbnails/</c> subfolder if it exists).</returns>
    /// <exception cref="ArgumentException">Thrown when videoPath is null or whitespace.</exception>
    public static string GetVideoThumbnailPath(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path cannot be null or empty.", nameof(videoPath));

        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(videoPath);
        var thumbFileName = $"{fileNameWithoutExtension}_thumb.webp";

        // Prefer .thumbnails/ subfolder
        var newPath = Path.Combine(directory, ThumbnailsSubfolder, thumbFileName);
        if (File.Exists(newPath))
            return newPath;

        // Fall back to legacy same-directory location
        var legacyPath = Path.Combine(directory, thumbFileName);
        if (File.Exists(legacyPath))
            return legacyPath;

        // Default to the new .thumbnails/ location for generation
        return newPath;
    }

    /// <summary>
    /// Checks if a media file should be included in listings
    /// (excludes video thumbnails and files inside the <c>.thumbnails/</c> subfolder).
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file should be included in media listings.</returns>
    public static bool IsDisplayableMediaFile(string filePath)
    {
        return IsMediaFile(filePath) && !IsVideoThumbnailFile(filePath) && !IsInThumbnailsFolder(filePath);
    }

    /// <summary>
    /// Checks if a file resides inside a <c>.thumbnails</c> directory.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file is inside a .thumbnails directory.</returns>
    public static bool IsInThumbnailsFolder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return false;

        var dirName = Path.GetFileName(directory);
        return string.Equals(dirName, ThumbnailsSubfolder, StringComparison.OrdinalIgnoreCase);
    }
}
