using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// A layer has a position (OffsetX/OffsetY in canvas pixels) and its own size; Bounds combines
/// them. Offsets default to zero so untouched documents behave exactly as before.
/// </summary>
public class LayerBoundsTests
{
    private static SKBitmap Red(int w, int h)
    {
        var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        b.Erase(SKColors.Red);
        return b;
    }

    [Fact]
    public void NewLayer_HasZeroOffset_AndCanvasBounds()
    {
        using var layer = new Layer(40, 30, "L");
        layer.OffsetX.Should().Be(0);
        layer.OffsetY.Should().Be(0);
        layer.Bounds.Should().Be(new SKRectI(0, 0, 40, 30));
    }

    [Fact]
    public void OffsetConstructor_PlacesTheLayer()
    {
        using var src = Red(10, 8);
        using var layer = new Layer(src, "L", new SKPointI(-3, 5));
        layer.Bounds.Should().Be(new SKRectI(-3, 5, 7, 13));
    }

    [Fact]
    public void AdoptBitmapWithOffset_SwapsBoth_AndRaisesContentChanged()
    {
        using var layer = new Layer(10, 10, "L");
        var raised = 0;
        layer.ContentChanged += (_, _) => raised++;
        var replacement = Red(4, 6);

        layer.AdoptBitmap(replacement, new SKPointI(20, -2));

        layer.Bitmap.Should().BeSameAs(replacement);
        layer.Bounds.Should().Be(new SKRectI(20, -2, 24, 4));
        raised.Should().Be(1);
    }

    [Fact]
    public void SetOffset_RaisesPropertyChangedOffset_AndContentChanged()
    {
        using var layer = new Layer(10, 10, "L");
        string? prop = null;
        var content = 0;
        layer.PropertyChanged += (_, p) => prop = p;
        layer.ContentChanged += (_, _) => content++;

        layer.SetOffset(7, 9);

        prop.Should().Be("Offset");
        content.Should().Be(1);
        layer.OffsetX.Should().Be(7);
        layer.OffsetY.Should().Be(9);
    }

    [Fact]
    public void SetOffset_SameValue_RaisesNothing()
    {
        using var layer = new Layer(10, 10, "L");
        var content = 0;
        layer.ContentChanged += (_, _) => content++;
        layer.SetOffset(0, 0);
        content.Should().Be(0);
    }

    [Fact]
    public void Clone_CopiesOffset()
    {
        using var src = Red(5, 5);
        using var layer = new Layer(src, "L", new SKPointI(11, 12));
        using var clone = layer.Clone();
        clone.Bounds.Should().Be(layer.Bounds);
    }

    [Fact]
    public void ReplaceBitmap_KeepsOffset()
    {
        using var src = Red(5, 5);
        using var layer = new Layer(src, "L", new SKPointI(11, 12));
        layer.ReplaceBitmap(Red(5, 5));
        layer.OffsetX.Should().Be(11);
        layer.OffsetY.Should().Be(12);
    }
}
