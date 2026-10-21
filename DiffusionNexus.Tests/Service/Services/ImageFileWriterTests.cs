using DiffusionNexus.Service.Services.Imaging;
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class ImageFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dn-imagefilewriter-" + Guid.NewGuid().ToString("N"));

    public ImageFileWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    public void Write_LosslessFormats_RoundTripEveryPixel(string extension)
    {
        using var source = CreatePattern(7, 5);
        var path = Path.Combine(_dir, "pattern" + extension);

        ImageFileWriter.Write(source, path);

        using var reloaded = Load(path);
        reloaded.Width.Should().Be(7);
        reloaded.Height.Should().Be(5);
        reloaded.GetPixelSpan().ToArray().Should().Equal(source.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    public void Write_LossyFormats_ProduceADecodableFileOfTheSameSize(string extension)
    {
        using var source = CreatePattern(16, 12);
        var path = Path.Combine(_dir, "pattern" + extension);

        ImageFileWriter.Write(source, path);

        using var reloaded = SkiaPixels.DecodeRgba(path);
        reloaded.Width.Should().Be(16);
        reloaded.Height.Should().Be(12);
    }

    [Fact]
    public void Write_Bmp_IsAStandard32BitBottomUpBitmap()
    {
        using var source = CreatePattern(3, 2);

        var bytes = ImageFileWriter.EncodeBmp(source);

        bytes.Length.Should().Be(14 + 40 + 3 * 2 * 4);
        bytes[0].Should().Be((byte)'B');
        bytes[1].Should().Be((byte)'M');
        BitConverter.ToInt32(bytes, 2).Should().Be(bytes.Length);
        BitConverter.ToInt32(bytes, 10).Should().Be(54);
        BitConverter.ToInt32(bytes, 18).Should().Be(3);
        BitConverter.ToInt32(bytes, 22).Should().Be(2);
        BitConverter.ToInt16(bytes, 28).Should().Be(32);
        // Bottom-up: the first stored row is the last image row; pixel (0, 1) of the pattern.
        var expected = source.GetPixel(0, 1);
        bytes[54].Should().Be(expected.Blue);
        bytes[55].Should().Be(expected.Green);
        bytes[56].Should().Be(expected.Red);
        bytes[57].Should().Be(expected.Alpha);
    }

    [Fact]
    public void Write_Bmp_ConvertsAPremultipliedBgraBitmap()
    {
        using var bgra = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
        bgra.Erase(new SKColor(200, 100, 50, 255));
        var path = Path.Combine(_dir, "bgra.bmp");

        ImageFileWriter.Write(bgra, path);

        using var reloaded = Load(path);
        reloaded.GetPixel(2, 2).Should().Be(new SKColor(200, 100, 50, 255));
    }

    [Fact]
    public void Write_Tiff_IsReadableByTheImageEditorsReader()
    {
        using var source = CreatePattern(9, 4);
        var path = Path.Combine(_dir, "editor.tif");

        ImageFileWriter.Write(source, path);

        using var reloaded = TiffExporter.DecodeFlatImage(path);
        reloaded.Should().NotBeNull();
        reloaded!.Width.Should().Be(9);
        reloaded.Height.Should().Be(4);
        reloaded.GetPixel(8, 3).Should().Be(source.GetPixel(8, 3));
    }

    [Fact]
    public void Write_Gif_IsRefusedWithAClearMessage_AndLeavesNoFile()
    {
        using var source = CreatePattern(2, 2);
        var path = Path.Combine(_dir, "refused.gif");

        var act = () => ImageFileWriter.Write(source, path);

        act.Should().Throw<NotSupportedException>().WithMessage("*.gif*GIF encoder*");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Write_UnknownExtension_IsRefused()
    {
        using var source = CreatePattern(2, 2);

        var act = () => ImageFileWriter.Write(source, Path.Combine(_dir, "x.xyz"));

        act.Should().Throw<NotSupportedException>().WithMessage("*.xyz*");
    }

    [Theory]
    [InlineData("a.PNG", true)]
    [InlineData("a.Tiff", true)]
    [InlineData("a.bmp", true)]
    [InlineData("a.gif", false)]
    [InlineData("a", false)]
    public void CanWrite_FollowsTheExtension(string name, bool expected)
        => ImageFileWriter.CanWrite(name).Should().Be(expected);

    [Theory]
    [InlineData(".bmp")]
    [InlineData(".tif")]
    public void Write_PremultipliedSemiTransparentInput_StoresStraightAlpha(string extension)
    {
        // Image-editor bitmaps are premultiplied BGRA. Half-transparent pure red is stored
        // premultiplied as (128, 0, 0, 128); written as-is it reloads darkened.
        using var premul = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        premul.Erase(new SKColor(255, 0, 0, 128));
        var path = Path.Combine(_dir, "half-red" + extension);

        ImageFileWriter.Write(premul, path);

        if (extension == ".bmp")
        {
            // Skia's (and Windows') BMP reader treats a BI_RGB 32-bit file as opaque, so read the
            // stored bytes: first pixel after the 54-byte header, BGRA order.
            File.ReadAllBytes(path).AsSpan(54, 4).ToArray().Should().Equal(new byte[] { 0, 0, 255, 128 });
        }
        else
        {
            // Read the stored scanline, not LibTiff's RGBA interface: that interface premultiplies
            // unassociated alpha on read (a separate reader issue, tracked in #638).
            using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(path, "r")!;
            var row = new byte[2 * 4];
            tiff.ReadScanline(row, 0).Should().BeTrue();
            row.AsSpan(0, 4).ToArray().Should().Equal(new byte[] { 255, 0, 0, 128 });
        }
    }

    [Fact]
    public void Write_Png_WithMetadataSource_KeepsTheGenerationTextChunks()
    {
        // ComfyUI and A1111 store their prompt/workflow as tEXt chunks; the colour fixer rewrites
        // such files in place and must not strip them.
        var source = Path.Combine(_dir, "comfy.png");
        using (var bmp = CreatePattern(8, 8)) ImageFileWriter.Write(bmp, source);
        File.WriteAllBytes(source, InsertPngChunkBeforeIdat(File.ReadAllBytes(source), "tEXt", "parameters\0steps: 20, sampler: euler"u8.ToArray()));
        File.WriteAllBytes(source, InsertPngChunkBeforeIdat(File.ReadAllBytes(source), "tEXt", "workflow\0{\"nodes\":[]}"u8.ToArray()));

        using (var reloaded = SkiaPixels.DecodeRgba(source))
        {
            ImageFileWriter.Write(reloaded, source, metadataSource: source);
        }

        var texts = ReadPngTextChunks(File.ReadAllBytes(source));
        texts.Should().Contain("parameters\0steps: 20, sampler: euler");
        texts.Should().Contain("workflow\0{\"nodes\":[]}");
        using var decodable = SkiaPixels.DecodeRgba(source);
        decodable.Width.Should().Be(8);
    }

    [Fact]
    public void Write_Jpeg_WithMetadataSource_KeepsTheExifOrientation()
    {
        // A phone photo stores rotation in EXIF and leaves the pixels unrotated; dropping the tag
        // turns the image sideways in every viewer and trainer.
        var source = Path.Combine(_dir, "phone.jpg");
        using (var bmp = CreatePattern(16, 8)) ImageFileWriter.Write(bmp, source, lossyQuality: 90);
        File.WriteAllBytes(source, InsertExifOrientation(File.ReadAllBytes(source), orientation: 6));
        OriginOf(source).Should().Be(SKEncodedOrigin.RightTop, "the fixture must carry the tag");

        using (var reloaded = SkiaPixels.DecodeRgba(source))
        {
            ImageFileWriter.Write(reloaded, source, metadataSource: source);
        }

        OriginOf(source).Should().Be(SKEncodedOrigin.RightTop);
    }

    [Fact]
    public void Write_Jpeg_WithMetadataSourceAndNoQuality_KeepsTheSourceQuality()
    {
        var source = Path.Combine(_dir, "q95.jpg");
        var reference = Path.Combine(_dir, "reference-q95.jpg");
        using (var bmp = CreatePattern(32, 32))
        {
            ImageFileWriter.Write(bmp, source, lossyQuality: 95);
            ImageFileWriter.Write(bmp, reference, lossyQuality: 95);
        }

        using (var reloaded = SkiaPixels.DecodeRgba(source))
        {
            ImageFileWriter.Write(reloaded, source, metadataSource: source);
        }

        QuantTables(File.ReadAllBytes(source)).Should().Equal(QuantTables(File.ReadAllBytes(reference)),
            "a q95 JPEG must be re-encoded at q95, not at the default 75");
    }

    [Fact]
    public void Write_Webp_WithLosslessSource_StaysLossless()
    {
        var source = Path.Combine(_dir, "lossless.webp");
        using (var bmp = CreatePattern(8, 8))
        using (var pixmap = bmp.PeekPixels())
        using (var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100)))
        {
            File.WriteAllBytes(source, data.ToArray());
        }
        WebpFourCc(source).Should().Be("VP8L", "the fixture must be lossless");

        using (var reloaded = SkiaPixels.DecodeRgba(source))
        {
            ImageFileWriter.Write(reloaded, source, metadataSource: source);
        }

        WebpFourCc(source).Should().Be("VP8L");
    }

    [Fact]
    public void Write_OverExistingFile_ReplacesItAndLeavesNoTemporaryFiles()
    {
        var path = Path.Combine(_dir, "target.tif");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var bmp = CreatePattern(4, 4);

        ImageFileWriter.Write(bmp, path);

        Directory.GetFiles(_dir).Should().ContainSingle().Which.Should().Be(path);
        using var reloaded = Load(path);
        reloaded.Width.Should().Be(4);
    }

    [Fact]
    public void Write_RefusedFormat_LeavesAnExistingFileUntouched()
    {
        var path = Path.Combine(_dir, "keep.gif");
        File.WriteAllBytes(path, [7, 7, 7]);
        using var bmp = CreatePattern(4, 4);

        var act = () => ImageFileWriter.Write(bmp, path);

        act.Should().Throw<NotSupportedException>();
        File.ReadAllBytes(path).Should().Equal(new byte[] { 7, 7, 7 });
    }

    [Fact]
    public void Write_WithAnimatedMetadataSource_IsRefusedInsteadOfKeepingOnlyTheFirstFrame()
    {
        // Two 1x1 frames. Skia decodes one frame, so writing the result back over an animation
        // would silently throw every other frame away.
        var source = Path.Combine(_dir, "anim.gif");
        File.WriteAllBytes(source, TwoFrameGif());
        using (var codec = SKCodec.Create(source)) codec.FrameCount.Should().Be(2, "the fixture must be animated");
        var target = Path.Combine(_dir, "anim-out.png");
        using var bmp = CreatePattern(1, 1);

        var act = () => ImageFileWriter.Write(bmp, target, metadataSource: source);

        act.Should().Throw<NotSupportedException>().WithMessage("*animated*");
        File.Exists(target).Should().BeFalse();
    }

    private static byte[] TwoFrameGif()
    {
        byte[] frame =
        [
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,       // graphic control: 100 ms delay
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // image descriptor 1x1
            0x02, 0x02, 0x44, 0x01, 0x00,                         // LZW min size 2, one data block
        ];
        return
        [
            .. "GIF89a"u8, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, // 1x1, global colour table of 2
            0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF,                      // red, blue
            .. frame, .. frame,
            0x3B,
        ];
    }

    private static SKEncodedOrigin OriginOf(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec.EncodedOrigin;
    }

    /// <summary>
    /// The FourCC of the WebP image-data chunk: "VP8L" lossless, "VP8 " lossy. Skips the
    /// extended-format container (VP8X, ICCP, ...) Skia adds when a colour profile is present.
    /// </summary>
    private static string WebpFourCc(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int p = 12;
        while (p + 8 <= bytes.Length)
        {
            var fourCc = System.Text.Encoding.ASCII.GetString(bytes, p, 4);
            if (fourCc is "VP8L" or "VP8 ") return fourCc;
            int size = BitConverter.ToInt32(bytes, p + 4);
            p += 8 + size + (size & 1);
        }
        return "none";
    }

    /// <summary>Concatenated DQT segment payloads, i.e. the quantisation tables the encoder chose.</summary>
    private static byte[] QuantTables(byte[] jpeg)
    {
        var tables = new List<byte>();
        int p = 2;
        while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF && jpeg[p + 1] != 0xDA)
        {
            int length = (jpeg[p + 2] << 8) | jpeg[p + 3];
            if (jpeg[p + 1] == 0xDB) tables.AddRange(jpeg.AsSpan(p + 4, length - 2).ToArray());
            p += 2 + length;
        }
        return tables.ToArray();
    }

    /// <summary>Inserts an APP1 Exif segment holding only IFD0/Orientation right after the SOI marker.</summary>
    private static byte[] InsertExifOrientation(byte[] jpeg, short orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF header, IFD0 at 8
            0x01, 0x00,                                               // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,           // tag 0x0112 Orientation, SHORT, count 1
            (byte)orientation, 0x00, 0x00, 0x00,                      // value
            0x00, 0x00, 0x00, 0x00,                                   // no next IFD
        ];
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        int length = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }

    private static byte[] InsertPngChunkBeforeIdat(byte[] png, string type, byte[] data)
    {
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int length = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            if (System.Text.Encoding.ASCII.GetString(png, p + 4, 4) == "IDAT") break;
            p += 12 + length;
        }
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var crc = Crc32(typeBytes.Concat(data).ToArray());
        var chunk = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }
            .Concat(typeBytes).Concat(data)
            .Concat(new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        return png.Take(p).Concat(chunk).Concat(png.Skip(p)).ToArray();
    }

    private static List<string> ReadPngTextChunks(byte[] png)
    {
        var texts = new List<string>();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int length = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            if (System.Text.Encoding.ASCII.GetString(png, p + 4, 4) == "tEXt")
                texts.Add(System.Text.Encoding.Latin1.GetString(png, p + 8, length));
            p += 12 + length;
        }
        return texts;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Opaque RGBA pattern with a distinct colour per pixel so a swapped channel or flipped row shows.</summary>
    private static SKBitmap CreatePattern(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                rgba[i] = (byte)(x * 37 + 10);
                rgba[i + 1] = (byte)(y * 53 + 20);
                rgba[i + 2] = (byte)(x * 11 + y * 29 + 30);
                rgba[i + 3] = 255;
            }
        }
        return SkiaPixels.FromRgbaBytes(rgba, width, height);
    }

    /// <summary>Loads PNG/BMP through Skia and TIFF through the editor's LibTiff reader, as unpremultiplied RGBA.</summary>
    private static SKBitmap Load(string path)
    {
        if (Path.GetExtension(path).StartsWith(".tif", StringComparison.OrdinalIgnoreCase))
        {
            using var tiff = TiffExporter.DecodeFlatImage(path) ?? throw new InvalidOperationException("TIFF decode failed");
            // Copy(Rgba8888) would keep the reader's premultiplied alpha; read into straight RGBA.
            var info = new SKImageInfo(tiff.Width, tiff.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var straight = new SKBitmap(info);
            using var pixmap = tiff.PeekPixels();
            pixmap.ReadPixels(info, straight.GetPixels(), straight.RowBytes, 0, 0).Should().BeTrue();
            return straight;
        }
        return SkiaPixels.DecodeRgba(path);
    }
}
