using DiffusionNexus.Inference.Captioning;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.Inference.Captioning;

/// <summary>
/// Unit tests for <see cref="ImagePreprocessor"/>. Uses real SkiaSharp encoders to
/// generate PNG/JPEG/WebP fixtures in a temp directory so the codec path is
/// exercised end-to-end without touching the network or a model.
/// </summary>
public sealed class ImagePreprocessorTests : IDisposable
{
    private readonly string _root;

    public ImagePreprocessorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-imgpre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Writes a valid solid-colour image of the requested size and format into the
    /// temp directory. Returns the absolute path.
    /// </summary>
    private string WriteImage(string fileName, int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var path = Path.Combine(_root, fileName);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x80, 0x40, 0xC0));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90)
            ?? throw new InvalidOperationException("Test fixture: SKImage.Encode returned null.");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    #region ImagePreprocessResult factories

    [Fact]
    public void Succeeded_Factory_SetsAllFields()
    {
        var bytes = new byte[] { 1, 2, 3 };

        var result = ImagePreprocessResult.Succeeded(bytes, 640, 480, wasResized: true);

        result.Success.Should().BeTrue();
        result.ImageData.Should().BeSameAs(bytes);
        result.Width.Should().Be(640);
        result.Height.Should().Be(480);
        result.WasResized.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failed_Factory_CarriesErrorAndZerosTheRest()
    {
        var result = ImagePreprocessResult.Failed("nope");

        result.Success.Should().BeFalse();
        result.ImageData.Should().BeNull();
        result.Width.Should().Be(0);
        result.Height.Should().Be(0);
        result.WasResized.Should().BeFalse();
        result.ErrorMessage.Should().Be("nope");
    }

    #endregion

    #region ValidateImageFile

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateImageFile_RejectsNullOrWhitespacePath(string? path)
    {
        var (isValid, error) = ImagePreprocessor.ValidateImageFile(path!);
        isValid.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ValidateImageFile_RejectsMissingFile()
    {
        var missing = Path.Combine(_root, "does-not-exist.png");

        var (isValid, error) = ImagePreprocessor.ValidateImageFile(missing);

        isValid.Should().BeFalse();
        error.Should().Contain("does not exist");
    }

    [Fact]
    public void ValidateImageFile_RejectsUnsupportedExtension()
    {
        var path = Path.Combine(_root, "readme.txt");
        File.WriteAllText(path, new string('x', 200));

        var (isValid, error) = ImagePreprocessor.ValidateImageFile(path);

        isValid.Should().BeFalse();
        error.Should().Contain("Unsupported file format");
    }

    [Fact]
    public void ValidateImageFile_RejectsTooSmallFile()
    {
        // < 100 bytes — the configured minimum for an image-shaped payload.
        var path = Path.Combine(_root, "tiny.png");
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var (isValid, error) = ImagePreprocessor.ValidateImageFile(path);

        isValid.Should().BeFalse();
        error.Should().Contain("too small");
    }

    [Fact]
    public void ValidateImageFile_AcceptsValidPng()
    {
        var path = WriteImage("ok.png", 64, 64);

        var (isValid, error) = ImagePreprocessor.ValidateImageFile(path);

        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    #endregion

    #region ProcessImage

    [Fact]
    public void ProcessImage_RejectsBelowMinDimension()
    {
        // The validator only checks file size, so we have to bypass it with a real
        // (small but ≥100 byte) PNG whose decoded dimensions are below the 16-px floor.
        var path = WriteImage("tiny-decoded.png", 8, 8);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too small");
    }

    [Fact]
    public void ProcessImage_KeepsDimensionsWhenWithinCap()
    {
        var path = WriteImage("medium.png", 640, 480);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeTrue();
        result.Width.Should().Be(640);
        result.Height.Should().Be(480);
        result.WasResized.Should().BeFalse();
        result.ImageData.Should().NotBeNull();
        result.ImageData!.Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public void ProcessImage_ResizesPreservingAspectRatioWhenOverCap()
    {
        // Source 2560×1280 (landscape, longer side over 1280 cap).
        var path = WriteImage("big-landscape.png", 2560, 1280);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeTrue();
        result.WasResized.Should().BeTrue();
        // Longest side should clamp to MaxImageDimension; shorter side scales.
        result.Width.Should().Be(ImagePreprocessor.MaxImageDimension);
        result.Height.Should().Be(ImagePreprocessor.MaxImageDimension / 2);
    }

    [Fact]
    public void ProcessImage_EncodesAsJpegRegardlessOfSourceFormat()
    {
        // A PNG source should still come back as JPEG bytes (codec quirk handled).
        var path = WriteImage("source.png", 256, 256);

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeTrue();
        result.ImageData.Should().NotBeNull();
        // JPEG SOI marker.
        result.ImageData![0].Should().Be(0xFF);
        result.ImageData![1].Should().Be(0xD8);
    }

    [Fact]
    public void ProcessImage_FailsOnCorruptHeader()
    {
        var path = Path.Combine(_root, "broken.jpg");
        File.WriteAllBytes(path, new byte[200]);  // 200 zero-bytes — not a valid JPEG.

        var result = ImagePreprocessor.ProcessImage(path);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region ProcessImageFromBytes

    [Fact]
    public void ProcessImageFromBytes_ThrowsOnNull()
    {
        var act = () => ImagePreprocessor.ProcessImageFromBytes(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessImageFromBytes_RejectsTooSmallBuffer()
    {
        var result = ImagePreprocessor.ProcessImageFromBytes(new byte[] { 1, 2, 3 });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too small");
    }

    [Fact]
    public void ProcessImageFromBytes_AcceptsValidPng()
    {
        var path = WriteImage("from-bytes.png", 256, 256);
        var bytes = File.ReadAllBytes(path);

        var result = ImagePreprocessor.ProcessImageFromBytes(bytes);

        result.Success.Should().BeTrue();
        result.Width.Should().Be(256);
        result.Height.Should().Be(256);
        result.WasResized.Should().BeFalse();
        result.ImageData.Should().NotBeNull();
    }

    [Fact]
    public void ProcessImageFromBytes_RejectsBelowMinDimension()
    {
        var path = WriteImage("tiny-from-bytes.png", 8, 8);
        var bytes = File.ReadAllBytes(path);

        var result = ImagePreprocessor.ProcessImageFromBytes(bytes);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too small");
    }

    #endregion

    #region Supported-format helpers

    [Theory]
    [InlineData(".png", true)]
    [InlineData(".PNG", true)]
    [InlineData(".jpg", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".webp", true)]
    [InlineData(".bmp", true)]
    [InlineData(".gif", true)]
    [InlineData(".tiff", false)]
    [InlineData(".txt", false)]
    [InlineData("", false)]
    public void IsSupportedImageFormat_RespectsExtensionWhitelist(string extension, bool expected)
    {
        ImagePreprocessor.IsSupportedImageFormat("img" + extension).Should().Be(expected);
    }

    [Fact]
    public void GetSupportedExtensions_ReturnsTheSameSetUsedForValidation()
    {
        var extensions = ImagePreprocessor.GetSupportedExtensions();

        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".png");
        extensions.Should().Contain(".jpg");
        // Make sure callers can iterate the result without surprises.
        extensions.Count.Should().Be(extensions.Distinct().Count());
    }

    #endregion
}
