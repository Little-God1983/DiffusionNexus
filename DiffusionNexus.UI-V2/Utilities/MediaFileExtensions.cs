namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Centralized utility class for media file type detection and extension handling.
/// Follows DRY principle by consolidating file extension arrays and helper methods
/// that were duplicated across DatasetCardViewModel, DatasetManagementViewModel,
/// and DatasetImageViewModel.
/// </summary>
public static class MediaFileExtensions
{
    /// <summary>
    /// Supported image file extensions.
    /// </summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>
    /// Supported video file extensions.
    /// </summary>
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v"];

    /// <summary>
    /// Supported caption file extensions.
    /// </summary>
    public static readonly string[] CaptionExtensions = [".txt", ".caption"];

    /// <summary>
    /// Combined media extensions (images + videos).
    /// </summary>
    public static readonly string[] MediaExtensions = [..ImageExtensions, ..VideoExtensions];

    /// <summary>
    /// Checks if a file is an image file based on its extension.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized image extension.</returns>
    public static bool IsImageFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a file is a video file based on its extension.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized video extension.</returns>
    public static bool IsVideoFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a file is a media file (image or video).
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized media extension.</returns>
    public static bool IsMediaFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return MediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a file is a caption file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file has a recognized caption extension.</returns>
    public static bool IsCaptionFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return CaptionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a file is a video thumbnail (ends with _thumb.webp, _thumb.jpg, or _thumb.png).
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
    /// Uses the naming convention: {videoname}_thumb.webp
    /// </summary>
    /// <param name="videoPath">Path to the video file.</param>
    /// <returns>Expected thumbnail path.</returns>
    public static string GetVideoThumbnailPath(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}_thumb.webp");
    }

    /// <summary>
    /// Checks if a media file should be included in listings (excludes video thumbnails).
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if the file should be included in media listings.</returns>
    public static bool IsDisplayableMediaFile(string filePath)
    {
        return IsMediaFile(filePath) && !IsVideoThumbnailFile(filePath);
    }
}
