using DiffusionNexus.Domain.Enums;
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.Captioning;

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
    private const int MaxDimension = 2048;
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
    public static ImagePreprocessResult ProcessImage(string filePath, int maxDimension = MaxDimension)
    {
        var (isValid, validationError) = ValidateImageFile(filePath);
        if (!isValid)
            return ImagePreprocessResult.Failed(validationError!);

        try
        {
            using var originalBitmap = SKBitmap.Decode(filePath);
            if (originalBitmap is null)
                return ImagePreprocessResult.Failed("Failed to decode image. The file may be corrupted.");

            if (originalBitmap.Width < MinDimension || originalBitmap.Height < MinDimension)
                return ImagePreprocessResult.Failed($"Image dimensions too small ({originalBitmap.Width}x{originalBitmap.Height}). Minimum: {MinDimension}x{MinDimension}.");

            var needsResize = originalBitmap.Width > maxDimension || originalBitmap.Height > maxDimension;
            
            SKBitmap processedBitmap;
            if (needsResize)
            {
                var (newWidth, newHeight) = CalculateScaledDimensions(
                    originalBitmap.Width,
                    originalBitmap.Height,
                    maxDimension);

#pragma warning disable CS0618 // Type or member is obsolete - Resize with SKSamplingOptions has different behavior
                processedBitmap = originalBitmap.Resize(new SKImageInfo(newWidth, newHeight), SKFilterQuality.High);
#pragma warning restore CS0618
                if (processedBitmap is null)
                    return ImagePreprocessResult.Failed("Failed to resize image.");

                Log.Debug("Resized image from {OldW}x{OldH} to {NewW}x{NewH}",
                    originalBitmap.Width, originalBitmap.Height, newWidth, newHeight);
            }
            else
            {
                processedBitmap = originalBitmap;
            }

            try
            {
                using var image = SKImage.FromBitmap(processedBitmap);
                
                // Encode as JPEG for smaller size, or PNG if original was PNG
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                var format = extension == ".png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                var quality = format == SKEncodedImageFormat.Jpeg ? 90 : 100;

                using var data = image.Encode(format, quality);
                var imageBytes = data.ToArray();

                return ImagePreprocessResult.Succeeded(
                    imageBytes,
                    processedBitmap.Width,
                    processedBitmap.Height,
                    needsResize);
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
            Log.Error(ex, "Error preprocessing image: {FilePath}", filePath);
            return ImagePreprocessResult.Failed($"Error processing image: {ex.Message}");
        }
    }

    /// <summary>
    /// Preprocesses an image from a byte array.
    /// </summary>
    /// <param name="imageData">The image data.</param>
    /// <param name="maxDimension">Maximum dimension (width or height). Default: 2048.</param>
    /// <returns>The preprocessing result.</returns>
    public static ImagePreprocessResult ProcessImageFromBytes(byte[] imageData, int maxDimension = MaxDimension)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        
        if (imageData.Length < MinFileSizeBytes)
            return ImagePreprocessResult.Failed($"Image data is too small ({imageData.Length} bytes).");

        try
        {
            using var originalBitmap = SKBitmap.Decode(imageData);
            if (originalBitmap is null)
                return ImagePreprocessResult.Failed("Failed to decode image data.");

            if (originalBitmap.Width < MinDimension || originalBitmap.Height < MinDimension)
                return ImagePreprocessResult.Failed($"Image dimensions too small ({originalBitmap.Width}x{originalBitmap.Height}).");

            var needsResize = originalBitmap.Width > maxDimension || originalBitmap.Height > maxDimension;

            SKBitmap processedBitmap;
            if (needsResize)
            {
                var (newWidth, newHeight) = CalculateScaledDimensions(
                    originalBitmap.Width,
                    originalBitmap.Height,
                    maxDimension);

#pragma warning disable CS0618 // Type or member is obsolete - Resize with SKSamplingOptions has different behavior
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
                    needsResize);
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
