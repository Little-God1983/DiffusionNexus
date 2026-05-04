using DiffusionNexus.Captioning;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.Captioning;

public class ImagePreprocessorTests : IDisposable
{
    private readonly string _workDir;

    public ImagePreprocessorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "dn-imgpre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static byte[] CreateImageBytes(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.SkyBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private string CreateImageFile(string fileName, int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var path = Path.Combine(_workDir, fileName);
        File.WriteAllBytes(path, CreateImageBytes(width, height, format));
        return path;
    }

    [Fact]
    public void ValidateImageFile_NullOrWhitespacePath_ReturnsInvalid()
    {
        var (ok, err) = ImagePreprocessor.ValidateImageFile("");
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateImageFile_FileDoesNotExist_ReturnsInvalid()
    {
        var (ok, err) = ImagePreprocessor.ValidateImageFile(Path.Combine(_workDir, "missing.png"));
        ok.Should().BeFalse();
        err.Should().Contain("does not exist");
    }

    [Fact]
    public void ValidateImageFile_UnsupportedExtension_ReturnsInvalid()
    {
        var path = Path.Combine(_workDir, "doc.txt");
        File.WriteAllText(path, "not an image but long enough");

        var (ok, err) = ImagePreprocessor.ValidateImageFile(path);

        ok.Should().BeFalse();
        err.Should().Contain("Unsupported");
    }

    [Fact]
    public void ValidateImageFile_FileTooSmall_ReturnsInvalid()
    {
        var path = Path.Combine(_workDir, "tiny.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var (ok, err) = ImagePreprocessor.ValidateImageFile(path);

        ok.Should().BeFalse();
        err.Should().Contain("too small");
    }

    [Fact]
    public void ValidateImageFile_ValidPngFile_ReturnsValid()
    {
        var path = CreateImageFile("ok.png", 64, 64);

        var (ok, err) = ImagePreprocessor.ValidateImageFile(path);

        ok.Should().BeTrue();
        err.Should().BeNull();
    }

    [Fact]
    public void ProcessImage_InvalidFile_ReturnsFailure()
    {
        var result = ImagePreprocessor.ProcessImage(Path.Combine(_workDir, "missing.png"));

        result.Success.Should().BeFalse();
        result.ImageData.Should().BeNull();
    }

    [Fact]
    public void ProcessImage_DimensionsTooSmall_ReturnsFailure()
    {
        var path = CreateImageFile("small.png", 8, 8);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too small");
    }

    [Fact]
    public void ProcessImage_BelowMaxDimension_DoesNotResize()
    {
        var path = CreateImageFile("medium.png", 256, 128);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeTrue();
        result.WasResized.Should().BeFalse();
        result.Width.Should().Be(256);
        result.Height.Should().Be(128);
        result.ImageData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ProcessImage_AboveMaxDimension_ResizesPreservingAspectRatio()
    {
        var path = CreateImageFile("big.png", 4000, 2000);

        var result = ImagePreprocessor.ProcessImage(path, maxDimension: 1024);

        result.Success.Should().BeTrue();
        result.WasResized.Should().BeTrue();
        result.Width.Should().Be(1024);
        result.Height.Should().Be(512);
    }

    [Fact]
    public void ProcessImage_PngExtension_EncodesAsPng()
    {
        var path = CreateImageFile("a.png", 64, 64);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeTrue();
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        result.ImageData!.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public void ProcessImage_JpegExtension_EncodesAsJpeg()
    {
        var path = CreateImageFile("a.jpg", 64, 64, SKEncodedImageFormat.Jpeg);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeTrue();
        // JPEG SOI marker: FF D8
        result.ImageData!.Take(2).Should().Equal(0xFF, 0xD8);
    }

    [Fact]
    public void ProcessImageFromBytes_NullData_Throws()
    {
        var act = () => ImagePreprocessor.ProcessImageFromBytes(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessImageFromBytes_TooSmall_ReturnsFailure()
    {
        var result = ImagePreprocessor.ProcessImageFromBytes(new byte[] { 1, 2, 3 });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too small");
    }

    [Fact]
    public void ProcessImageFromBytes_ValidImage_Succeeds()
    {
        var bytes = CreateImageBytes(128, 64, SKEncodedImageFormat.Png);

        var result = ImagePreprocessor.ProcessImageFromBytes(bytes);

        result.Success.Should().BeTrue();
        result.Width.Should().Be(128);
        result.Height.Should().Be(64);
        result.WasResized.Should().BeFalse();
    }

    [Fact]
    public void ProcessImageFromBytes_AboveMaxDimension_Resizes()
    {
        var bytes = CreateImageBytes(2000, 4000, SKEncodedImageFormat.Png);

        var result = ImagePreprocessor.ProcessImageFromBytes(bytes, maxDimension: 1024);

        result.Success.Should().BeTrue();
        result.WasResized.Should().BeTrue();
        result.Height.Should().Be(1024);
        result.Width.Should().Be(512);
    }

    [Fact]
    public void IsSupportedImageFormat_KnownExtensions()
    {
        ImagePreprocessor.IsSupportedImageFormat("a.png").Should().BeTrue();
        ImagePreprocessor.IsSupportedImageFormat("a.jpg").Should().BeTrue();
        ImagePreprocessor.IsSupportedImageFormat("a.txt").Should().BeFalse();
    }

    [Fact]
    public void GetSupportedExtensions_IsNonEmpty()
    {
        ImagePreprocessor.GetSupportedExtensions().Should().NotBeEmpty();
    }

    [Fact]
    public void MaxImageDimension_IsExposed()
    {
        ImagePreprocessor.MaxImageDimension.Should().Be(2048);
    }

    [Fact]
    public void ImagePreprocessResult_FactoryHelpers_SetExpectedFields()
    {
        var ok = ImagePreprocessResult.Succeeded(new byte[] { 1, 2 }, 10, 20, true);
        ok.Success.Should().BeTrue();
        ok.WasResized.Should().BeTrue();
        ok.Width.Should().Be(10);

        var fail = ImagePreprocessResult.Failed("oops");
        fail.Success.Should().BeFalse();
        fail.ImageData.Should().BeNull();
        fail.ErrorMessage.Should().Be("oops");
    }
}
