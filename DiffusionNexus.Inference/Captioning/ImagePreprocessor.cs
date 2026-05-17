using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.Inference.Captioning;

/// <summary>
/// Result of image preprocessing.
/// </summary>
/// <param name="Success">Whether preprocessing succeeded.</param>
/// <param name="ImageData">The preprocessed image data (JPEG or PNG bytes).</param>
/// <param name="Width">Width of the processed image.</param>
/// <param name="Height">Height of the processed image.</param>
/// <param name="WasResized">Whether the image was resized.</param>
/// <param name="ErrorMessage">Error message if preprocessing failed.</param>
public record ImagePreprocessResult(
    bool Success,
    byte[]? ImageData,
    int Width,
    int Height,
    bool WasResized,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ImagePreprocessResult Succeeded(byte[] imageData, int width, int height, bool wasResized) =>
        new(true, imageData, width, height, wasResized);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ImagePreprocessResult Failed(string error) =>
        new(false, null, 0, 0, false, error);
}

/// <summary>
/// Preprocesses images for vision-language model inference.
/// Validates, resizes, and formats images using SkiaSharp.
/// </summary>
public sealed class ImagePreprocessor
{
    /// <summary>
    /// Maximum side length (in pixels) we send to the captioning model. Qwen2/2.5/3-VL
    /// recommend max_pixels ≈ 1280·28·28 ≈ 1M pixels for their vision encoder; capping
    /// the longer side at 1280 stays comfortably under that for any aspect ratio and
    /// matches the model's native processing budget. Pictures larger than this are
    /// scaled down in-memory before captioning — the original file on disk is never
    /// modified and no temporary file is written.
    /// </summary>
    private const int MaxDimension = 1280;
    private const int MinDimension = 16;
    private const long MinFileSizeBytes = 100; // Minimum valid image size

    /// <summary>
    /// Gets the maximum dimension for image preprocessing.
    /// </summary>
    public static int MaxImageDimension => MaxDimension;

    /// <summary>
    /// Checks if a file is a valid image that can be processed.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>Tuple of (isValid, errorMessage).</returns>
    public static (bool IsValid, string? ErrorMessage) ValidateImageFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (false, "File path is empty.");

        if (!File.Exists(filePath))
            return (false, "File does not exist.");

