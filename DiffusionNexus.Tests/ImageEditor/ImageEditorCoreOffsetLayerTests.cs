using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Pixel tools address the active layer in CANVAS coordinates; a layer with an offset receives
/// the paint at the right place and nothing lands outside its bounds.
/// </summary>
public class ImageEditorCoreOffsetLayerTests : IDisposable
{
    private readonly ImageEditorCore _sut = new();
    private readonly EditorServices _services = EditorServiceFactory.Create();

    public ImageEditorCoreOffsetLayerTests()
    {
        _sut.SetServices(_services);
        using var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());
        _sut.EnableLayerMode();
    }

    public void Dispose() => _sut.Dispose();

    private Layer AddOffsetLayer()
    {
        using var small = new SKBitmap(20, 20, SKColorType.Rgba8888, SKAlphaType.Premul);
        small.Erase(SKColors.Transparent);
        var layer = _services.Layers.AddLayerFromBitmap(small, "small", new SKPointI(50, 50))!;
        _sut.ActiveLayer = layer;
        return layer;
    }

    [Fact]
    public void ApplyStroke_OnOffsetLayer_LandsInLayerLocalPixels()
    {
        var layer = AddOffsetLayer();
        // Canvas point (60,60) => layer-local (10,10). Brush 4 px of 100 => 0.04.
        _sut.ApplyStroke([new SKPoint(0.6f, 0.6f)], SKColors.Red, 0.04f, BrushShape.Square).Should().BeTrue();

        layer.Bitmap!.GetPixel(10, 10).Red.Should().BeGreaterThan(200);
        layer.Bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void ApplyStroke_OutsideLayerBounds_DrawsNothing()
    {
        var layer = AddOffsetLayer();
        _sut.ApplyStroke([new SKPoint(0.1f, 0.1f)], SKColors.Red, 0.04f, BrushShape.Square).Should().BeTrue();
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                layer.Bitmap!.GetPixel(x, y).Alpha.Should().Be(0);
    }

    [Fact]
    public void ApplyShape_OnOffsetLayer_UsesCanvasCoordinates()
    {
        var layer = AddOffsetLayer();
        var shape = new ShapeData
        {
            ShapeType = ShapeType.Rectangle, FillMode = ShapeFillMode.Fill,
            StrokeColor = SKColors.Blue, FillColor = SKColors.Blue, StrokeWidth = 0.01f,
            NormalizedStart = new SKPoint(0.55f, 0.55f), NormalizedEnd = new SKPoint(0.65f, 0.65f)
        };
        _sut.ApplyShape(shape).Should().BeTrue();
        layer.Bitmap!.GetPixel(10, 10).Blue.Should().BeGreaterThan(200);
        layer.Bitmap!.GetPixel(1, 1).Alpha.Should().Be(0);
    }
}
