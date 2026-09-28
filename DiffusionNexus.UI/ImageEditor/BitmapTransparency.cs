using System.Numerics;
using System.Runtime.InteropServices;
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

        // Both 8888 layouts keep alpha in the fourth byte. Read as a little-endian uint (every
        // platform this Windows app runs on), that is the top byte, so a pixel is fully opaque
        // exactly when it is >= 0xFF000000. Compared a vector of pixels at a time: this runs on
        // the UI thread, and an opaque image (the usual case) has to be read to the end.
        var opaqueFloor = new Vector<uint>(OpaqueFloor);
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var rowLength = bitmap.Width * 4;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * rowBytes, rowLength));
            var x = 0;
            for (; x <= row.Length - Vector<uint>.Count; x += Vector<uint>.Count)
            {
                if (Vector.LessThanAny(new Vector<uint>(row.Slice(x)), opaqueFloor))
                    return true;
            }

            for (; x < row.Length; x++)
            {
                if (row[x] < OpaqueFloor)
                    return true;
            }
        }

        return false;
    }

    private const uint OpaqueFloor = 0xFF000000;
}
