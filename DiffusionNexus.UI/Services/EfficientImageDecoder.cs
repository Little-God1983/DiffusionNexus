using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
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

            // Read dimensions via SKCodec first. SKCodec.Create may take ownership
            // of the stream (especially for WebP), so we must open a separate stream
            // for any fallback decode.
            int width;
            int height;
            using (var probeStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (probeStream.Length == 0)
                    return null;

                using var codec = SKCodec.Create(probeStream);
                if (codec is null)
                {
                    // Codec creation failed — fall back to Avalonia decode
                    return FallbackDecode(imagePath, targetWidth);
                }

                var info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0)
                    return null;

                width = info.Width;
                height = info.Height;

                // If the image is large enough to benefit from subsampled decode, do it now
                // while the codec is still alive.
                if (width > targetWidth * SmallImageMultiplier)
                {
                    var subsampled = DecodeWithCodec(codec, info, targetWidth);
                    if (subsampled is not null)
                        return subsampled;

                    // SKCodec path failed (e.g. SKBitmap allocation failed for a very
                    // large non-JPEG image). Fall through to the Avalonia stream-based
                    // decoder which handles large images more gracefully.
                    Log.Warning("[EfficientImageDecoder] SKCodec decode returned null for large image, falling back to Avalonia decode: {Path} ({W}x{H}, target={Target})",
                        imagePath, width, height, targetWidth);
                }
            }

            // Image is small enough OR SKCodec path failed — use normal Avalonia decode with a fresh stream
            return FallbackDecode(imagePath, targetWidth);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EfficientImageDecoder] DecodeThumbnail failed for {Path}", imagePath);
            // Last-ditch attempt: try Avalonia decode in case the failure was inside the SKCodec path
            return FallbackDecode(imagePath, targetWidth);
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

            // Probe with SKCodec in its own stream scope. SKCodec.Create may take
            // ownership of the stream (especially for WebP), so a fresh stream is
            // needed for any fallback / full-res decode.
            using (var probeStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (probeStream.Length == 0)
                    return null;

                using var codec = SKCodec.Create(probeStream);
                if (codec is null)
                {
                    // Fallback: load full resolution via Avalonia with a fresh stream
                    return new Bitmap(imagePath);
                }

                var info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0)
                    return null;

                // If the image exceeds the cap, decode at reduced resolution now
                if (info.Width > maxDimension || info.Height > maxDimension)
                {
                    var targetWidth = info.Width >= info.Height
                        ? maxDimension
                        : (int)((float)maxDimension / info.Height * info.Width);

                    var scaled = DecodeWithCodec(codec, info, targetWidth);
                    if (scaled is not null)
                        return scaled;

                    // SKCodec path failed for a very large image (allocation failure or
                    // unsupported codec scaling). Fall back to Avalonia's stream decoder
                    // capped at the same target width to avoid loading the full bitmap.
                    Log.Warning("[EfficientImageDecoder] SKCodec decode returned null for large image, falling back to Avalonia DecodeToWidth: {Path} ({W}x{H}, target={Target})",
                        imagePath, info.Width, info.Height, targetWidth);
                    return FallbackDecode(imagePath, targetWidth);
                }
            }

            // Within the cap — load at full resolution with a fresh stream
            return new Bitmap(imagePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EfficientImageDecoder] DecodeForDisplay failed for {Path}", imagePath);
            // Last-ditch attempt: try Avalonia decode capped at the configured display dimension
            return FallbackDecode(imagePath, maxDimension);
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

        SKBitmap? decoded;
        try
        {
            decoded = new SKBitmap(decodeInfo);
        }
        catch (Exception ex)
        {
            // Pixel allocation failed (very large image). Caller will fall back to
            // Avalonia's stream-based decoder which handles big images gracefully.
            Log.Warning(ex, "[EfficientImageDecoder] SKBitmap allocation failed for {W}x{H}", scaledSize.Width, scaledSize.Height);
            return null;
        }

        using (decoded)
        {
            // SKBitmap silently leaves Width=0 / pixels=IntPtr.Zero when allocation
            // fails internally. Detect that and bail out so the caller can fall back.
            if (decoded.Width == 0 || decoded.Height == 0 || decoded.GetPixels() == IntPtr.Zero)
            {
                Log.Warning("[EfficientImageDecoder] SKBitmap pixel buffer unavailable for {W}x{H}", scaledSize.Width, scaledSize.Height);
                return null;
            }

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
    /// or when SKCodec creation fails. Opens its own stream to avoid issues with
    /// <see cref="SKCodec"/> taking ownership of the previous stream.
    /// </summary>
    private static Bitmap? FallbackDecode(string imagePath, int targetWidth)
    {
        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EfficientImageDecoder] FallbackDecode failed for {Path} (targetWidth={Width})", imagePath, targetWidth);
            return null;
        }
    }
}
