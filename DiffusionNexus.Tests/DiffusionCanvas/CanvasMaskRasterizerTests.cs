using Avalonia;
using DiffusionNexus.UI.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The mask layer's strokes become the request's <c>MaskImage</c> here (#595): white = repaint, black =
/// keep, for exactly the box's region.
/// </summary>
public class CanvasMaskRasterizerTests
{
    private static CanvasMaskStroke Dot(double x, double y, double size, bool erase = false) =>
        new([new Point(x, y)], size, erase);

    private static CanvasMaskStroke Line(Point from, Point to, double size, bool erase = false) =>
        new([from, to], size, erase);

    private static byte Value(CanvasMaskRaster mask, int x, int y) => mask.Bitmap.GetPixel(x, y).Red;

    [Fact]
    public void NoStrokesRepaintsNothing()
    {
        using var mask = CanvasMaskRasterizer.Rasterize([], new Rect(0, 0, 64, 64), 64, 64, feather: 0, invert: false);

        mask.IsEmpty.Should().BeTrue();
        mask.RepaintFraction.Should().Be(0);
        Value(mask, 32, 32).Should().Be(0);
    }

    [Fact]
    public void ABrushDotIsWhiteWhereItWasPaintedAndBlackElsewhere()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(32, 32, 20)], new Rect(0, 0, 64, 64), 64, 64, feather: 0, invert: false);

        Value(mask, 32, 32).Should().Be(255);
        Value(mask, 2, 2).Should().Be(0);
        mask.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void TheMaskIsOpaqueGrey()
    {
        // ComfyUI's ImageToMask reads one channel and sd.cpp loads RGB: every channel must agree, and a
        // transparent pixel would read as black in one and undefined in the other.
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(32, 32, 20)], new Rect(0, 0, 64, 64), 64, 64, feather: 6, invert: false);

        foreach (var (x, y) in new[] { (32, 32), (43, 32), (60, 60) })
        {
            var pixel = mask.Bitmap.GetPixel(x, y);
            pixel.Alpha.Should().Be(255);
            pixel.Green.Should().Be(pixel.Red);
            pixel.Blue.Should().Be(pixel.Red);
        }
    }

    [Fact]
    public void TheRegionIsInWorldCoordinates()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(1010, -490, 6)], new Rect(1000, -500, 20, 20), 20, 20, feather: 0, invert: false);

        Value(mask, 10, 10).Should().Be(255);
        Value(mask, 1, 1).Should().Be(0);
    }

    [Fact]
    public void AStrokeOutsideTheRegionLeavesItUntouched()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(500, 500, 40)], new Rect(0, 0, 64, 64), 64, 64, feather: 0, invert: false);

        mask.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TheEraserRemovesWhatWasPaintedBeforeIt()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(32, 32, 30), Dot(32, 32, 10, erase: true)], new Rect(0, 0, 64, 64), 64, 64, feather: 0, invert: false);

        Value(mask, 32, 32).Should().Be(0, "the eraser cleared the middle");
        Value(mask, 32, 22).Should().Be(255, "the ring outside the eraser stays");
    }

    [Fact]
    public void ErasingAStrokeWithTheSameBrushLeavesAnEmptyMask()
    {
        // The antialiased eraser leaves faint alpha along the old edge, and the feather spreads it. That
        // residue is not painting: the mask must read as empty so Generate refuses instead of running.
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Line(new Point(10, 32), new Point(54, 30), 17), Line(new Point(10, 32), new Point(54, 30), 17, erase: true)],
            new Rect(0, 0, 64, 64), 64, 64, feather: 6, invert: false);

        mask.IsEmpty.Should().BeTrue();
        mask.MaxValue.Should().BeLessThan(CanvasMaskRaster.MeaningfulValue);
    }

    [Fact]
    public void FeatherReachIsZeroWithoutFeatherAndGrowsWithIt()
    {
        CanvasMaskRasterizer.FeatherReachOf(0).Should().Be(0);
        CanvasMaskRasterizer.FeatherReachOf(16).Should().Be(57);
    }

    [Fact]
    public void PaintingAfterTheEraserPaintsAgain()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(32, 32, 30), Dot(32, 32, 10, erase: true), Dot(32, 32, 4)],
            new Rect(0, 0, 64, 64), 64, 64, feather: 0, invert: false);

        Value(mask, 32, 32).Should().Be(255);
    }

    [Fact]
    public void InvertRepaintsEverythingExceptThePainting()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(32, 32, 20)], new Rect(0, 0, 64, 64), 64, 64, feather: 0, invert: true);

        Value(mask, 32, 32).Should().Be(0);
        Value(mask, 2, 2).Should().Be(255);
    }

    [Fact]
    public void InvertingNothingRepaintsTheWholeRegion()
    {
        using var mask = CanvasMaskRasterizer.Rasterize([], new Rect(0, 0, 16, 16), 16, 16, feather: 0, invert: true);

        mask.RepaintFraction.Should().Be(1);
    }

    [Fact]
    public void FeatherSoftensTheEdge()
    {
        var region = new Rect(0, 0, 64, 64);

        using var hard = CanvasMaskRasterizer.Rasterize([Dot(32, 32, 20)], region, 64, 64, feather: 0, invert: false);
        using var soft = CanvasMaskRasterizer.Rasterize([Dot(32, 32, 20)], region, 64, 64, feather: 6, invert: false);

        // Five pixels past the painted edge (radius 10).
        Value(hard, 47, 32).Should().Be(0);
        Value(soft, 47, 32).Should().BeInRange(1, 254);
    }

    [Fact]
    public void AnInvertedFeatheredMaskIsFullStrengthAtTheRegionsBorder()
    {
        // Feathering first and inverting after keeps the blur from reading the region's border as an edge.
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(32, 32, 10)], new Rect(0, 0, 64, 64), 64, 64, feather: 6, invert: true);

        Value(mask, 0, 0).Should().Be(255);
        Value(mask, 63, 32).Should().Be(255);
    }

    [Fact]
    public void AStrokeJustOutsideTheRegionStillFeathersIntoIt()
    {
        // The edge of this dot sits 2 px left of the region. Without the margin the rasteriser would not
        // see it at all, and moving the box a few pixels would switch the soft edge on and off.
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(-12, 32, 20)], new Rect(0, 0, 64, 64), 64, 64, feather: 6, invert: false);

        Value(mask, 0, 32).Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheOutputSizeScalesTheRegion()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(75, 75, 10)], new Rect(0, 0, 100, 100), 50, 50, feather: 0, invert: false);

        mask.Bitmap.Width.Should().Be(50);
        Value(mask, 37, 37).Should().Be(255);
        Value(mask, 10, 10).Should().Be(0);
    }

    [Fact]
    public void RepaintFractionMeasuresThePaintedShare()
    {
        // A thick vertical line over the left half of the box.
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Line(new Point(25, -100), new Point(25, 200), 50)], new Rect(0, 0, 100, 100), 100, 100, feather: 0, invert: false);

        mask.RepaintFraction.Should().BeApproximately(0.5, 0.02);
    }

    [Fact]
    public void EncodePngRoundTripsTheMask()
    {
        using var mask = CanvasMaskRasterizer.Rasterize(
            [Dot(8, 8, 6)], new Rect(0, 0, 16, 16), 16, 16, feather: 0, invert: false);

        using var decoded = SKBitmap.Decode(mask.EncodePng());

        decoded.Width.Should().Be(16);
        decoded.GetPixel(8, 8).Red.Should().Be(255);
        decoded.GetPixel(0, 0).Red.Should().Be(0);
    }

    [Fact]
    public void AStrokeNeedsAPointAndAPositiveSize()
    {
        var noPoints = () => new CanvasMaskStroke([], 10, isErase: false);
        var noSize = () => new CanvasMaskStroke([new Point(0, 0)], 0, isErase: false);

        noPoints.Should().Throw<ArgumentException>();
        noSize.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AStrokesBoundsIncludeTheBrush()
    {
        var stroke = Line(new Point(10, 20), new Point(30, 20), 8);

        stroke.Bounds.Should().Be(new Rect(6, 16, 28, 8));
    }
}
