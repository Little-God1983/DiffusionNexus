using DiffusionNexus.Service.Services.Imaging;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class SkiaPixelsTests
{
    /// <summary>
    /// A 1-px black/white checkerboard averages to mid-grey. A single 4×4-tap cubic pass over a
    /// 30× reduction samples a handful of source pixels per output pixel and lands anywhere from
    /// near-black to near-white (measured: 21–234); an area-aware downscale stays near 128.
    /// </summary>
    [Fact]
    public void ResizeTo_LargeReductionOfFineDetail_AveragesInsteadOfAliasing()
    {
        using var checker = CreateCheckerboard(1000, 1000);

        using var small = SkiaPixels.ResizeTo(checker, 32, 32);

        AssertAllChannelsNear(small, 128, tolerance: 10);
    }

    [Fact]
    public void FitWithin_LargeReductionOfFineDetail_AveragesInsteadOfAliasing()
    {
        using var checker = CreateCheckerboard(1200, 900);

        var fitted = SkiaPixels.FitWithin(checker, 40);

        try
        {
            fitted.Width.Should().Be(40);
            fitted.Height.Should().Be(30);
            AssertAllChannelsNear(fitted, 128, tolerance: 10);
        }
        finally
        {
            if (!ReferenceEquals(fitted, checker)) fitted.Dispose();
        }
    }

    [Fact]
    public void ResizeTo_Upscale_StillProducesTheRequestedSize()
    {
        using var checker = CreateCheckerboard(10, 8);

        using var big = SkiaPixels.ResizeTo(checker, 25, 17);

        big.Width.Should().Be(25);
        big.Height.Should().Be(17);
    }

    [Fact]
    public void ReadDimensions_ReadsTheHeaderWithoutDecodingPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-dims-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = CreateCheckerboard(37, 21)) SkiaPixels.SavePng(bmp, path);

            SkiaPixels.ReadDimensions(path).Should().Be((37, 21));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SKBitmap CreateCheckerboard(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                byte v = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                rgba[i + 3] = 255;
            }
        }
        return SkiaPixels.FromRgbaBytes(rgba, width, height);
    }

    private static void AssertAllChannelsNear(SKBitmap bitmap, int expected, int tolerance)
    {
        var span = bitmap.GetPixelSpan();
        int min = 255, max = 0;
        for (int i = 0; i < span.Length; i += 4)
        {
            min = Math.Min(min, span[i]);
            max = Math.Max(max, span[i]);
        }
        min.Should().BeGreaterThanOrEqualTo(expected - tolerance, "a downscale should average fine detail, not alias it");
        max.Should().BeLessThanOrEqualTo(expected + tolerance, "a downscale should average fine detail, not alias it");
    }
}
