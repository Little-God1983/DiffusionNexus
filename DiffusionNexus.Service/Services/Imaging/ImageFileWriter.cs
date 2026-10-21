using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace DiffusionNexus.Service.Services.Imaging;

/// <summary>
/// Writes a bitmap to disk in the format its file extension names, the way ImageSharp's
/// <c>Image.Save(path)</c> chose an encoder. SkiaSharp only encodes PNG, JPEG and WebP
/// (<c>SKImage.Encode</c> returns null for everything else), so TIFF goes through LibTiff.NET,
/// which the app already ships for the image editor, and BMP through a built-in writer, the
/// format being a fixed header plus pixel rows. GIF has no encoder here: a photo quantised to
/// 256 colours is a degraded image anyway, and the only libraries that write it bring either
/// GDI+ or a large native payload. It is refused with <see cref="NotSupportedException"/> so a
/// caller never ends up with an empty file under a <c>.gif</c> name.
/// </summary>
public static class ImageFileWriter
{
    /// <summary>Default JPEG and WebP quality, ImageSharp's default for both encoders.</summary>
    public const int DefaultLossyQuality = 75;

    /// <summary>Extensions this writer can produce, lower case with the dot.</summary>
    public static readonly IReadOnlySet<string> WritableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" };

    /// <summary>True when <see cref="Write"/> can produce the format <paramref name="path"/>'s extension names.</summary>
    public static bool CanWrite(string path) => WritableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Writes <paramref name="bitmap"/> to <paramref name="path"/> in the format the extension
    /// names. The file is only created once the encode has succeeded, so a failure never leaves
    /// an empty file behind. Throws <see cref="NotSupportedException"/> for extensions this
    /// writer cannot produce (GIF, or anything unknown).
    /// </summary>
    /// <param name="bitmap">Any colour type; it is converted to unpremultiplied RGBA for the formats that need it.</param>
    /// <param name="path">Target file; the extension picks the format.</param>
    /// <param name="lossyQuality">JPEG / WebP quality 1–100; ignored by the lossless formats.</param>
    public static void Write(SKBitmap bitmap, string path, int lossyQuality = DefaultLossyQuality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".png":
                WriteSkia(bitmap, path, SKEncodedImageFormat.Png, 100);
                break;
            case ".jpg" or ".jpeg":
                WriteSkia(bitmap, path, SKEncodedImageFormat.Jpeg, lossyQuality);
                break;
            case ".webp":
                WriteSkia(bitmap, path, SKEncodedImageFormat.Webp, lossyQuality);
                break;
            case ".bmp":
                File.WriteAllBytes(path, EncodeBmp(bitmap));
                break;
            case ".tif" or ".tiff":
                WriteTiff(bitmap, path);
                break;
            case ".gif":
                throw new NotSupportedException(
                    "Cannot write .gif files: the image library has no GIF encoder. Save the image as PNG, JPEG, WebP, BMP or TIFF instead.");
            default:
                throw new NotSupportedException(
                    $"Cannot write '{extension}' files: supported formats are PNG, JPEG, WebP, BMP and TIFF.");
        }
    }

    private static void WriteSkia(SKBitmap bitmap, string path, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new InvalidOperationException($"Encoding {path} as {format} produced no data.");
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Encodes an uncompressed 32-bit BMP: 14-byte file header, 40-byte BITMAPINFOHEADER,
    /// bottom-up BGRA rows. Every row of a 32-bit image is already a multiple of four bytes,
    /// so no padding is needed. Alpha is written as the fourth byte; readers that ignore it
    /// (BI_RGB has no alpha mask) see an opaque image, which is what ImageSharp's BMP encoder
    /// produced for the fully opaque images the fixer handles.
    /// </summary>
    internal static byte[] EncodeBmp(SKBitmap bitmap)
    {
        using var rgba = AsRgba(bitmap);
        {
            int width = rgba.Width, height = rgba.Height;
            const int fileHeader = 14, infoHeader = 40;
            int rowBytes = width * 4;
            int pixelBytes = rowBytes * height;
            var file = new byte[fileHeader + infoHeader + pixelBytes];
            var span = file.AsSpan();

            // BITMAPFILEHEADER
            span[0] = (byte)'B'; span[1] = (byte)'M';
            BitConverter.TryWriteBytes(span[2..], file.Length);
            BitConverter.TryWriteBytes(span[10..], fileHeader + infoHeader);
            // BITMAPINFOHEADER
            BitConverter.TryWriteBytes(span[14..], infoHeader);
            BitConverter.TryWriteBytes(span[18..], width);
            BitConverter.TryWriteBytes(span[22..], height);           // positive = bottom-up
            BitConverter.TryWriteBytes(span[26..], (short)1);         // planes
            BitConverter.TryWriteBytes(span[28..], (short)32);        // bits per pixel
            BitConverter.TryWriteBytes(span[30..], 0);                // BI_RGB
            BitConverter.TryWriteBytes(span[34..], pixelBytes);
            BitConverter.TryWriteBytes(span[38..], 2835);             // 72 dpi in pixels/metre
            BitConverter.TryWriteBytes(span[42..], 2835);

            var src = rgba.GetPixelSpan();
            int srcStride = rgba.RowBytes;
            for (int y = 0; y < height; y++)
            {
                var srcRow = src.Slice(y * srcStride, rowBytes);
                var dstRow = span.Slice(fileHeader + infoHeader + (height - 1 - y) * rowBytes, rowBytes);
                for (int x = 0; x < rowBytes; x += 4)
                {
                    dstRow[x] = srcRow[x + 2];     // B
                    dstRow[x + 1] = srcRow[x + 1]; // G
                    dstRow[x + 2] = srcRow[x];     // R
                    dstRow[x + 3] = srcRow[x + 3]; // A
                }
            }
            return file;
        }
    }

    /// <summary>
    /// Writes a single RGBA page (8-bit, LZW, unassociated alpha), the same layout the image
    /// editor's <c>TiffExporter</c> writes, so its reader loads these files too.
    /// </summary>
    private static void WriteTiff(SKBitmap bitmap, string path)
    {
        using var rgba = AsRgba(bitmap);
        int width = rgba.Width, height = rgba.Height;

        using var tiff = Tiff.Open(path, "w")
            ?? throw new IOException($"Could not open {path} for writing.");
        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
        tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
        tiff.SetField(TiffTag.ROWSPERSTRIP, tiff.DefaultStripSize(0));

        var src = rgba.GetPixelSpan();
        int stride = rgba.RowBytes;
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            src.Slice(y * stride, row.Length).CopyTo(row);
            if (!tiff.WriteScanline(row, y))
                throw new IOException($"Writing scanline {y} of {path} failed.");
        }
        if (!tiff.WriteDirectory())
            throw new IOException($"Writing the TIFF directory of {path} failed.");
    }

    /// <summary>
    /// Returns the bitmap as unpremultiplied RGBA8888, always as a disposable the caller owns:
    /// a non-owning view over the caller's pixels when the layout already matches (disposing
    /// the view frees nothing), otherwise a converted copy.
    /// </summary>
    private static SKBitmap AsRgba(SKBitmap bitmap)
    {
        if (bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType != SKAlphaType.Premul)
        {
            var view = new SKBitmap();
            view.InstallPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
            return view;
        }

        return bitmap.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException($"Converting a {bitmap.ColorType}/{bitmap.AlphaType} bitmap to RGBA failed.");
    }
}
