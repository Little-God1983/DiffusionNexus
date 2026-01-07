using System.Diagnostics;
using DiffusionNexus.Domain.Autocropper;
using DiffusionNexus.Domain.Services;
using SkiaSharp;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for adjusting images to standard aspect ratio buckets using SkiaSharp.
/// Supports both cropping (removing pixels) and padding (adding canvas) with various fill modes.
/// </summary>
public sealed class ImageCropperService : IImageCropperService
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private readonly AspectRatioCropper _cropper = new();
    private int? _maxLongestSide;
    private FitMode _fitMode;
    private PaddingOptions _paddingOptions = new();

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
        FitMode fitMode = FitMode.Crop,
        PaddingOptions? paddingOptions = null,
        IProgress<CropProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolderPath);

        var scanResult = ScanFolder(sourceFolderPath);
        if (scanResult.ImageFiles == 0)
        {
            return new CropOperationResult(0, 0, 0, TimeSpan.Zero);
        }

        // Configure options
        _cropper.SetAllowedBuckets(allowedBuckets);
        _maxLongestSide = maxLongestSide;
        _fitMode = fitMode;
        _paddingOptions = paddingOptions ?? new PaddingOptions();

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
                return _fitMode == FitMode.Pad
                    ? ProcessWithPadding(originalBitmap, sourcePath, outputFolder, overwriteMode, skipUnchanged)
                    : ProcessWithCropping(originalBitmap, sourcePath, outputFolder, overwriteMode, skipUnchanged);
            }
            finally
            {
                originalBitmap?.Dispose();
            }
        });
    }

    private ProcessResult ProcessWithCropping(
        SKBitmap originalBitmap,
        string sourcePath,
        string outputFolder,
        bool overwriteMode,
        bool skipUnchanged)
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
        SKBitmap adjustedBitmap;
        if (needsCrop)
        {
            var cropRect = new SKRectI(
                cropResult.CropX,
                cropResult.CropY,
                cropResult.CropX + cropResult.TargetWidth,
                cropResult.CropY + cropResult.TargetHeight);

            adjustedBitmap = new SKBitmap(cropResult.TargetWidth, cropResult.TargetHeight);
            using var canvas = new SKCanvas(adjustedBitmap);

            var sourceRect = SKRect.Create(cropRect.Left, cropRect.Top, cropRect.Width, cropRect.Height);
            var destRect = SKRect.Create(0, 0, cropResult.TargetWidth, cropResult.TargetHeight);

            canvas.DrawBitmap(originalBitmap, sourceRect, destRect);
        }
        else
        {
            adjustedBitmap = originalBitmap.Copy();
        }

        // Step 2: Scale if needed
        var finalBitmap = ApplyScaling(adjustedBitmap, needsScale);
        if (finalBitmap != adjustedBitmap)
        {
            adjustedBitmap.Dispose();
        }

        // Save the result
        SaveBitmap(finalBitmap, sourcePath, outputFolder, overwriteMode);
        finalBitmap.Dispose();

        return new ProcessResult(true, false, cropResult.Bucket);
    }

    private ProcessResult ProcessWithPadding(
        SKBitmap originalBitmap,
        string sourcePath,
        string outputFolder,
        bool overwriteMode,
        bool skipUnchanged)
    {
        var padResult = _cropper.CalculatePad(originalBitmap.Width, originalBitmap.Height);

        // Determine if we need to pad
        bool needsPad = padResult.ImageX != 0 || padResult.ImageY != 0 ||
            padResult.CanvasWidth != originalBitmap.Width ||
            padResult.CanvasHeight != originalBitmap.Height;

        // Determine if we need to scale
        int longestSide = Math.Max(padResult.CanvasWidth, padResult.CanvasHeight);
        bool needsScale = _maxLongestSide.HasValue && longestSide > _maxLongestSide.Value;

        // Skip if no processing needed
        if (!needsPad && !needsScale)
        {
            if (skipUnchanged)
            {
                return new ProcessResult(false, true, padResult.Bucket);
            }

            if (!overwriteMode)
            {
                string destPath = Path.Combine(outputFolder, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destPath, overwrite: true);
            }
            return new ProcessResult(true, true, padResult.Bucket);
        }

        // Step 1: Create padded canvas
        SKBitmap adjustedBitmap;
        if (needsPad)
        {
            adjustedBitmap = CreatePaddedBitmap(originalBitmap, padResult);
        }
        else
        {
            adjustedBitmap = originalBitmap.Copy();
        }

        // Step 2: Scale if needed
        var finalBitmap = ApplyScaling(adjustedBitmap, needsScale);
        if (finalBitmap != adjustedBitmap)
        {
            adjustedBitmap.Dispose();
        }

        // Save the result
        SaveBitmap(finalBitmap, sourcePath, outputFolder, overwriteMode);
        finalBitmap.Dispose();

        return new ProcessResult(true, false, padResult.Bucket);
    }

    private SKBitmap CreatePaddedBitmap(SKBitmap originalBitmap, AspectRatioCropper.PadResult padResult)
    {
        var paddedBitmap = new SKBitmap(padResult.CanvasWidth, padResult.CanvasHeight);
        using var canvas = new SKCanvas(paddedBitmap);

        // Fill background based on padding options
        FillPaddingBackground(canvas, originalBitmap, padResult);

        // Draw the original image centered on the canvas
        canvas.DrawBitmap(originalBitmap, padResult.ImageX, padResult.ImageY);

        return paddedBitmap;
    }

    private void FillPaddingBackground(SKCanvas canvas, SKBitmap originalBitmap, AspectRatioCropper.PadResult padResult)
    {
        switch (_paddingOptions.FillMode)
        {
            case PaddingFillMode.SolidColor:
                var color = SKColor.TryParse(_paddingOptions.FillColor, out var parsedColor)
                    ? parsedColor
                    : SKColors.Black;
                canvas.Clear(color);
                break;

            case PaddingFillMode.White:
                canvas.Clear(SKColors.White);
                break;

            case PaddingFillMode.BlurFill:
                FillWithBlur(canvas, originalBitmap, padResult);
                break;

            case PaddingFillMode.Mirror:
                FillWithMirror(canvas, originalBitmap, padResult);
                break;

            default:
                canvas.Clear(SKColors.Black);
                break;
        }
    }

    private void FillWithBlur(SKCanvas canvas, SKBitmap originalBitmap, AspectRatioCropper.PadResult padResult)
    {
        // Create a scaled version of the image to fill the entire canvas
        using var scaledBitmap = new SKBitmap(padResult.CanvasWidth, padResult.CanvasHeight);
        using var scaleCanvas = new SKCanvas(scaledBitmap);
        
        // Scale the original image to fill the canvas (will be cropped/stretched)
        var destRect = SKRect.Create(0, 0, padResult.CanvasWidth, padResult.CanvasHeight);
        scaleCanvas.DrawBitmap(originalBitmap, destRect);

        // Apply blur filter
        using var blurPaint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(_paddingOptions.BlurRadius, _paddingOptions.BlurRadius)
        };

        canvas.DrawBitmap(scaledBitmap, 0, 0, blurPaint);
    }

    private static void FillWithMirror(SKCanvas canvas, SKBitmap originalBitmap, AspectRatioCropper.PadResult padResult)
    {
        // Fill with mirrored edges
        // Left padding
        if (padResult.ImageX > 0)
        {
            using var leftSlice = new SKBitmap(Math.Min(padResult.ImageX, originalBitmap.Width), originalBitmap.Height);
            using var leftCanvas = new SKCanvas(leftSlice);
            leftCanvas.Scale(-1, 1);
            leftCanvas.Translate(-Math.Min(padResult.ImageX, originalBitmap.Width), 0);
            leftCanvas.DrawBitmap(originalBitmap, 0, 0);
            
            canvas.DrawBitmap(leftSlice, 0, padResult.ImageY);
        }

        // Right padding
        int rightPadding = padResult.CanvasWidth - padResult.ImageX - originalBitmap.Width;
        if (rightPadding > 0)
        {
            using var rightSlice = new SKBitmap(Math.Min(rightPadding, originalBitmap.Width), originalBitmap.Height);
            using var rightCanvas = new SKCanvas(rightSlice);
            rightCanvas.Scale(-1, 1);
            rightCanvas.Translate(-originalBitmap.Width, 0);
            rightCanvas.DrawBitmap(originalBitmap, originalBitmap.Width - Math.Min(rightPadding, originalBitmap.Width), 0);
            
            canvas.DrawBitmap(rightSlice, padResult.ImageX + originalBitmap.Width, padResult.ImageY);
        }

        // Top padding
        if (padResult.ImageY > 0)
        {
            using var topSlice = new SKBitmap(originalBitmap.Width, Math.Min(padResult.ImageY, originalBitmap.Height));
            using var topCanvas = new SKCanvas(topSlice);
            topCanvas.Scale(1, -1);
            topCanvas.Translate(0, -Math.Min(padResult.ImageY, originalBitmap.Height));
            topCanvas.DrawBitmap(originalBitmap, 0, 0);
            
            canvas.DrawBitmap(topSlice, padResult.ImageX, 0);
        }

        // Bottom padding
        int bottomPadding = padResult.CanvasHeight - padResult.ImageY - originalBitmap.Height;
        if (bottomPadding > 0)
        {
            using var bottomSlice = new SKBitmap(originalBitmap.Width, Math.Min(bottomPadding, originalBitmap.Height));
            using var bottomCanvas = new SKCanvas(bottomSlice);
            bottomCanvas.Scale(1, -1);
            bottomCanvas.Translate(0, -originalBitmap.Height);
            bottomCanvas.DrawBitmap(originalBitmap, 0, originalBitmap.Height - Math.Min(bottomPadding, originalBitmap.Height));
            
            canvas.DrawBitmap(bottomSlice, padResult.ImageX, padResult.ImageY + originalBitmap.Height);
        }

        // Fill corners with solid color from nearest corner pixel
        if (padResult.ImageX > 0 && padResult.ImageY > 0)
        {
            var cornerColor = originalBitmap.GetPixel(0, 0);
            using var cornerPaint = new SKPaint { Color = cornerColor };
            canvas.DrawRect(0, 0, padResult.ImageX, padResult.ImageY, cornerPaint);
        }

        int rightX = padResult.ImageX + originalBitmap.Width;
        if (rightPadding > 0 && padResult.ImageY > 0)
        {
            var cornerColor = originalBitmap.GetPixel(originalBitmap.Width - 1, 0);
            using var cornerPaint = new SKPaint { Color = cornerColor };
            canvas.DrawRect(rightX, 0, rightPadding, padResult.ImageY, cornerPaint);
        }

        if (padResult.ImageX > 0 && bottomPadding > 0)
        {
            var cornerColor = originalBitmap.GetPixel(0, originalBitmap.Height - 1);
            using var cornerPaint = new SKPaint { Color = cornerColor };
            canvas.DrawRect(0, padResult.ImageY + originalBitmap.Height, padResult.ImageX, bottomPadding, cornerPaint);
        }

        if (rightPadding > 0 && bottomPadding > 0)
        {
            var cornerColor = originalBitmap.GetPixel(originalBitmap.Width - 1, originalBitmap.Height - 1);
            using var cornerPaint = new SKPaint { Color = cornerColor };
            canvas.DrawRect(rightX, padResult.ImageY + originalBitmap.Height, rightPadding, bottomPadding, cornerPaint);
        }
    }

    private SKBitmap ApplyScaling(SKBitmap bitmap, bool needsScale)
    {
        if (!needsScale || !_maxLongestSide.HasValue)
        {
            return bitmap;
        }

        var (scaledWidth, scaledHeight) = CalculateScaledDimensions(
            bitmap.Width, bitmap.Height, _maxLongestSide.Value);

        var scaledBitmap = new SKBitmap(scaledWidth, scaledHeight);
        using var canvas = new SKCanvas(scaledBitmap);

        using var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true
        };

        canvas.DrawBitmap(bitmap, SKRect.Create(0, 0, scaledWidth, scaledHeight), paint);

        return scaledBitmap;
    }

    private void SaveBitmap(SKBitmap bitmap, string sourcePath, string outputFolder, bool overwriteMode)
    {
        string fileName = Path.GetFileName(sourcePath);
        string outputPath = Path.Combine(outputFolder, fileName);

        // Use a temp file for overwrite mode to avoid corruption
        string writeTarget = overwriteMode
            ? Path.Combine(outputFolder, $"_temp_{Guid.NewGuid()}{Path.GetExtension(fileName)}")
            : outputPath;

        var format = GetImageFormat(sourcePath);

        using (var outputStream = File.Create(writeTarget))
        {
            bitmap.Encode(outputStream, format, 100);
        }

        // Replace original with temp file in overwrite mode
        if (overwriteMode)
        {
            File.Delete(sourcePath);
            File.Move(writeTarget, outputPath);
        }
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
