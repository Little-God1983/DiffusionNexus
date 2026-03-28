using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Provides efficient image decoding that avoids loading full-resolution images
/// into memory when only a smaller version is needed. Uses SKCodec for subsampled
/// decoding (JPEG can decode at 1/2, 1/4, 1/8 size natively without full decode).
/// <para>
/// <b>TODO: Linux Implementation</b> — Verify that SKCodec subsampled decoding
/// performs consistently across Linux Skia builds (X11/Wayland).
/// </para>
/// </summary>
internal static class EfficientImageDecoder
{
    /// <summary>
    /// Maximum dimension (width or height) for display-quality images.
    /// Controls the cap applied by <see cref="DecodeForDisplay"/>.
    /// </summary>
    internal const int DefaultMaxDisplayDimension = 4096;

    /// <summary>
    /// If the source image width is at most this multiple of the target, skip
    /// the SKCodec path and let Avalonia's built-in decoder handle it.
    /// </summary>
    private const int SmallImageMultiplier = 2;

    /// <summary>
    /// Decodes an image at a reduced size suitable for thumbnails.
    /// Uses SKCodec subsampled decoding for JPEG files to avoid full memory decode.
    /// For non-JPEG formats, decodes at the smallest codec-supported size then resizes.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="targetWidth">Desired thumbnail width in pixels.</param>
    /// <returns>An Avalonia Bitmap scaled to approximately the target width, or null on failure.</returns>
    internal static Bitmap? DecodeThumbnail(string imagePath, int targetWidth)
    {
        if (string.IsNullOrEmpty(imagePath) || targetWidth <= 0)
            return null;

        try
        {
            if (!File.Exists(imagePath))
                return null;

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0)
                return null;

            // Peek at the image dimensions without full decode
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                // Fallback: codec creation failed, try normal Avalonia decode
                stream.Position = 0;
                return FallbackDecode(stream, targetWidth);
            }

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
                return null;

            // If the image is already small enough, use normal decode
            if (info.Width <= targetWidth * SmallImageMultiplier)
            {
                stream.Position = 0;
                return FallbackDecode(stream, targetWidth);
            }

            // Use SKCodec subsampled decode for efficient thumbnail creation
            return DecodeWithCodec(codec, info, targetWidth);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an image capped at a maximum dimension for display purposes.
    /// Large images (e.g., 8K+) are decoded at reduced resolution while preserving
    /// aspect ratio. Images within the cap are loaded at full resolution.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="maxDimension">Maximum width or height in pixels.</param>
    /// <returns>An Avalonia Bitmap, or null on failure.</returns>
    internal static Bitmap? DecodeForDisplay(string imagePath, int maxDimension = DefaultMaxDisplayDimension)
    {
        if (string.IsNullOrEmpty(imagePath))
            return null;

        try
        {
            if (!File.Exists(imagePath))
                return null;

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0)
                return null;

            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                // Fallback: load full resolution via Avalonia
                stream.Position = 0;
                return new Bitmap(stream);
            }

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
                return null;

            // If within the cap, load at full resolution
            if (info.Width <= maxDimension && info.Height <= maxDimension)
            {
                stream.Position = 0;
                return new Bitmap(stream);
            }

            // Determine target dimension based on which axis exceeds the cap
            var targetWidth = info.Width >= info.Height
                ? maxDimension
                : (int)((float)maxDimension / info.Height * info.Width);

            return DecodeWithCodec(codec, info, targetWidth);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Core decode using SKCodec. Leverages subsampled decoding for JPEG
    /// (1/2, 1/4, 1/8 native subsample) and resizes the result to the target width.
    /// </summary>
    private static Bitmap? DecodeWithCodec(SKCodec codec, SKImageInfo info, int targetWidth)
    {
        // Calculate scale factor for the codec
        float scale = (float)targetWidth / info.Width;

        // GetScaledDimensions returns the closest natively-supported size.
        // For JPEG this may be 1/2, 1/4, or 1/8 of the original (much less memory).
        // For other formats this returns the original dimensions.
        var scaledSize = codec.GetScaledDimensions(scale);

        var decodeInfo = new SKImageInfo(
            scaledSize.Width,
            scaledSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);

        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());

        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            return null;

        // The codec may have decoded at a larger size than our target (e.g., PNG returns
        // full resolution). Resize if the decoded width is notably larger than the target.
        if (decoded.Width > targetWidth * 1.25)
        {
            float finalScale = (float)targetWidth / decoded.Width;
            int finalHeight = Math.Max(1, (int)(decoded.Height * finalScale));

#pragma warning disable CS0618 // SKFilterQuality is obsolete but Resize with SKSamplingOptions has different behavior
            using var resized = decoded.Resize(new SKImageInfo(targetWidth, finalHeight), SKFilterQuality.Medium);
#pragma warning restore CS0618

            return resized is not null ? ToAvaloniaBitmap(resized) : ToAvaloniaBitmap(decoded);
        }

        return ToAvaloniaBitmap(decoded);
    }

    /// <summary>
    /// Converts an SKBitmap to an Avalonia Bitmap by copying pixel data directly
    /// into a <see cref="WriteableBitmap"/>. Avoids the overhead of encoding/decoding
    /// through an intermediate image format.
    /// </summary>
    private static Bitmap? ToAvaloniaBitmap(SKBitmap skBitmap)
    {
        if (skBitmap is null || skBitmap.Width <= 0 || skBitmap.Height <= 0)
            return null;

        // Ensure BGRA format for Avalonia compatibility
        SKBitmap? converted = null;
        if (skBitmap.ColorType != SKColorType.Bgra8888)
        {
            converted = new SKBitmap(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(converted);
            canvas.DrawBitmap(skBitmap, 0, 0);
        }

        var source = converted ?? skBitmap;

        try
        {
            var wb = new WriteableBitmap(
                new PixelSize(source.Width, source.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = wb.Lock())
            {
                var srcPtr = source.GetPixels();
                var byteCount = source.RowBytes * source.Height;

                // Row-by-row copy handles cases where RowBytes may differ
                if (source.RowBytes == fb.RowBytes)
                {
                    CopyMemory(srcPtr, fb.Address, byteCount);
                }
                else
                {
                    var copyBytes = Math.Min(source.RowBytes, fb.RowBytes);
                    for (int y = 0; y < source.Height; y++)
                    {
                        var srcRow = srcPtr + y * source.RowBytes;
                        var dstRow = fb.Address + y * fb.RowBytes;
                        CopyMemory(srcRow, dstRow, copyBytes);
                    }
                }
            }

            return wb;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// Copies memory between two IntPtr locations using a managed byte buffer.
    /// </summary>
    private static void CopyMemory(IntPtr source, IntPtr destination, int byteCount)
    {
        var buffer = new byte[byteCount];
        Marshal.Copy(source, buffer, 0, byteCount);
        Marshal.Copy(buffer, 0, destination, byteCount);
    }

    /// <summary>
    /// Fallback to Avalonia's built-in <see cref="Bitmap.DecodeToWidth"/> for small images
    /// or when SKCodec creation fails.
    /// </summary>
    private static Bitmap? FallbackDecode(Stream stream, int targetWidth)
    {
        try
        {
            return Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }
}
