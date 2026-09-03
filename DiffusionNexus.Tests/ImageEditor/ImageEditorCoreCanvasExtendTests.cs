using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Applying a canvas extension grows every layer and the working bitmap, keeps the old
/// content at the offset, leaves the new pixels transparent, and resets the tool.
/// </summary>
public class ImageEditorCoreCanvasExtendTests : IDisposable
{
    private readonly ImageEditorCore _sut;

    public ImageEditorCoreCanvasExtendTests()
    {
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(100, 80, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());

        _sut.CanvasExtendTool.IsActive = true;
        _sut.CanvasExtendTool.ImagePixelWidth = 100;
        _sut.CanvasExtendTool.ImagePixelHeight = 80;
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void WithoutExtension_ReturnsFalse_AndChangesNothing()
    {
        _sut.ApplyCanvasExtend().Should().BeFalse();

        _sut.Width.Should().Be(100);
        _sut.Height.Should().Be(80);
    }

    [Fact]
    public void WithExtension_GrowsTheCanvas_KeepsContentAtOffset_NewPixelsTransparent()
    {
        _sut.CanvasExtendTool.SetExtension(top: 10, right: 20, bottom: 30, left: 40);
        var changed = 0;
        _sut.ImageChanged += (_, _) => changed++;

        _sut.ApplyCanvasExtend().Should().BeTrue();

        _sut.Width.Should().Be(160);
        _sut.Height.Should().Be(120);
        changed.Should().BeGreaterThanOrEqualTo(1);

        var layerBitmap = _sut.Layers![0].Bitmap!;
        layerBitmap.Width.Should().Be(160);
        layerBitmap.GetPixel(40, 10).Should().Be(SKColors.Red);      // old (0,0) moved to the offset
        layerBitmap.GetPixel(139, 89).Should().Be(SKColors.Red);     // old (99,79)
        layerBitmap.GetPixel(0, 0).Alpha.Should().Be(0);             // new area transparent
        layerBitmap.GetPixel(159, 119).Alpha.Should().Be(0);
    }

    [Fact]
    public void AfterApply_ToolIsReset()
    {
        _sut.CanvasExtendTool.SetExtension(0, 50, 0, 0);

        _sut.ApplyCanvasExtend();

        _sut.CanvasExtendTool.HasExtension.Should().BeFalse();
    }

    [Fact]
    public void ApplyTwice_Accumulates()
    {
        _sut.CanvasExtendTool.SetExtension(0, 50, 0, 0);
        _sut.ApplyCanvasExtend();
        _sut.CanvasExtendTool.ImagePixelWidth = _sut.Width; // the view refreshes this on every render
        _sut.CanvasExtendTool.SetExtension(0, 0, 0, 50);

        _sut.ApplyCanvasExtend().Should().BeTrue();

        _sut.Width.Should().Be(200);
    }

    [Fact]
    public void AbsurdExtension_ReturnsFalse_AndLeavesCanvasAndToolUntouched()
    {
        // Skia hands back an empty bitmap instead of throwing when the native allocation
        // fails, so the guard has to spot it. Either way the apply must fail cleanly.
        _sut.CanvasExtendTool.SetExtension(0, 1_000_000_000, 0, 0);

        _sut.ApplyCanvasExtend().Should().BeFalse();

        _sut.Width.Should().Be(100);
        _sut.Height.Should().Be(80);
        _sut.CanvasExtendTool.HasExtension.Should().BeTrue();
    }
}
