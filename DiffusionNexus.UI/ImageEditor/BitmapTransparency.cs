using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Finds pixels that a format without an alpha channel (JPEG) cannot store.
/// </summary>
internal static class BitmapTransparency
{
    /// <summary>
    /// Returns true when at least one pixel of <paramref name="bitmap"/> is not fully opaque.
    /// Partial alpha counts: the JPEG encoder darkens it towards black just like full transparency.
    /// </summary>
    public static bool HasTransparentPixels(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;

        if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
        {
            using var converted = bitmap.Copy(SKColorType.Rgba8888);

            // Unreadable pixels: report transparency. A needless question only costs a click,
            // while a missed one writes black areas into the file.
            return converted is null || HasTransparentPixels(converted);
        }

        // Both 8888 layouts keep alpha in the fourth byte of every pixel.
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var rowLength = bitmap.Width * 4;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * rowBytes, rowLength);
            for (var alpha = 3; alpha < row.Length; alpha += 4)
            {
                if (row[alpha] != byte.MaxValue)
                    return true;
            }
        }

        return false;
    }
}
