using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Stack-wide operations on layers that have their own bounds: Flatten draws at the offset,
/// Merge Down unions both layers, Crop clips to the new canvas, Extend grows to the union with
/// the canvas, whole-image rotate/flip remap the offset.
/// </summary>
public class LayerStackOffsetTests : IDisposable
{
    private readonly LayerStack _stack = new(20, 10);

    public void Dispose() => _stack.Dispose();

    private static SKBitmap Solid(int w, int h, SKColor c)
    {
        var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        b.Erase(c);
        return b;
    }

    private Layer Add(int w, int h, SKColor c, int x, int y, string name = "L")
    {
        using var src = Solid(w, h, c);
        return _stack.AddLayerFromBitmap(src, name, new SKPointI(x, y));
    }

    [Fact]
    public void Flatten_DrawsAtOffset_AndClips()
    {
        Add(4, 4, SKColors.Red, 18, 8);
        using var flat = _stack.Flatten()!;
        flat.Width.Should().Be(20);
        flat.GetPixel(18, 8).Should().Be(SKColors.Red);
        flat.GetPixel(17, 8).Alpha.Should().Be(0);
    }

    [Fact]
    public void MergeDown_ProducesUnionBounds_AndKeepsOffCanvasPixels()
    {
        var below = Add(4, 4, SKColors.Red, 0, 0, "below");
        var top = Add(4, 4, SKColors.Blue, 22, 2, "top"); // wholly off canvas

        _stack.MergeDown(top).Should().BeTrue();

        _stack.Count.Should().Be(1);
        below.Bounds.Should().Be(new SKRectI(0, 0, 26, 6));
        below.Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
        below.Bitmap!.GetPixel(23, 3).Should().Be(SKColors.Blue);
        below.Bitmap!.GetPixel(10, 5).Alpha.Should().Be(0);
    }

    [Fact]
    public void MergeDown_RefusesWhenTheUnionWouldBeTooLarge_AndLeavesBothLayersUntouched()
    {
        var below = Add(4, 4, SKColors.Red, 0, 0, "below");
        var top = Add(4, 4, SKColors.Blue, 30000, 30000, "top"); // union would blow past the size guard

        _stack.MergeDown(top).Should().BeFalse();

        _stack.Count.Should().Be(2);
        below.Bounds.Should().Be(new SKRectI(0, 0, 4, 4));
        below.Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
    }

    [Fact]
    public void CropAll_ClipsEachLayer_AndReoffsets()
    {
        var layer = Add(10, 10, SKColors.Red, -5, -5); // covers canvas 0..5
        _stack.CropAll(new SKRectI(2, 2, 8, 8));

        _stack.Width.Should().Be(6);
        _stack.Height.Should().Be(6);
        layer.Bounds.Should().Be(new SKRectI(0, 0, 3, 3)); // intersection (2,2)-(5,5) relative to (2,2)
        layer.Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
    }

    [Fact]
    public void CropAll_LayerWhollyOutside_BecomesOnePixelTransparent()
    {
        var layer = Add(2, 2, SKColors.Red, 30, 30);
        _stack.CropAll(new SKRectI(0, 0, 10, 10));
        layer.Bounds.Should().Be(new SKRectI(0, 0, 1, 1));
        layer.Bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void ResizeCanvas_GrowsToUnionWithCanvas_AndShifts()
    {
        var full = Add(20, 10, SKColors.Red, 0, 0, "bg");
        var moved = Add(4, 4, SKColors.Blue, 25, 3, "moved"); // off canvas to the right

        _stack.ResizeCanvas(30, 20, 5, 5); // add 5 px left and top

        _stack.Width.Should().Be(30);
        full.Bounds.Should().Be(new SKRectI(0, 0, 30, 20));
        full.Bitmap!.GetPixel(5, 5).Should().Be(SKColors.Red);
        full.Bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
        // moved layer: shifted to (30,8)-(34,12), union with canvas (0,0)-(30,20) => (0,0)-(34,20)
        moved.Bounds.Should().Be(new SKRectI(0, 0, 34, 20));
        moved.Bitmap!.GetPixel(30, 8).Should().Be(SKColors.Blue);
    }

    [Theory]
    [InlineData("right", 10, 20, 4, 3)]   // (H-(y+h), x) = (10-(3+3), 3) = (4,3)
    [InlineData("left", 10, 20, 3, 15)]   // (y, W-(x+w)) = (3, 20-(3+2)) = (3,15)
    [InlineData("180", 20, 10, 15, 4)]    // (W-(x+w), H-(y+h)) = (15, 4)
    [InlineData("flipH", 20, 10, 15, 3)]  // (W-(x+w), y)
    [InlineData("flipV", 20, 10, 3, 4)]   // (x, H-(y+h))
    public void TransformAll_RemapsOffsets(string op, int expectedW, int expectedH, int ox, int oy)
    {
        using var core = new ImageEditorCoreProbe(_stack);
        var layer = Add(2, 3, SKColors.Red, 3, 3);

        core.Apply(op);

        _stack.Width.Should().Be(expectedW);
        _stack.Height.Should().Be(expectedH);
        layer.OffsetX.Should().Be(ox);
        layer.OffsetY.Should().Be(oy);
    }

    /// <summary>Drives the stack through the same remap the core uses (LayerOffsetRemap).</summary>
    private sealed class ImageEditorCoreProbe(LayerStack stack) : IDisposable
    {
        public void Apply(string op)
        {
            var w = stack.Width; var h = stack.Height;
            switch (op)
            {
                case "right": stack.TransformAll(l => (Rot(l.Bitmap!, 90), LayerOffsetRemap.RotateRight(l.Bounds, w, h)), h, w); break;
                case "left": stack.TransformAll(l => (Rot(l.Bitmap!, -90), LayerOffsetRemap.RotateLeft(l.Bounds, w, h)), h, w); break;
                case "180": stack.TransformAll(l => (Rot(l.Bitmap!, 180), LayerOffsetRemap.Rotate180(l.Bounds, w, h)), w, h); break;
                case "flipH": stack.TransformAll(l => (l.Bitmap!.Copy(), LayerOffsetRemap.FlipHorizontal(l.Bounds, w, h)), w, h); break;
                case "flipV": stack.TransformAll(l => (l.Bitmap!.Copy(), LayerOffsetRemap.FlipVertical(l.Bounds, w, h)), w, h); break;
            }
        }

        private static SKBitmap Rot(SKBitmap src, int deg)
        {
            var swap = deg != 180;
            var r = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
            using var c = new SKCanvas(r);
            c.Translate(r.Width / 2f, r.Height / 2f);
            c.RotateDegrees(deg);
            c.Translate(-src.Width / 2f, -src.Height / 2f);
            c.DrawBitmap(src, 0, 0);
            return r;
        }

        public void Dispose() { }
    }
}
