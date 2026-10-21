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
            return tiff.Copy(SKColorType.Rgba8888)!;
        }
        return SkiaPixels.DecodeRgba(path);
    }
}
