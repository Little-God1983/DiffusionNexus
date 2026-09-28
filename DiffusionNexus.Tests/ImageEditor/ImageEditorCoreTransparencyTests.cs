using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// JPEG cannot store transparency (#584). <see cref="ImageEditorCore.HasTransparency"/> answers
/// "would a JPEG lose something?" for the image as it would be written — the flattened result,
/// not each layer on its own — and <see cref="ImageEditorCore.SaveImage"/>'s fill colour decides
/// what the transparent areas become instead of the encoder's black.
/// </summary>
public class ImageEditorCoreTransparencyTests : IDisposable
{
    private readonly ImageEditorCore _sut;
    private readonly DirectoryInfo _tempDir;

    public ImageEditorCoreTransparencyTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();

        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(100, 80, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
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

    private void ExtendRightBy(int pixels)
    {
        _sut.CanvasExtendTool.IsActive = true;
        _sut.CanvasExtendTool.ImagePixelWidth = _sut.Width;
        _sut.CanvasExtendTool.ImagePixelHeight = _sut.Height;
        _sut.CanvasExtendTool.SetExtension(top: 0, right: pixels, bottom: 0, left: 0);
        _sut.ApplyCanvasExtend().Should().BeTrue();
    }

    private SKColor SaveJpegAndReadPixel(SKColor? fillColor, int x, int y)
    {
        var path = Path.Combine(_tempDir.FullName, "export.jpg");
        _sut.SaveImage(path, SKEncodedImageFormat.Jpeg, 95, fillColor).Should().BeTrue();

        using var decoded = SKBitmap.Decode(path);
        return decoded.GetPixel(x, y);
    }

    [Fact]
    public void WhenEveryPixelIsOpaqueThenThereIsNoTransparency()
    {
        _sut.HasTransparency().Should().BeFalse();
    }

    [Fact]
    public void WhenAnEmptyLayerSitsOnAnOpaqueBackgroundThenThereIsNoTransparency()
    {
        // A new layer (or a text layer) is almost entirely transparent, but the opaque layer
        // under it shows through everywhere, so the JPEG loses nothing.
        _sut.AddLayer("Empty").Should().NotBeNull();

        _sut.HasTransparency().Should().BeFalse();
    }

    [Fact]
    public void WhenTheCanvasWasExtendedThenTheNewStripIsTransparent()
    {
        ExtendRightBy(20);

        _sut.HasTransparency().Should().BeTrue();
    }

    [Fact]
    public void WhenTheOnlyLayerIsHalfOpaqueThenThereIsTransparency()
    {
        // Partial alpha is transparency too: the encoder would darken it towards black.
        _sut.Layers![0].Opacity = 0.5f;

        _sut.HasTransparency().Should().BeTrue();
    }

    [Fact]
    public void WhenLayerModeIsOffThenTheWorkingBitmapIsChecked()
    {
        _sut.DisableLayerMode();
        ExtendRightBy(20);

        _sut.HasTransparency().Should().BeTrue();
    }

    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    public void WhenSavingAJpegWithAFillColourThenTransparentAreasTakeThatColour(byte r, byte g, byte b)
    {
        ExtendRightBy(20);

        var strip = SaveJpegAndReadPixel(new SKColor(r, g, b), x: 110, y: 40);
        var original = SaveJpegAndReadPixel(new SKColor(r, g, b), x: 50, y: 40);

        // JPEG is lossy: allow a small tolerance per channel.
        strip.Red.Should().BeCloseTo(r, 8);
        strip.Green.Should().BeCloseTo(g, 8);
        strip.Blue.Should().BeCloseTo(b, 8);
        original.Red.Should().BeCloseTo(255, 8);
        original.Green.Should().BeCloseTo(0, 8);
        original.Blue.Should().BeCloseTo(0, 8);
    }

    [Fact]
    public void WhenSavingAJpegWithAFillColourThenHalfTransparentPixelsBlendIntoIt()
    {
        // 50% red over white is (255, 128, 128). Dropping the alpha instead of blending would
        // give the premultiplied (128, 0, 0) — the dark red the encoder produces on its own.
        _sut.Layers![0].Opacity = 0.5f;

        var pixel = SaveJpegAndReadPixel(SKColors.White, x: 50, y: 40);

        pixel.Red.Should().BeCloseTo(255, 8);
        pixel.Green.Should().BeCloseTo(128, 8);
        pixel.Blue.Should().BeCloseTo(128, 8);
    }

    [Fact]
    public void WhenCheckingForTransparencyThenTheLayersAreNotFlattened()
    {
        ExtendRightBy(20);
        _sut.AddLayer("Top").Should().NotBeNull();

        _sut.HasTransparency();

        _sut.Layers!.Count.Should().Be(2);
        _sut.Layers[0].Bitmap!.GetPixel(110, 40).Alpha.Should().Be(0);
    }
}
