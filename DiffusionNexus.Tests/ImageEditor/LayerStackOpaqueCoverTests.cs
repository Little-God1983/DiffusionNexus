using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// <see cref="LayerStack.HasOpaqueCoveringLayer"/> lets the JPEG transparency check (#584) skip the
/// full flatten: one visible, fully opaque layer over the whole canvas makes the result opaque
/// whatever else is stacked with it. Any doubt must answer false, so the flatten decides.
/// </summary>
public class LayerStackOpaqueCoverTests : IDisposable
{
    private readonly LayerStack _stack = new(40, 30);

    public void Dispose()
    {
        _stack.Dispose();
        GC.SuppressFinalize(this);
    }

    private Layer AddSolid(int width, int height, SKColor color, SKPointI offset = default)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        return _stack.AddLayerFromBitmap(bitmap, offset: offset);
    }

    [Fact]
    public void WhenAnOpaqueLayerFillsTheCanvasThenItCovers()
    {
        AddSolid(40, 30, SKColors.Red);

        _stack.HasOpaqueCoveringLayer().Should().BeTrue();
    }

    [Fact]
    public void WhenAHalfTransparentLayerSitsAboveTheOpaqueOneThenItStillCovers()
    {
        // Every blend mode keeps the result alpha at Sa + Da(1 - Sa): nothing drawn over an
        // opaque pixel can make it transparent again.
        AddSolid(40, 30, SKColors.Red);
        AddSolid(40, 30, SKColors.Blue.WithAlpha(64)).BlendMode = BlendMode.Difference;

        _stack.HasOpaqueCoveringLayer().Should().BeTrue();
    }

    [Fact]
    public void WhenALargerOpaqueLayerOverhangsTheCanvasThenItCovers()
    {
        AddSolid(60, 50, SKColors.Red, new SKPointI(-10, -10));

        _stack.HasOpaqueCoveringLayer().Should().BeTrue();
    }

    [Fact]
    public void WhenTheLayerIsBelowFullOpacityThenItDoesNotCover()
    {
        AddSolid(40, 30, SKColors.Red).Opacity = 0.99f;

        _stack.HasOpaqueCoveringLayer().Should().BeFalse();
    }

    [Fact]
    public void WhenTheLayerIsHiddenThenItDoesNotCover()
    {
        AddSolid(40, 30, SKColors.Red).IsVisible = false;

        _stack.HasOpaqueCoveringLayer().Should().BeFalse();
    }

    [Fact]
    public void WhenTheOpaqueLayerIsTheInpaintMaskThenItDoesNotCover()
    {
        // The mask is never part of the flattened image.
        AddSolid(40, 30, SKColors.Red).IsInpaintMask = true;

        _stack.HasOpaqueCoveringLayer().Should().BeFalse();
    }

    [Fact]
    public void WhenTheLayerIsOffsetAndLeavesAGapThenItDoesNotCover()
    {
        AddSolid(40, 30, SKColors.Red, new SKPointI(1, 0));

        _stack.HasOpaqueCoveringLayer().Should().BeFalse();
    }

    [Fact]
    public void WhenOnePixelOfTheLayerIsTransparentThenItDoesNotCover()
    {
        AddSolid(40, 30, SKColors.Red).Bitmap!.SetPixel(39, 29, SKColors.Transparent);

        _stack.HasOpaqueCoveringLayer().Should().BeFalse();
    }

    [Fact]
    public void WhenTheOnlyOpaqueLayerIsExceptedThenNothingCovers()
    {
        // A layer with a pending transform: its committed pixels will be different.
        var layer = AddSolid(40, 30, SKColors.Red);

        _stack.HasOpaqueCoveringLayer(except: layer).Should().BeFalse();
    }
}
