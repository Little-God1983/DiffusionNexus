using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Tests for how <see cref="ImageEditorCore.SaveImage"/> picks the encoder.
/// <para>
/// "Export as JPEG" passes <see cref="SKEncodedImageFormat.Jpeg"/> explicitly. That has to win:
/// the extension is only a fallback for callers that do not care. Deriving the format from the
/// path alone silently wrote a PNG whenever the user removed the ".jpg" from the suggested file
/// name in the save dialog.
/// </para>
/// </summary>
public class ImageEditorCoreSaveFormatTests : IDisposable
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    private readonly ImageEditorCore _sut;
    private readonly DirectoryInfo _tempDir;

    public ImageEditorCoreSaveFormatTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();

        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray()).Should().BeTrue();
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { _tempDir.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string fileName) => Path.Combine(_tempDir.FullName, fileName);

    private static void ShouldStartWith(string path, byte[] magic)
    {
        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Take(magic.Length).Should().Equal(magic);
    }

    [Theory]
    [InlineData("export-without-extension")]
    [InlineData("export.png")]
    [InlineData("export.unknown")]
    public void WhenJpegIsRequestedExplicitlyThenTheFileNameDoesNotOverrideIt(string fileName)
    {
        var path = PathFor(fileName);

        _sut.SaveImage(path, SKEncodedImageFormat.Jpeg).Should().BeTrue();

        ShouldStartWith(path, JpegMagic);
    }

    [Fact]
    public void WhenPngIsRequestedExplicitlyThenAJpegFileNameDoesNotOverrideIt()
    {
        var path = PathFor("export.jpg");

        _sut.SaveImage(path, SKEncodedImageFormat.Png).Should().BeTrue();

        ShouldStartWith(path, PngMagic);
    }

    [Fact]
    public void WhenNoFormatIsRequestedThenTheExtensionDecides()
    {
        var jpegPath = PathFor("from-extension.jpg");
        var pngPath = PathFor("from-extension.png");

        _sut.SaveImage(jpegPath).Should().BeTrue();
        _sut.SaveImage(pngPath).Should().BeTrue();

        ShouldStartWith(jpegPath, JpegMagic);
        ShouldStartWith(pngPath, PngMagic);
    }

    [Fact]
    public void WhenNoFormatIsRequestedAndTheExtensionIsUnknownThenPngIsWritten()
    {
        var path = PathFor("no-extension");

        _sut.SaveImage(path).Should().BeTrue();

        ShouldStartWith(path, PngMagic);
    }
}
