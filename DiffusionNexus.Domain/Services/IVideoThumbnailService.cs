namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Service for generating thumbnails from video files using FFmpeg.
/// </summary>
public interface IVideoThumbnailService
{
    /// <summary>
    /// Generates a thumbnail image from a video file.
    /// </summary>
    /// <param name="videoPath">Path to the video file.</param>
    /// <param name="options">Thumbnail generation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the thumbnail path or error information.</returns>
    Task<VideoThumbnailResult> GenerateThumbnailAsync(
        string videoPath,
        VideoThumbnailOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file is a supported video format.
    /// </summary>
    /// <param name="filePath">Path to the file to check.</param>
    /// <returns>True if the file is a supported video format.</returns>
    bool IsVideoFile(string filePath);

    /// <summary>
    /// Gets the list of supported video file extensions.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Ensures FFmpeg is available for use.
    /// Downloads FFmpeg if not already present.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnsureFFmpegAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for video thumbnail generation.
/// </summary>
public sealed record VideoThumbnailOptions
{
    /// <summary>
    /// Maximum width for the thumbnail. Height is calculated to maintain aspect ratio.
    /// Default is 320 pixels.
    /// </summary>
    public int MaxWidth { get; init; } = 320;

    /// <summary>
    /// Quality for image encoding (1-100).
    /// Default is 80.
    /// </summary>
    public int Quality { get; init; } = 80;

    /// <summary>
    /// Position in the video to capture the thumbnail.
    /// If null, captures from the middle of the video.
    /// </summary>
    public TimeSpan? CapturePosition { get; init; }

    /// <summary>
    /// Output format for the thumbnail.
    /// Default is WebP for smaller file sizes.
    /// </summary>
    public ThumbnailFormat OutputFormat { get; init; } = ThumbnailFormat.WebP;

    /// <summary>
    /// Custom output path for the thumbnail.
    /// If null, the thumbnail is saved next to the video with appropriate extension.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Whether to overwrite an existing thumbnail.
    /// Default is false (skip if thumbnail exists).
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Default options instance.
    /// </summary>
    public static VideoThumbnailOptions Default { get; } = new();
}

/// <summary>
/// Output format for video thumbnails.
/// </summary>
public enum ThumbnailFormat
{
    /// <summary>WebP format (smaller file size).</summary>
    WebP,
    
    /// <summary>JPEG format (wider compatibility).</summary>
    Jpeg,
    
    /// <summary>PNG format (lossless).</summary>
    Png
}

/// <summary>
/// Result of a video thumbnail generation operation.
/// </summary>
public sealed record VideoThumbnailResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Path to the generated thumbnail file.</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>Width of the generated thumbnail.</summary>
    public int Width { get; init; }

    /// <summary>Height of the generated thumbnail.</summary>
    public int Height { get; init; }

    /// <summary>Duration of the source video.</summary>
    public TimeSpan VideoDuration { get; init; }

    /// <summary>Position in the video where the thumbnail was captured.</summary>
    public TimeSpan CapturePosition { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static VideoThumbnailResult Succeeded(string thumbnailPath, int width, int height, TimeSpan duration, TimeSpan capturePosition) => new()
    {
        Success = true,
        ThumbnailPath = thumbnailPath,
        Width = width,
        Height = height,
        VideoDuration = duration,
        CapturePosition = capturePosition
    };

    /// <summary>Creates a failed result.</summary>
    public static VideoThumbnailResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };

    /// <summary>Creates a result indicating the thumbnail already exists.</summary>
    public static VideoThumbnailResult AlreadyExists(string thumbnailPath) => new()
    {
        Success = true,
        ThumbnailPath = thumbnailPath
    };
}
