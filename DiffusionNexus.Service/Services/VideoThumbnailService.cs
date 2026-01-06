using DiffusionNexus.Domain.Services;
using Serilog;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for generating thumbnails from video files using FFmpeg.
/// </summary>
public sealed class VideoThumbnailService : IVideoThumbnailService
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ffmpegLock = new(1, 1);
    private bool _ffmpegInitialized;

    /// <summary>
    /// Supported video file extensions.
    /// </summary>
    private static readonly string[] VideoExtensions =
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

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => VideoExtensions;

    /// <summary>
    /// Creates a new instance of VideoThumbnailService.
    /// </summary>
    public VideoThumbnailService()
    {
        _logger = Log.ForContext<VideoThumbnailService>();
    }

    /// <inheritdoc />
    public bool IsVideoFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var extension = Path.GetExtension(filePath);
        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task EnsureFFmpegAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_ffmpegInitialized)
            return;

        await _ffmpegLock.WaitAsync(cancellationToken);
        try
        {
            if (_ffmpegInitialized)
                return;

            _logger.Information("Ensuring FFmpeg is available...");
            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
            _ffmpegInitialized = true;
            _logger.Information("FFmpeg is ready");
        }
        finally
        {
            _ffmpegLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<VideoThumbnailResult> GenerateThumbnailAsync(
        string videoPath,
        VideoThumbnailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= VideoThumbnailOptions.Default;

        if (string.IsNullOrWhiteSpace(videoPath))
            return VideoThumbnailResult.Failed("Video path is required");

        if (!File.Exists(videoPath))
            return VideoThumbnailResult.Failed($"Video file not found: {videoPath}");

        if (!IsVideoFile(videoPath))
            return VideoThumbnailResult.Failed($"Unsupported video format: {Path.GetExtension(videoPath)}");

        // Determine output path
        var outputPath = options.OutputPath ?? GetDefaultThumbnailPath(videoPath, options.OutputFormat);

        // Check if thumbnail already exists
        if (!options.Overwrite && File.Exists(outputPath))
        {
            _logger.Debug("Thumbnail already exists: {Path}", outputPath);
            return VideoThumbnailResult.AlreadyExists(outputPath);
        }

        try
        {
            // Ensure FFmpeg is available
            await EnsureFFmpegAvailableAsync(cancellationToken);

            // Get video info
            var mediaInfo = await FFmpeg.GetMediaInfo(videoPath, cancellationToken);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();

            if (videoStream is null)
                return VideoThumbnailResult.Failed("No video stream found in file");

            var duration = videoStream.Duration;
            var capturePosition = options.CapturePosition ?? TimeSpan.FromTicks(duration.Ticks / 2);

            // Ensure capture position is within bounds
            if (capturePosition > duration)
                capturePosition = duration;
            if (capturePosition < TimeSpan.Zero)
                capturePosition = TimeSpan.Zero;

            _logger.Debug("Generating thumbnail for {Path} at {Position}", videoPath, capturePosition);

            // Create snapshot
            var conversion = await FFmpeg.Conversions
                .FromSnippet
                .Snapshot(videoPath, outputPath, capturePosition);

            // Add scaling filter
            conversion.AddParameter($"-vf scale={options.MaxWidth}:-1", ParameterPosition.PostInput);

            // Set quality based on format
            var formatExt = GetFormatExtension(options.OutputFormat);
            if (options.OutputFormat == ThumbnailFormat.Jpeg)
            {
                conversion.AddParameter($"-q:v {Math.Max(1, (100 - options.Quality) / 3)}", ParameterPosition.PostInput);
            }
            else if (options.OutputFormat == ThumbnailFormat.WebP)
            {
                conversion.AddParameter($"-quality {options.Quality}", ParameterPosition.PostInput);
            }

            await conversion.Start(cancellationToken);

            if (!File.Exists(outputPath))
                return VideoThumbnailResult.Failed("Thumbnail generation failed - output file not created");

            // Get thumbnail dimensions (estimate based on video aspect ratio and max width)
            var aspectRatio = (double)videoStream.Width / videoStream.Height;
            var thumbWidth = Math.Min(options.MaxWidth, videoStream.Width);
            var thumbHeight = (int)(thumbWidth / aspectRatio);

            _logger.Information("Generated thumbnail: {Path}", outputPath);

            return VideoThumbnailResult.Succeeded(
                outputPath,
                thumbWidth,
                thumbHeight,
                duration,
                capturePosition);
        }
        catch (OperationCanceledException)
        {
            return VideoThumbnailResult.Failed("Thumbnail generation was cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to generate thumbnail for {Path}", videoPath);
            return VideoThumbnailResult.Failed($"Thumbnail generation failed: {ex.Message}");
        }
    }

    private static string GetDefaultThumbnailPath(string videoPath, ThumbnailFormat format)
    {
        var extension = GetFormatExtension(format);
        return Path.ChangeExtension(videoPath, extension);
    }

    private static string GetFormatExtension(ThumbnailFormat format) => format switch
    {
        ThumbnailFormat.WebP => ".webp",
        ThumbnailFormat.Jpeg => ".jpg",
        ThumbnailFormat.Png => ".png",
        _ => ".webp"
    };
}
