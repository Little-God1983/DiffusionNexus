using System.Diagnostics;
using DiffusionNexus.Domain.Autocropper;
using DiffusionNexus.Domain.Services;
using SkiaSharp;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for cropping images to standard aspect ratio buckets using SkiaSharp.
/// Supports cropping to aspect ratio and optional downscaling.
/// </summary>
public sealed class ImageCropperService : IImageCropperService
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private readonly AspectRatioCropper _cropper = new();
    private int? _maxLongestSide;

    /// <inheritdoc />
    public FolderScanResult ScanFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            return new FolderScanResult(0, 0, []);
        }

        var allFiles = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
        var imageFiles = allFiles
            .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f)))
            .ToArray();

        return new FolderScanResult(allFiles.Length, imageFiles.Length, imageFiles);
    }

    /// <inheritdoc />
    public async Task<CropOperationResult> ProcessImagesAsync(
        string sourceFolderPath,
        string? targetFolderPath,
        IEnumerable<BucketDefinition>? allowedBuckets = null,
        int? maxLongestSide = null,
        bool skipUnchanged = false,
        IProgress<CropProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolderPath);

        var scanResult = ScanFolder(sourceFolderPath);
        if (scanResult.ImageFiles == 0)
        {
            return new CropOperationResult(0, 0, 0, TimeSpan.Zero);
        }

        // Configure allowed buckets and scaling
        _cropper.SetAllowedBuckets(allowedBuckets);
        _maxLongestSide = maxLongestSide;

        bool overwriteMode = string.IsNullOrWhiteSpace(targetFolderPath);
        string outputFolder = overwriteMode ? sourceFolderPath : targetFolderPath!;

        if (!overwriteMode && !Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        var stopwatch = Stopwatch.StartNew();
        int successCount = 0;
        int failedCount = 0;
        int skippedCount = 0;

        for (int i = 0; i < scanResult.ImagePaths.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string imagePath = scanResult.ImagePaths[i];
            string fileName = Path.GetFileName(imagePath);

            try
            {
                var result = await ProcessSingleImageAsync(imagePath, outputFolder, overwriteMode, skipUnchanged);

                progress?.Report(new CropProgress(
                    i + 1,
                    scanResult.ImageFiles,
                    fileName,
                    result.Bucket));

                if (result.Success)
                {
                    successCount++;
                }
                else if (result.Skipped)
                {
                    skippedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (Exception)
            {
                failedCount++;
                progress?.Report(new CropProgress(i + 1, scanResult.ImageFiles, fileName, null));
            }
        }

        stopwatch.Stop();
        return new CropOperationResult(successCount, failedCount, skippedCount, stopwatch.Elapsed);
    }

    private record ProcessResult(bool Success, bool Skipped, BucketDefinition? Bucket);

    private Task<ProcessResult> ProcessSingleImageAsync(
        string sourcePath,
        string outputFolder,
        bool overwriteMode,
        bool skipUnchanged)
    {
        return Task.Run(() =>
        {
            SKBitmap? originalBitmap;

            // Load bitmap and close the file handle immediately
            using (var inputStream = File.OpenRead(sourcePath))
            {
                originalBitmap = SKBitmap.Decode(inputStream);
            }

            if (originalBitmap == null)
            {
                return new ProcessResult(false, false, null);
            }

            try
            {
                var cropResult = _cropper.CalculateCrop(originalBitmap.Width, originalBitmap.Height);

                // Determine if we need to crop
                bool needsCrop = cropResult.CropX != 0 || cropResult.CropY != 0 ||
                    cropResult.TargetWidth != originalBitmap.Width ||
                    cropResult.TargetHeight != originalBitmap.Height;

                // Determine if we need to scale
                int longestSide = Math.Max(cropResult.TargetWidth, cropResult.TargetHeight);
                bool needsScale = _maxLongestSide.HasValue && longestSide > _maxLongestSide.Value;

                // Skip if no processing needed
                if (!needsCrop && !needsScale)
                {
                    originalBitmap.Dispose();

                    if (skipUnchanged)
                    {
                        return new ProcessResult(false, true, cropResult.Bucket);
                    }

                    // If not overwrite mode, still copy the file
                    if (!overwriteMode)
                    {
                        string destPath = Path.Combine(outputFolder, Path.GetFileName(sourcePath));
                        File.Copy(sourcePath, destPath, overwrite: true);
                    }
                    return new ProcessResult(true, true, cropResult.Bucket);
                }

                // Step 1: Crop to aspect ratio
                SKBitmap croppedBitmap;
                if (needsCrop)
                {
                    var cropRect = new SKRectI(
                        cropResult.CropX,
                        cropResult.CropY,
                        cropResult.CropX + cropResult.TargetWidth,
                        cropResult.CropY + cropResult.TargetHeight);

                    croppedBitmap = new SKBitmap(cropResult.TargetWidth, cropResult.TargetHeight);
                    using var canvas = new SKCanvas(croppedBitmap);

                    var sourceRect = SKRect.Create(cropRect.Left, cropRect.Top, cropRect.Width, cropRect.Height);
                    var destRect = SKRect.Create(0, 0, cropResult.TargetWidth, cropResult.TargetHeight);

                    canvas.DrawBitmap(originalBitmap, sourceRect, destRect);
                }
                else
                {
                    // No crop needed, use original
                    croppedBitmap = originalBitmap;
                    originalBitmap = null; // Prevent double dispose
                }

                // Dispose original if we cropped
                originalBitmap?.Dispose();
                originalBitmap = null;

                // Step 2: Scale if needed
                SKBitmap finalBitmap;
                if (needsScale)
                {
                    var (scaledWidth, scaledHeight) = CalculateScaledDimensions(
                        croppedBitmap.Width, croppedBitmap.Height, _maxLongestSide!.Value);

                    finalBitmap = new SKBitmap(scaledWidth, scaledHeight);
                    using var canvas = new SKCanvas(finalBitmap);

                    // Use high quality scaling
                    using var paint = new SKPaint
                    {
                        FilterQuality = SKFilterQuality.High,
                        IsAntialias = true
                    };

                    canvas.DrawBitmap(croppedBitmap,
                        SKRect.Create(0, 0, scaledWidth, scaledHeight),
                        paint);

                    croppedBitmap.Dispose();
                }
                else
                {
                    finalBitmap = croppedBitmap;
                }

                // Determine output path and format
                string fileName = Path.GetFileName(sourcePath);
                string outputPath = Path.Combine(outputFolder, fileName);

                // Use a temp file for overwrite mode to avoid corruption
                string writeTarget = overwriteMode
                    ? Path.Combine(outputFolder, $"_temp_{Guid.NewGuid()}{Path.GetExtension(fileName)}")
                    : outputPath;

                var format = GetImageFormat(sourcePath);

                using (var outputStream = File.Create(writeTarget))
                {
                    finalBitmap.Encode(outputStream, format, 100);
                }

                finalBitmap.Dispose();

                // Replace original with temp file in overwrite mode
                if (overwriteMode)
                {
                    File.Delete(sourcePath);
                    File.Move(writeTarget, outputPath);
                }

                return new ProcessResult(true, false, cropResult.Bucket);
            }
            finally
            {
                originalBitmap?.Dispose();
            }
        });
    }

    /// <summary>
    /// Calculates scaled dimensions while maintaining aspect ratio.
    /// The longest side will be scaled to maxLongestSide.
    /// Dimensions are rounded to multiples of 8.
    /// </summary>
    private static (int Width, int Height) CalculateScaledDimensions(int width, int height, int maxLongestSide)
    {
        double scale = (double)maxLongestSide / Math.Max(width, height);

        int scaledWidth = (int)(width * scale);
        int scaledHeight = (int)(height * scale);

        // Round to multiples of 8
        scaledWidth = (scaledWidth / 8) * 8;
        scaledHeight = (scaledHeight / 8) * 8;

        // Ensure minimum size
        scaledWidth = Math.Max(scaledWidth, 8);
        scaledHeight = Math.Max(scaledHeight, 8);

        return (scaledWidth, scaledHeight);
    }

    private static SKEncodedImageFormat GetImageFormat(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            ".gif" => SKEncodedImageFormat.Gif,
            ".bmp" => SKEncodedImageFormat.Bmp,
            _ => SKEncodedImageFormat.Png
        };
    }
}