        var extension = Path.GetExtension(filePath);
        if (!SupportedMediaTypes.ImageExtensionSet.Contains(extension))
            return (false, $"Unsupported file format: {extension}. Supported formats: {string.Join(", ", SupportedMediaTypes.ImageExtensions)}");

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < MinFileSizeBytes)
                return (false, $"File is too small ({fileInfo.Length} bytes). Minimum size: {MinFileSizeBytes} bytes.");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Error accessing file: {ex.Message}");
        }
    }

    /// <summary>
    /// Preprocesses an image file for model inference.
    /// Validates the image, resizes if necessary, and returns the image data.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <param name="maxDimension">Maximum dimension (width or height). Default: 2048.</param>
    /// <returns>The preprocessing result.</returns>
    public static ImagePreprocessResult ProcessImage(
        string filePath,
        int maxDimension = MaxDimension,
        IUnifiedLogger? unifiedLogger = null)
    {
        var (isValid, validationError) = ValidateImageFile(filePath);
        if (!isValid)
            return ImagePreprocessResult.Failed(validationError!);

        try
        {
            // Cheap peek at the source dimensions via SKCodec — needed only so
            // we can include the original size in the log line and bail early
            // on absurdly small images before a full decode allocates RAM.
            // (An earlier attempt to use SKCodec.GetPixels with SKCodecOptions
            // sampleSize > 1 for memory savings turned out to be silently
            // broken for libpng: it returns Success but writes garbage pixels,
            // which then encoded down to ~7 KB of blank JPEG that mtmd accepted
            // and the model captioned as nothing. See the v0.9.10.7 logs.)
            int sourceWidth, sourceHeight;
            string sourceFormat;
            using (var peekStream = File.OpenRead(filePath))
            using (var peekCodec = SKCodec.Create(peekStream))
            {
                if (peekCodec is null)
                {
                    var msg = "Failed to decode image. The file may be corrupted or in an unsupported format.";
                    unifiedLogger?.Warn(LogCategory.Captioning, "ImagePreprocessor",
                        $"Codec rejected {Path.GetFileName(filePath)}", msg);
                    return ImagePreprocessResult.Failed(msg);
                }

                var info = peekCodec.Info;
                sourceWidth = info.Width;
                sourceHeight = info.Height;
                sourceFormat = peekCodec.EncodedFormat.ToString();

                if (sourceWidth < MinDimension || sourceHeight < MinDimension)
                {
                    var msg = $"Image dimensions too small ({sourceWidth}x{sourceHeight}). Minimum: {MinDimension}x{MinDimension}.";
                    unifiedLogger?.Warn(LogCategory.Captioning, "ImagePreprocessor",
                        $"{Path.GetFileName(filePath)} below min dimension", msg);
                    return ImagePreprocessResult.Failed(msg);
                }
            }

            // Full decode via the canonical SKBitmap.Decode path. Every Skia
            // codec supports this; channel order / alpha / colorspace get
            // resolved correctly inside libskia regardless of source format.
            using var originalBitmap = SKBitmap.Decode(filePath);
            if (originalBitmap is null)
            {
                var msg = "Decoder accepted the file header but produced no bitmap. " +
                          "The image is likely corrupt or uses an unsupported pixel layout.";
                unifiedLogger?.Warn(LogCategory.Captioning, "ImagePreprocessor",
                    $"SKBitmap.Decode returned null for {Path.GetFileName(filePath)}", msg);
                return ImagePreprocessResult.Failed(msg);
            }

            var (newWidth, newHeight) = CalculateScaledDimensions(
                originalBitmap.Width, originalBitmap.Height, maxDimension);

            var needsResize = newWidth != originalBitmap.Width || newHeight != originalBitmap.Height;
            var wasScaledFromSource = needsResize;

            SKBitmap processedBitmap;
            if (needsResize)
            {
#pragma warning disable CS0618 // SKSamplingOptions overload has different behaviour; SKFilterQuality.High remains the correct production choice.
                processedBitmap = originalBitmap.Resize(new SKImageInfo(newWidth, newHeight), SKFilterQuality.High);
#pragma warning restore CS0618
                if (processedBitmap is null)
                {
                    unifiedLogger?.Warn(LogCategory.Captioning, "ImagePreprocessor",
                        $"Resize failed for {Path.GetFileName(filePath)}",
                        $"target={newWidth}x{newHeight}, source={originalBitmap.Width}x{originalBitmap.Height}");
                    return ImagePreprocessResult.Failed("Failed to resize image.");
                }
            }
            else
            {
                processedBitmap = originalBitmap;
            }

            if (wasScaledFromSource)
            {
                var scaleMsg = $"Scaled {Path.GetFileName(filePath)} from {sourceWidth}x{sourceHeight} to " +
                               $"{processedBitmap.Width}x{processedBitmap.Height} (cap={maxDimension}px, src={sourceFormat})";
                Log.Information("{Message}", scaleMsg);
                unifiedLogger?.Info(LogCategory.Captioning, "ImagePreprocessor", scaleMsg);
            }
            else
            {
                unifiedLogger?.Debug(LogCategory.Captioning, "ImagePreprocessor",
                    $"{Path.GetFileName(filePath)} kept at native {processedBitmap.Width}x{processedBitmap.Height} ({sourceFormat})");
            }

            try
            {
                using var image = SKImage.FromBitmap(processedBitmap);

                // Encode purely to an in-memory buffer (SKImage.Encode returns
                // SKData; .ToArray() copies it out). The original file on disk
                // is untouched; no temp file is written anywhere along this path.
                // We always re-encode as JPEG: mtmd is happy with JPEG bytes
                // regardless of the source format, and a single output format
                // avoids edge cases where PNG alpha or HDR PNG variants would
                // break the captioning model.
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90)
                    ?? throw new InvalidOperationException("SKImage.Encode returned null — codec refused the bitmap.");
                var imageBytes = data.ToArray();

                if (imageBytes.Length < MinFileSizeBytes)
                {
                    var msg = $"Preprocessing produced only {imageBytes.Length} bytes; " +
                              "the source image may be corrupt or use an unsupported pixel format.";
                    Log.Warning("Preprocessed image {File} produced suspicious buffer ({Bytes} bytes)",
                        Path.GetFileName(filePath), imageBytes.Length);
                    unifiedLogger?.Warn(LogCategory.Captioning, "ImagePreprocessor",
                        $"Tiny encoded buffer for {Path.GetFileName(filePath)}", msg);
                    return ImagePreprocessResult.Failed(msg);
                }

                return ImagePreprocessResult.Succeeded(
                    imageBytes,
                    processedBitmap.Width,
                    processedBitmap.Height,
                    wasScaledFromSource);
            }
            finally
            {
                // Only dispose the resize result; the source bitmap is in a
                // `using` scope at the call site so the runtime handles it.
                if (needsResize && processedBitmap != originalBitmap)
                {
                    processedBitmap.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preprocessing image: {FilePath}", filePath);
            unifiedLogger?.Error(LogCategory.Captioning, "ImagePreprocessor",
                $"Exception preprocessing {Path.GetFileName(filePath)}: {ex.Message}", ex);
            return ImagePreprocessResult.Failed($"Error processing image: {ex.Message}");
        }
    }

    /// <summary>
    /// Preprocesses an image from a byte array.
    /// </summary>
    /// <param name="imageData">The image data.</param>
    /// <param name="maxDimension">Maximum dimension (width or height). Default: 1280.</param>
    /// <returns>The preprocessing result.</returns>
    public static ImagePreprocessResult ProcessImageFromBytes(byte[] imageData, int maxDimension = MaxDimension)
    {
        ArgumentNullException.ThrowIfNull(imageData);

        if (imageData.Length < MinFileSizeBytes)
            return ImagePreprocessResult.Failed($"Image data is too small ({imageData.Length} bytes).");

        try
        {
            // Mirror the file-path overload: full decode via SKBitmap.Decode,
            // resize after. We don't use SKCodec sample-size decoding here
            // because libpng (and several other codecs) silently ignores the
            // sample-size parameter and writes uninitialised pixels.
            using var originalBitmap = SKBitmap.Decode(imageData);
            if (originalBitmap is null)
                return ImagePreprocessResult.Failed("Failed to decode image data.");

            if (originalBitmap.Width < MinDimension || originalBitmap.Height < MinDimension)
                return ImagePreprocessResult.Failed($"Image dimensions too small ({originalBitmap.Width}x{originalBitmap.Height}).");

            var (newWidth, newHeight) = CalculateScaledDimensions(
                originalBitmap.Width, originalBitmap.Height, maxDimension);

            var needsResize = newWidth != originalBitmap.Width || newHeight != originalBitmap.Height;
            var wasScaledFromSource = needsResize;

            SKBitmap processedBitmap;
            if (needsResize)
            {
#pragma warning disable CS0618
                processedBitmap = originalBitmap.Resize(new SKImageInfo(newWidth, newHeight), SKFilterQuality.High);
#pragma warning restore CS0618
                if (processedBitmap is null)
                    return ImagePreprocessResult.Failed("Failed to resize image.");
            }
            else
            {
                processedBitmap = originalBitmap;
            }

            try
            {
                using var image = SKImage.FromBitmap(processedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                var resultBytes = data.ToArray();

                return ImagePreprocessResult.Succeeded(
                    resultBytes,
                    processedBitmap.Width,
                    processedBitmap.Height,
                    wasScaledFromSource);
            }
            finally
            {
                if (needsResize && processedBitmap != originalBitmap)
                {
                    processedBitmap.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preprocessing image from bytes");
            return ImagePreprocessResult.Failed($"Error processing image: {ex.Message}");
        }
    }

    /// <summary>
    /// Calculates new dimensions that fit within the max dimension while preserving aspect ratio.
    /// </summary>
    private static (int Width, int Height) CalculateScaledDimensions(int originalWidth, int originalHeight, int maxDimension)
    {
        if (originalWidth <= maxDimension && originalHeight <= maxDimension)
            return (originalWidth, originalHeight);

        double scale;
        if (originalWidth > originalHeight)
        {
            scale = (double)maxDimension / originalWidth;
        }
        else
        {
            scale = (double)maxDimension / originalHeight;
        }

        var newWidth = (int)Math.Round(originalWidth * scale);
        var newHeight = (int)Math.Round(originalHeight * scale);

        // Ensure minimum dimensions
        newWidth = Math.Max(newWidth, MinDimension);
        newHeight = Math.Max(newHeight, MinDimension);

        return (newWidth, newHeight);
    }

    /// <summary>
        /// Checks if a file extension is a supported image format.
        /// </summary>
        public static bool IsSupportedImageFormat(string filePath) => SupportedMediaTypes.IsImageFile(filePath);

        /// <summary>
        /// Gets the list of supported image extensions.
        /// </summary>
        public static IReadOnlyCollection<string> GetSupportedExtensions() => SupportedMediaTypes.ImageExtensions;
    }
