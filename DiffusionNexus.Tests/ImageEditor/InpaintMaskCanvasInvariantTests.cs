using System.Linq;
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// The inpaint mask layer is drawn canvas-aligned and full-canvas-sized; every stack-wide
/// geometry operation (crop, canvas resize, whole-image rotate/flip) must keep it that way so the
/// mask never drifts off (0, 0) or ends up a different size than the canvas it masks.
/// </summary>
public class InpaintMaskCanvasInvariantTests : IDisposable
{
    private readonly LayerStack _stack = new(60, 40);

    public InpaintMaskCanvasInvariantTests()
    {
        _stack.AddLayer("Background");
        var mask = _stack.AddLayer("Mask");
        mask.IsInpaintMask = true;
    }

    public void Dispose() => _stack.Dispose();

    private Layer Mask => _stack.Layers.Single(l => l.IsInpaintMask);

    [Fact]
    public void CropAll_KeepsTheMaskCanvasAligned_AndFullSized()
    {
        _stack.CropAll(new SKRectI(10, 5, 50, 35));

        Mask.Bounds.Should().Be(new SKRectI(0, 0, 40, 30));
    }

    [Fact]
    public void ResizeCanvas_GrowsTheMaskWithTheCanvas()
    {
        _stack.ResizeCanvas(80, 50, 10, 5);

        Mask.Bounds.Should().Be(new SKRectI(0, 0, 80, 50));
    }

    [Fact]
    public void RotateRight_SwapsTheMaskToTheRotatedCanvasSize()
    {
        using var core = new ImageEditorCoreProbe(_stack);

        core.Apply("right");

        Mask.Bounds.Should().Be(new SKRectI(0, 0, 40, 60));
    }

    /// <summary>Drives the stack through the same remap ImageEditorCore.Transforms uses (LayerOffsetRemap).</summary>
    private sealed class ImageEditorCoreProbe(LayerStack stack) : IDisposable
    {
        public void Apply(string op)
        {
            var w = stack.Width; var h = stack.Height;
            switch (op)
            {
                case "right": stack.TransformAll(l => (Rot(l.Bitmap!, 90), LayerOffsetRemap.RotateRight(l.Bounds, w, h)), h, w); break;
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
