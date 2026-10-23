using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Compositing honours each layer's offset, clips to the canvas, and can preview one layer
/// through a matrix override without touching the layer.
/// </summary>
public class LayerCompositorOffsetTests : IDisposable
{
    private readonly LayerStack _stack = new(20, 20);

    public void Dispose() => _stack.Dispose();

    private static SKBitmap Solid(int w, int h, SKColor c)
    {
        var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        b.Erase(c);
        return b;
    }

    private SKBitmap Composite(LayerRenderOverride? over = null)
    {
        var result = new SKBitmap(20, 20, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        LayerCompositor.CompositeToCanvas(canvas, _stack, new SKRect(0, 0, 20, 20), over);
        return result;
    }

    [Fact]
    public void LayerAtOffset_LandsAtOffset()
    {
        using var src = Solid(4, 4, SKColors.Red);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(10, 5));

        using var result = Composite();

        result.GetPixel(10, 5).Should().Be(SKColors.Red);
        result.GetPixel(13, 8).Should().Be(SKColors.Red);
        result.GetPixel(9, 5).Alpha.Should().Be(0);
        result.GetPixel(14, 8).Alpha.Should().Be(0);
    }

    [Fact]
    public void ContentOutsideCanvas_IsClipped_AndDoesNotWrap()
    {
        using var src = Solid(30, 30, SKColors.Blue);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(-15, -15));

        using var result = Composite();

        result.GetPixel(0, 0).Should().Be(SKColors.Blue);
        result.GetPixel(14, 14).Should().Be(SKColors.Blue);
        result.GetPixel(15, 15).Alpha.Should().Be(0);
    }

    [Fact]
    public void Override_MovesOnlyThatLayer_AndLeavesTheLayerUntouched()
    {
        using var red = Solid(4, 4, SKColors.Red);
        using var green = Solid(4, 4, SKColors.Green);
        var redLayer = _stack.AddLayerFromBitmap(red, "R", new SKPointI(0, 0));
        _stack.AddLayerFromBitmap(green, "G", new SKPointI(10, 10));

        using var result = Composite(new LayerRenderOverride(redLayer, SKMatrix.CreateTranslation(5, 0)));

        result.GetPixel(0, 0).Alpha.Should().Be(0);
        result.GetPixel(5, 0).Should().Be(SKColors.Red);
        result.GetPixel(10, 10).Should().Be(SKColors.Green);
        redLayer.Bounds.Should().Be(new SKRectI(0, 0, 4, 4));
    }

    [Fact]
    public void FlattenedBitmap_HonoursOffset()
    {
        using var src = Solid(2, 2, SKColors.Red);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(3, 4));

        using var flat = LayerCompositor.CreateFlattenedBitmap(_stack);

        flat.GetPixel(3, 4).Should().Be(SKColors.Red);
        flat.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void ExportLayersAsBitmaps_ReportsOffsets()
    {
        using var src = Solid(2, 2, SKColors.Red);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(3, 4));

        var exported = LayerCompositor.ExportLayersAsBitmaps(_stack);

        exported.Should().ContainSingle().Which.Offset.Should().Be(new SKPointI(3, 4));
        foreach (var e in exported) e.Bitmap.Dispose();
    }
}
