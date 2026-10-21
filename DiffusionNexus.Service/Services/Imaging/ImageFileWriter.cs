using SkiaSharp;

namespace DiffusionNexus.Service.Services.Imaging;

/// <summary>
/// Writes a bitmap to disk in the format its file extension names, the way ImageSharp's
/// <c>Image.Save(path)</c> chose an encoder, for the formats the app lists as images
/// (<c>SupportedMediaTypes.ImageExtensions</c>). SkiaSharp only encodes PNG, JPEG and WebP
/// (<c>SKImage.Encode</c> returns null for everything else), so BMP goes through a built-in
/// writer, the format being a fixed header plus pixel rows. GIF has no encoder here: a photo
/// quantised to 256 colours is a degraded image anyway, and the only libraries that write it
/// bring either GDI+ or a large native payload. It is refused with <see cref="NotSupportedException"/>.
/// TIFF is not an app image format (SkiaSharp cannot even decode it), so it is not written here
/// either; the image editor has its own <c>TiffExporter</c>.
/// <para>
/// The whole file is encoded in memory first and then moved over the target, so a failure at
/// any point leaves an existing file untouched and never leaves an empty or partial one.
/// </para>
/// </summary>
public static class ImageFileWriter
{
    /// <summary>Default JPEG and WebP quality, ImageSharp's default for both encoders.</summary>
    public const int DefaultLossyQuality = 75;

    /// <summary>Extensions this writer can produce, lower case with the dot.</summary>
    public static readonly IReadOnlySet<string> WritableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    /// <summary>True when <see cref="Write"/> can produce the format <paramref name="path"/>'s extension names.</summary>
    public static bool CanWrite(string path) => WritableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Writes <paramref name="bitmap"/> to <paramref name="path"/> in the format the extension
    /// names. Throws <see cref="NotSupportedException"/> for extensions this writer cannot
    /// produce (GIF, or anything unknown); in that case nothing on disk changes.
    /// </summary>
    /// <param name="bitmap">Any colour and alpha type; formats that need straight RGBA get a converted copy.</param>
    /// <param name="path">Target file; the extension picks the format. May be the same file as <paramref name="metadataSource"/>.</param>
    /// <param name="lossyQuality">
    /// JPEG / WebP quality 1–100. When null: a JPEG keeps the quality of a JPEG
    /// <paramref name="metadataSource"/> and a WebP stays lossless when its source was, as
    /// ImageSharp's encoders did; otherwise <see cref="DefaultLossyQuality"/>.
    /// </param>
    /// <param name="metadataSource">
    /// The original file this bitmap came from. Its generation text, EXIF, XMP, IPTC and comments
    /// are carried into the new file when both are PNG or both are JPEG (see <see cref="ImageMetadataCarrier"/>).
    /// </param>
    public static void Write(SKBitmap bitmap, string path, int? lossyQuality = null, string? metadataSource = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!WritableExtensions.Contains(extension))
        {
            throw new NotSupportedException(extension == ".gif"
                ? "Cannot write .gif files: the image library has no GIF encoder. Save the image as PNG, JPEG, WebP or BMP instead."
                : $"Cannot write '{extension}' files: supported formats are PNG, JPEG, WebP and BMP.");
        }

        // Read the original before anything is written: it may be the target itself.
        byte[]? source = metadataSource is not null && File.Exists(metadataSource)
            ? File.ReadAllBytes(metadataSource)
            : null;

        // Skia decodes one frame of an animation, so the bitmap holds only the first frame;
        // writing it as the replacement of an animated original would drop every other frame.
        if (source is not null)
        {
            using var sourceStream = new SKMemoryStream(source);
            using var sourceCodec = SKCodec.Create(sourceStream);
            if (sourceCodec is { FrameCount: > 1 })
            {
                throw new NotSupportedException(
                    $"{Path.GetFileName(metadataSource)} is animated ({sourceCodec.FrameCount} frames); saving it would keep only the first frame.");
            }
        }

