using Avalonia;
using DiffusionNexus.UI.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// "What is under the bounding box" is composited arithmetically from the accepted rasters, not
/// captured from the screen — so it is verifiable here, pixel by pixel, with no Avalonia platform.
/// </summary>
public class CanvasRegionCompositorTests
{
    private static SKBitmap SolidBitmap(int width, int height, SKColor colour)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(colour);
        return bitmap;
    }

    private static SKColor PixelAt(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

    [Fact]
    public void EmptyRegion_IsFullyTransparentAndReportsNoCoverage()
    {
        using var composite = CanvasRegionCompositor.Composite([], new Rect(0, 0, 512, 512), 512, 512);

        composite.Coverage.Should().Be(0);
        composite.IsEmpty.Should().BeTrue();
        composite.IsFullyCovered.Should().BeFalse();
        PixelAt(composite.Bitmap, 256, 256).Alpha.Should().Be(0);
    }

    [Fact]
    public void RasterUnderTheWholeBox_ReportsFullCoverageAndCopiesThePixels()
    {
        using var raster = SolidBitmap(256, 256, new SKColor(200, 40, 40));
        var sources = new[] { new CanvasCompositeSource(raster, new Rect(0, 0, 512, 512)) };

        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(0, 0, 512, 512), 512, 512);

        composite.Coverage.Should().Be(1.0);
        composite.IsFullyCovered.Should().BeTrue();
        var pixel = PixelAt(composite.Bitmap, 100, 100);
        pixel.Red.Should().BeCloseTo(200, 2);
        pixel.Green.Should().BeCloseTo(40, 2);
        pixel.Alpha.Should().Be(255);
    }

    [Fact]
    public void RasterCoveringHalfTheBox_ReportsHalfCoverage()
    {
        using var raster = SolidBitmap(64, 64, SKColors.White);
        // The raster occupies the left half of the region.
        var sources = new[] { new CanvasCompositeSource(raster, new Rect(0, 0, 256, 512)) };

        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(0, 0, 512, 512), 512, 512);

        composite.Coverage.Should().BeApproximately(0.5, 0.01);
        composite.IsEmpty.Should().BeFalse();
        composite.IsFullyCovered.Should().BeFalse();
        PixelAt(composite.Bitmap, 10, 256).Alpha.Should().Be(255);
        PixelAt(composite.Bitmap, 500, 256).Alpha.Should().Be(0);
    }

    [Fact]
    public void BoxDraggedHalfOffARaster_PutsTheKnownPixelsOnTheCorrectSide()
    {
        using var raster = SolidBitmap(128, 128, SKColors.Blue);
        // Raster at world (0,0)-(512,512); box shifted right by 256 so its LEFT half is covered.
        var sources = new[] { new CanvasCompositeSource(raster, new Rect(0, 0, 512, 512)) };

        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(256, 0, 512, 512), 512, 512);

        PixelAt(composite.Bitmap, 10, 10).Blue.Should().BeGreaterThan(200, "the box's left half sits on the raster");
        PixelAt(composite.Bitmap, 500, 10).Alpha.Should().Be(0, "the box's right half is over empty canvas");
        composite.Coverage.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void RastersAreDrawnInListOrderSoTheLastOneWins()
    {
        // Explicit channel values: SKColors.Green is CSS green (0,128,0), not full-intensity.
        using var lower = SolidBitmap(32, 32, new SKColor(255, 0, 0));
        using var upper = SolidBitmap(32, 32, new SKColor(0, 255, 0));
        var region = new Rect(0, 0, 256, 256);
        var sources = new[]
        {
            new CanvasCompositeSource(lower, region),
            new CanvasCompositeSource(upper, region),
        };

        using var composite = CanvasRegionCompositor.Composite(sources, region, 256, 256);

        PixelAt(composite.Bitmap, 128, 128).Green.Should().BeGreaterThan(200);
        PixelAt(composite.Bitmap, 128, 128).Red.Should().BeLessThan(60);
    }

    [Fact]
    public void NonIntersectingRastersAreIgnored()
    {
        using var far = SolidBitmap(32, 32, SKColors.White);
        var sources = new[] { new CanvasCompositeSource(far, new Rect(10_000, 10_000, 512, 512)) };

        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(0, 0, 512, 512), 512, 512);

        composite.Coverage.Should().Be(0);
    }

    [Fact]
    public void RegionIsScaledToTheLatentSizeNotTheRastersOwnResolution()
    {
        using var raster = SolidBitmap(4096, 4096, SKColors.White);
        var sources = new[] { new CanvasCompositeSource(raster, new Rect(0, 0, 1024, 1024)) };

        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(0, 0, 1024, 1024), 512, 512);

        composite.Bitmap.Width.Should().Be(512);
        composite.Bitmap.Height.Should().Be(512);
        composite.Coverage.Should().Be(1.0);
    }

    [Fact]
    public void EncodeAsPng_ProducesRealPixelsRatherThanABlankImage()
    {
        using var raster = SolidBitmap(64, 64, new SKColor(10, 180, 90));
        var sources = new[] { new CanvasCompositeSource(raster, new Rect(0, 0, 256, 256)) };
        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(0, 0, 256, 256), 256, 256);

        var png = CanvasRegionCompositor.EncodeAsPng(composite.Bitmap);

        png.Should().NotBeEmpty();
        using var decoded = SKBitmap.Decode(png);
        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(256);
        var pixel = PixelAt(decoded, 128, 128);
        pixel.Alpha.Should().Be(255, "premultiplied data encoded straight to PNG comes back blank");
        pixel.Green.Should().BeCloseTo(180, 3);
    }

    [Fact]
    public void EncodeAsPng_FlattensTheUncoveredAreaOntoTheGivenFill()
    {
        using var raster = SolidBitmap(32, 32, SKColors.White);
        var sources = new[] { new CanvasCompositeSource(raster, new Rect(0, 0, 128, 256)) };
        using var composite = CanvasRegionCompositor.Composite(sources, new Rect(0, 0, 256, 256), 256, 256);

        var png = CanvasRegionCompositor.EncodeAsPng(composite.Bitmap, CanvasRegionCompositor.NeutralFill);

        using var decoded = SKBitmap.Decode(png);
        var uncovered = PixelAt(decoded!, 200, 128);
        uncovered.Alpha.Should().Be(255, "the whole image is opaque once flattened");
        uncovered.Red.Should().BeCloseTo(0x80, 2);
        uncovered.Green.Should().BeCloseTo(0x80, 2);
        uncovered.Blue.Should().BeCloseTo(0x80, 2);
    }

    [Fact]
    public void CompositeRejectsADegenerateRegion()
    {
        var act = () => CanvasRegionCompositor.Composite([], new Rect(0, 0, 0, 512), 512, 512);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompositeRejectsANonPositiveOutputSize()
    {
        var act = () => CanvasRegionCompositor.Composite([], new Rect(0, 0, 512, 512), 0, 512);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LoadIntersecting_SkipsRastersWhoseFileIsMissingAndSaysSo()
    {
        var skipped = new List<string>();
        var rasters = new ICanvasRaster[]
        {
            new StubRaster(0, 0, 512, 512, ImagePath: null),
            new StubRaster(0, 0, 512, 512, ImagePath: Path.Combine(Path.GetTempPath(), $"dn-missing-{Guid.NewGuid():N}.png")),
        };

        var loaded = CanvasRegionCompositor.LoadIntersecting(rasters, new Rect(0, 0, 512, 512), skipped.Add);

        loaded.Should().BeEmpty();
        skipped.Should().HaveCount(2);
    }

    [Fact]
    public void LoadIntersecting_DecodesOnlyTheRastersUnderTheBox()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dn-canvas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var underBox = Path.Combine(directory, "under.png");
            var farAway = Path.Combine(directory, "far.png");
            using (var bitmap = SolidBitmap(16, 16, SKColors.White))
                File.WriteAllBytes(underBox, CanvasRegionCompositor.EncodeAsPng(bitmap));
            File.Copy(underBox, farAway);

            var rasters = new ICanvasRaster[]
            {
                new StubRaster(0, 0, 512, 512, underBox),
                new StubRaster(9000, 9000, 512, 512, farAway),
            };

            var loaded = CanvasRegionCompositor.LoadIntersecting(rasters, new Rect(0, 0, 512, 512));

            try
            {
                loaded.Should().HaveCount(1);
                loaded[0].WorldRect.Should().Be(new Rect(0, 0, 512, 512));
            }
            finally
            {
                foreach (var source in loaded)
                    source.Bitmap.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Minimal <see cref="ICanvasRaster"/> that needs no Avalonia bitmap.</summary>
    private sealed record StubRaster(
        double CanvasX, double CanvasY, int Width, int Height, string? ImagePath) : ICanvasRaster
    {
        public Avalonia.Media.Imaging.Bitmap? FrameImage => null;
    }
}
