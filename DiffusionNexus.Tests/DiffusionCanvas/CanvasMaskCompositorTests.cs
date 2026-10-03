using DiffusionNexus.UI.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The kept pixels of a masked run are the original's, not a VAE round trip of them (#595 review).
/// </summary>
public class CanvasMaskCompositorTests
{
    private static SKBitmap Solid(int size, SKColor colour)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(colour);
        return bitmap;
    }

    private static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A mask white on its left half, black on its right, with one grey column at x = 8.</summary>
    private static SKBitmap LeftHalfMask(int size)
    {
        var mask = Solid(size, SKColors.Black);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size / 2; x++)
                mask.SetPixel(x, y, SKColors.White);
            mask.SetPixel(size / 2, y, new SKColor(128, 128, 128));
        }

        return mask;
    }

    [Fact]
    public void OutsideTheMaskTheOriginalComesBackAndInsideTheGeneratedPixels()
    {
        using var original = Solid(16, SKColors.Red);
        using var generated = Solid(16, SKColors.Blue);
        using var mask = LeftHalfMask(16);

        var png = CanvasMaskCompositor.KeepUnmasked(Png(generated), original, mask);

        using var result = SKBitmap.Decode(png);
        result.GetPixel(2, 2).Should().Be(SKColors.Blue, "painted: the generated pixel");
        result.GetPixel(14, 2).Should().Be(SKColors.Red, "unpainted: the original pixel, exactly");
    }

    [Fact]
    public void AFeatheredEdgeBlendsTheTwo()
    {
        using var original = Solid(16, SKColors.Red);
        using var generated = Solid(16, SKColors.Blue);
        using var mask = LeftHalfMask(16);

        using var result = SKBitmap.Decode(CanvasMaskCompositor.KeepUnmasked(Png(generated), original, mask));

        var edge = result.GetPixel(8, 2);
        edge.Red.Should().BeInRange(120, 135);
        edge.Blue.Should().BeInRange(120, 135);
        edge.Alpha.Should().Be(255);
    }

    [Fact]
    public void ASizeMismatchReturnsNullSoTheResultIsKeptAsItIs()
    {
        using var original = Solid(16, SKColors.Red);
        using var generated = Solid(32, SKColors.Blue);
        using var mask = LeftHalfMask(16);

        CanvasMaskCompositor.KeepUnmasked(Png(generated), original, mask).Should().BeNull();
    }

    [Fact]
    public void AnUndecodableResultReturnsNull()
    {
        using var original = Solid(16, SKColors.Red);
        using var mask = LeftHalfMask(16);

        CanvasMaskCompositor.KeepUnmasked([1, 2, 3, 4], original, mask).Should().BeNull();
    }
}