        byte[] encoded = extension switch
        {
            ".png" => ImageMetadataCarrier.CarryPng(SkiaPixels.Encode(bitmap, SKEncodedImageFormat.Png, 100), source),
            ".jpg" or ".jpeg" => ImageMetadataCarrier.CarryJpeg(
                SkiaPixels.Encode(bitmap, SKEncodedImageFormat.Jpeg,
                    lossyQuality ?? ImageMetadataCarrier.EstimateJpegQuality(source) ?? DefaultLossyQuality),
                source),
            ".webp" => lossyQuality is null && ImageMetadataCarrier.IsLosslessWebp(source)
                ? EncodeLosslessWebp(bitmap)
                : SkiaPixels.Encode(bitmap, SKEncodedImageFormat.Webp, lossyQuality ?? DefaultLossyQuality),
            _ => EncodeBmp(bitmap), // .bmp
        };

        ReplaceAtomically(path, encoded);
    }

    /// <summary>
    /// Encodes <paramref name="bitmap"/> in <paramref name="format"/> without touching any file:
    /// PNG, JPEG and WebP through Skia, BMP through the built-in writer. Throws
    /// <see cref="NotSupportedException"/> for GIF and every other format, so a caller that
    /// encodes first and opens its file afterwards never leaves an empty file behind.
    /// </summary>
    /// <param name="quality">JPEG / WebP quality 1–100; ignored by PNG and BMP.</param>
    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return format switch
        {
            SKEncodedImageFormat.Png => SkiaPixels.Encode(bitmap, SKEncodedImageFormat.Png, 100),
            SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp => SkiaPixels.Encode(bitmap, format, quality),
            SKEncodedImageFormat.Bmp => EncodeBmp(bitmap),
            SKEncodedImageFormat.Gif => throw new NotSupportedException(
                "Cannot write GIF: the image library has no GIF encoder. Save the image as PNG, JPEG, WebP or BMP instead."),
            _ => throw new NotSupportedException($"Cannot write {format}: supported formats are PNG, JPEG, WebP and BMP."),
        };
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a temporary file beside <paramref name="path"/> and swaps
    /// it in, so the target is either the old file or the complete new one. A symlinked target is
    /// resolved first so the real file is updated rather than the link replaced, and an existing
    /// file is swapped with <see cref="File.Replace(string, string, string?, bool)"/>, which keeps
    /// its ACLs, attributes and creation date.
    /// </summary>
    private static void ReplaceAtomically(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true) is { } linkTarget)
            fullPath = linkTarget.FullName;

        var directory = Path.GetDirectoryName(fullPath)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(fullPath))
                File.Replace(temp, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, fullPath);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort: the original is what matters */ }
            throw;
        }
    }

    private static byte[] EncodeLosslessWebp(SKBitmap bitmap)
    {
        using var pixmap = bitmap.PeekPixels()
            ?? throw new InvalidOperationException("The bitmap has no pixels to encode.");
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Lossless WebP encoding produced no data.");
        return data.ToArray();
    }

    /// <summary>
    /// Encodes an uncompressed 32-bit BMP: 14-byte file header, 40-byte BITMAPINFOHEADER,
    /// bottom-up BGRA rows with straight alpha. Every row of a 32-bit image is already a multiple
    /// of four bytes, so no padding is needed. Readers that ignore the fourth byte (BI_RGB has no
    /// alpha mask) see an opaque image, which is what ImageSharp's BMP encoder produced.
    /// </summary>
    internal static byte[] EncodeBmp(SKBitmap bitmap)
    {
        using var rgba = ToStraightRgba(bitmap);
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

    /// <summary>
    /// Returns the pixels as unpremultiplied RGBA8888, always as a bitmap the caller disposes: a
    /// non-owning view when the layout already matches, otherwise a converted copy. The copy is
    /// made by reading into an explicit Rgba8888/Unpremul target because <c>SKBitmap.Copy(colorType)</c>
    /// keeps a premultiplied source premultiplied, which stored half-transparent red as dark red.
    /// </summary>
    private static SKBitmap ToStraightRgba(SKBitmap bitmap)
    {
        if (bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType != SKAlphaType.Premul)
        {
            var view = new SKBitmap();
            view.InstallPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
            return view;
        }

        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, bitmap.ColorSpace);
        var copy = new SKBitmap(info);
        using var pixmap = bitmap.PeekPixels();
        if (pixmap is null || !pixmap.ReadPixels(info, copy.GetPixels(), copy.RowBytes, 0, 0))
        {
            copy.Dispose();
            throw new InvalidOperationException($"Converting a {bitmap.ColorType}/{bitmap.AlphaType} bitmap to straight RGBA failed.");
        }
        return copy;
    }
}
