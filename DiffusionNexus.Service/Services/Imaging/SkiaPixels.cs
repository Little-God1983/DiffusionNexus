using SkiaSharp;

namespace DiffusionNexus.Service.Services.Imaging;

/// <summary>
/// Pixel-level loading helpers on SkiaSharp for the analysis and ONNX code that used to run on
/// ImageSharp. Everything here hands back plain <see cref="SKBitmap"/>s in a known layout
/// (<see cref="SKColorType.Rgba8888"/>, unpremultiplied) or plain byte arrays, so callers can
/// walk rows with <see cref="SKBitmap.GetPixelSpan"/> the way they walked ImageSharp's
/// <c>ProcessPixelRows</c>.
/// </summary>
public static class SkiaPixels
{
    /// <summary>
    /// The resampler used for every downscale here. ImageSharp's default <c>Bicubic</c> is a Keys
    /// cubic with a = -0.5, which is Catmull-Rom; Skia has no Lanczos, so call sites that used
    /// <c>Lanczos3</c> use this too.
    /// </summary>
    public static readonly SKSamplingOptions Sampling = new(SKCubicResampler.CatmullRom);

    /// <summary>
    /// Decodes an image file into an unpremultiplied RGBA8888 bitmap. Throws when the file is not
    /// a decodable image, matching ImageSharp's <c>Image.Load</c>, which threw too.
    /// </summary>
    public static SKBitmap DecodeRgba(string path)
    {
        using var stream = File.OpenRead(path);
        return DecodeRgba(stream);
    }

    /// <summary>Decodes a stream into an unpremultiplied RGBA8888 bitmap.</summary>
    public static SKBitmap DecodeRgba(Stream stream)
    {
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("The data is not a decodable image.");
        return DecodeRgba(codec);
    }

    /// <summary>Decodes raw RGBA8888 bytes (width × height × 4) into a bitmap that owns a copy.</summary>
    public static SKBitmap FromRgbaBytes(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        rgba.CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    /// <summary>
    /// Returns a bitmap that fits within <paramref name="maxDimension"/> on both axes, keeping the
    /// aspect ratio. Images that already fit come back as the same instance (not a copy), so
    /// callers dispose the result only when it differs from the input. This is ImageSharp's
    /// <c>ResizeMode.Max</c> as the analysers used it: downscale only.
    /// </summary>
    public static SKBitmap FitWithin(SKBitmap source, int maxDimension)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension)
            return source;

        double scale = Math.Min(maxDimension / (double)source.Width, maxDimension / (double)source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return ResizeTo(source, width, height);
    }

    /// <summary>Resizes to exactly <paramref name="width"/> × <paramref name="height"/> (ImageSharp's <c>ResizeMode.Stretch</c>).</summary>
    public static SKBitmap ResizeTo(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType);
        return source.Resize(info, Sampling)
            ?? throw new InvalidOperationException($"Resize to {width}x{height} failed.");
    }

    /// <summary>
    /// Converts an RGBA8888 bitmap to one 8-bit luminance value per pixel, row-major, using the
    /// same BT.709 weights and rounding ImageSharp's <c>L8</c> conversion used, so grayscale
    /// analysis results stay where they were.
    /// </summary>
    public static byte[] ToGray8(SKBitmap rgba)
    {
        EnsureRgba(rgba);
        var pixels = rgba.GetPixelSpan();
        var gray = new byte[rgba.Width * rgba.Height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            gray[i] = Luminance(pixels[p], pixels[p + 1], pixels[p + 2]);
        }
        return gray;
    }

    /// <summary>BT.709 luma of an 8-bit RGB triple, rounded like ImageSharp's <c>L8</c>.</summary>
    public static byte Luminance(byte r, byte g, byte b)
        => (byte)((r * .2126F) + (g * .7152F) + (b * .0722F) + 0.5F);

    /// <summary>
    /// Loads a file as 8-bit luminance, downscaled to fit <paramref name="maxDimension"/> when
    /// larger. Returns the gray plane plus its dimensions.
    /// </summary>
    public static (byte[] Gray, int Width, int Height) LoadGray(string path, int maxDimension = int.MaxValue)
    {
        using var decoded = DecodeRgba(path);
        var fitted = FitWithin(decoded, maxDimension);
        try
        {
            return (ToGray8(fitted), fitted.Width, fitted.Height);
        }
        finally
        {
            if (!ReferenceEquals(fitted, decoded)) fitted.Dispose();
        }
    }

    /// <summary>
    /// Loads a file as unpremultiplied RGBA8888, downscaled to fit <paramref name="maxDimension"/>
    /// when larger. The caller owns the returned bitmap.
    /// </summary>
    public static SKBitmap LoadRgba(string path, int maxDimension = int.MaxValue)
    {
        var decoded = DecodeRgba(path);
        var fitted = FitWithin(decoded, maxDimension);
        if (!ReferenceEquals(fitted, decoded)) decoded.Dispose();
        return fitted;
    }

    /// <summary>Encodes a bitmap to PNG bytes.</summary>
    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Encodes a bitmap to PNG and writes it to <paramref name="path"/>.</summary>
    public static void SavePng(SKBitmap bitmap, string path)
        => File.WriteAllBytes(path, EncodePng(bitmap));

    /// <summary>Encodes a bitmap to JPEG at <paramref name="quality"/> (1–100) and writes it to <paramref name="path"/>.</summary>
    public static void SaveJpeg(SKBitmap bitmap, string path, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static SKBitmap DecodeRgba(SKCodec codec)
    {
        var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Image decode failed: {result}.");
        }
        return bitmap;
    }

    private static void EnsureRgba(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888)
            throw new ArgumentException($"Expected an Rgba8888 bitmap, got {bitmap.ColorType}.", nameof(bitmap));
    }
}
