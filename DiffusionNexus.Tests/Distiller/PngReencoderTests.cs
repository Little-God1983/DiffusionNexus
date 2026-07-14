using System;
using System.IO;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.Distiller;

public class PngReencoderTests
{
    internal static string MakeRealPng(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"real_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Theory]
    [InlineData(4000, 2000, 2048, 2048, 1024)] // landscape capped on width
    [InlineData(2000, 4000, 2048, 1024, 2048)] // portrait capped on height
    [InlineData(800, 600, 2048, 800, 600)]     // already fits — unchanged
    [InlineData(800, 600, null, 800, 600)]     // no cap — unchanged
    public void TargetSize_caps_longest_side_and_preserves_aspect(int w, int h, int? max, int expW, int expH)
    {
        PngReencoder.TargetSize(w, h, max).Should().Be((expW, expH));
    }

    [Fact]
    public void Reencode_downscales_to_the_longest_side_cap()
    {
        var src = MakeRealPng(256, 128);
        try
        {
            var result = PngReencoder.Reencode(src, maxDimension: 64, PngReencoder.DefaultZlibLevel);

            result.Should().NotBeNull();
            result!.Width.Should().Be(64);
            result.Height.Should().Be(32);

            using var decoded = SKBitmap.Decode(result.Bytes);
            decoded.Should().NotBeNull();
            decoded!.Width.Should().Be(64);
            decoded.Height.Should().Be(32);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Reencode_without_resize_keeps_dimensions()
    {
        var src = MakeRealPng(100, 50);
        try
        {
            var result = PngReencoder.Reencode(src, maxDimension: null, PngReencoder.MaxZlibLevel);

            result.Should().NotBeNull();
            (result!.Width, result.Height).Should().Be((100, 50));

            using var decoded = SKBitmap.Decode(result.Bytes);
            decoded!.Width.Should().Be(100);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Reencode_returns_null_for_undecodable_input()
    {
        var src = Path.Combine(Path.GetTempPath(), $"junk_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(src, [1, 2, 3, 4]);
        try
        {
            PngReencoder.Reencode(src, 64, PngReencoder.DefaultZlibLevel).Should().BeNull();
        }
        finally { File.Delete(src); }
    }
}
