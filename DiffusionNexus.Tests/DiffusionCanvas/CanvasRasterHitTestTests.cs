using Avalonia;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>The z-order rule behind the surface's right-click: the result the user sees is the one acted on.</summary>
public class CanvasRasterHitTestTests
{
    private sealed record StubRaster(double CanvasX, double CanvasY, int Width, int Height) : ICanvasRaster
    {
        public Bitmap? FrameImage => null;
        public string? ImagePath => null;
    }

    [Fact]
    public void ReturnsNullOverEmptyCanvas()
    {
        var rasters = new ICanvasRaster[] { new StubRaster(0, 0, 1024, 1024) };

        CanvasRasterHitTest.TopmostAt(rasters, new Point(5000, 5000)).Should().BeNull();
        CanvasRasterHitTest.TopmostAt([], new Point(0, 0)).Should().BeNull();
    }

    [Fact]
    public void ReturnsTheRasterContainingThePoint()
    {
        var left = new StubRaster(0, 0, 1024, 1024);
        var right = new StubRaster(2048, 0, 1024, 1024);

        CanvasRasterHitTest.TopmostAt([left, right], new Point(2500, 500)).Should().BeSameAs(right);
        CanvasRasterHitTest.TopmostAt([left, right], new Point(500, 500)).Should().BeSameAs(left);
    }

    [Fact]
    public void WhereRastersOverlapTheLastOneWins()
    {
        // Rasters are drawn in list order, so the last one is what the user sees at the overlap.
        var below = new StubRaster(0, 0, 1024, 1024);
        var above = new StubRaster(512, 512, 1024, 1024);

        CanvasRasterHitTest.TopmostAt([below, above], new Point(700, 700)).Should().BeSameAs(above);
        CanvasRasterHitTest.TopmostAt([above, below], new Point(700, 700)).Should().BeSameAs(below);
    }

    [Fact]
    public void IgnoresDegenerateRasters()
    {
        var empty = new StubRaster(0, 0, 0, 0);

        CanvasRasterHitTest.TopmostAt([empty], new Point(0, 0)).Should().BeNull();
    }
}
