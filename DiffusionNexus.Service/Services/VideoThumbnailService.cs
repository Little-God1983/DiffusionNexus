using DiffusionNexus.Domain.Services;
using Serilog;
using FFMpegCore;
using FFMpegCore.Extensions.Downloader;
using FFMpegCore.Extensions.Downloader.Enums;

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
    public string GetThumbnailPath(string videoPath, ThumbnailFormat format = ThumbnailFormat.WebP)
    {
        return GetDefaultThumbnailPath(videoPath, format);
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

            // Each candidate must hold BOTH ffmpeg and ffprobe — FFMpegCore analyses with
            // ffprobe, so a directory with only ffmpeg passes discovery and then fails at
            // the first GenerateThumbnailAsync call.

            // 1. App-local ffmpeg/ subdirectory (bundled deployment)
            var ffmpegDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (FFmpegBinaryLocator.HasBothBinaries(ffmpegDir))
            {
                UseBinaryFolder(ffmpegDir, "app-local directory");
                return;
            }

            // 2. App base directory root (NuGet-packaged binary)
            if (FFmpegBinaryLocator.HasBothBinaries(AppContext.BaseDirectory))
            {
                UseBinaryFolder(AppContext.BaseDirectory, "app base directory");
                return;
            }

            // 3. System PATH — many developers have FFmpeg installed globally
            var pathDir = FFmpegBinaryLocator.FindOnPath();
            if (pathDir is not null)
            {
                UseBinaryFolder(pathDir, "system PATH");
                return;
            }

            // 4. Last resort: download both binaries into the app-local directory
            _logger.Information("FFmpeg not found locally or on PATH — downloading to {Path}...", ffmpegDir);
            Directory.CreateDirectory(ffmpegDir);
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = ffmpegDir });

            await FFMpegDownloader.DownloadBinaries(
                FFMpegVersions.LatestAvailable,
                FFMpegBinaries.FFMpeg | FFMpegBinaries.FFProbe,
                new FFOptions { BinaryFolder = ffmpegDir });

            if (!FFmpegBinaryLocator.HasBothBinaries(ffmpegDir))
                throw new InvalidOperationException(
                    $"FFmpeg download completed but '{FFmpegBinaryLocator.FFmpegFileName}' and " +
                    $"'{FFmpegBinaryLocator.FFprobeFileName}' are not both present in '{ffmpegDir}'.");

            _ffmpegInitialized = true;
            _logger.Information("FFmpeg downloaded and ready at {Path}", ffmpegDir);
        }
        finally
        {
            _ffmpegLock.Release();
        }
    }

    private void UseBinaryFolder(string directory, string source)
    {
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = directory });
        _logger.Information("FFmpeg found in {Source}: {Path}", source, directory);
        _ffmpegInitialized = true;
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
            var analysis = await FFProbe.AnalyseAsync(videoPath, cancellationToken: cancellationToken);
            var videoStream = analysis.PrimaryVideoStream;

            if (videoStream is null)
                return VideoThumbnailResult.Failed("No video stream found in file");

            // analysis.Duration, NOT videoStream.Duration: FFMpegCore parses a stream's duration
            // from ffprobe's per-stream "duration" field only, and Matroska-family containers
            // (.mkv, .webm - both in SupportedExtensions) report that as N/A, yielding
            // TimeSpan.Zero. A zero duration clamps capturePosition to 0 even when the caller
            // supplied one, so every WebM/MKV thumbnail was frame 0 and every result reported a
            // zero-length video. analysis.Duration is the max of the format and stream durations.
            var duration = analysis.Duration;
            var capturePosition = options.CapturePosition ?? TimeSpan.FromTicks(duration.Ticks / 2);

            // Ensure capture position is within bounds
            if (capturePosition > duration)
                capturePosition = duration;
            if (capturePosition < TimeSpan.Zero)
                capturePosition = TimeSpan.Zero;

            _logger.Debug("Generating thumbnail for {Path} at {Position}", videoPath, capturePosition);

            // Quality flag differs per encoder: -q:v is an inverted scale, -quality is not.
            var qualityArgument = options.OutputFormat switch
            {
                ThumbnailFormat.Jpeg => $"-q:v {Math.Max(1, (100 - options.Quality) / 3)}",
                ThumbnailFormat.WebP => $"-quality {options.Quality}",
                _ => string.Empty
            };

            var succeeded = await FFMpegArguments
                .FromFileInput(videoPath, verifyExists: true, input => input.Seek(capturePosition))
                .OutputToFile(outputPath, overwrite: true, output =>
                {
                    output.WithFrameOutputCount(1);
                    output.WithCustomArgument($"-vf scale={options.MaxWidth}:-1");
                    if (!string.IsNullOrEmpty(qualityArgument))
                        output.WithCustomArgument(qualityArgument);
                })
                .CancellableThrough(cancellationToken)
                // throwOnError defaults to true, which would turn a non-zero ffmpeg exit into an
                // exception swallowed by the generic catch below - making the specific message
                // here unreachable and handing the user the generic one instead.
                .ProcessAsynchronously(throwOnError: false);

            if (!succeeded)
                return VideoThumbnailResult.Failed("Thumbnail generation failed - ffmpeg reported failure");

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
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var thumbnailDir = Path.Combine(directory, ".thumbnails");
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(videoPath);
        var extension = GetFormatExtension(format);

        if (!Directory.Exists(thumbnailDir))
        {
            Directory.CreateDirectory(thumbnailDir);

            // Set hidden attribute on Windows; .dot-prefix already hides on Linux/macOS
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(thumbnailDir, File.GetAttributes(thumbnailDir) | FileAttributes.Hidden);
            }
        }

        return Path.Combine(thumbnailDir, $"{fileNameWithoutExtension}_thumb{extension}");
    }

    private static string GetFormatExtension(ThumbnailFormat format) => format switch
    {
        ThumbnailFormat.WebP => ".webp",
        ThumbnailFormat.Jpeg => ".jpg",
        ThumbnailFormat.Png => ".png",
        _ => ".webp"
    };
}
