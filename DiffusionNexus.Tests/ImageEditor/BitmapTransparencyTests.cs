using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// <see cref="BitmapTransparency"/> compares a vector of pixels at a time and finishes each row
/// one pixel at a time. 37 pixels is not a multiple of any vector width, so every row has a
/// tail: a single non-opaque pixel must be found wherever it sits.
/// </summary>
public class BitmapTransparencyTests
{
    private const int Width = 37;
    private const int Height = 5;

    private static SKBitmap Opaque(SKColorType colorType = SKColorType.Rgba8888)
    {
        var bitmap = new SKBitmap(Width, Height, colorType, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Teal);
        return bitmap;
    }

    [Fact]
    public void WhenEveryPixelIsOpaqueThenNothingIsFound()
    {
        using var bitmap = Opaque();

        BitmapTransparency.HasTransparentPixels(bitmap).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(18, 2)]
    [InlineData(Width - 1, 0)]
    [InlineData(Width - 1, Height - 1)]
    public void WhenOnePixelIsNotFullyOpaqueThenItIsFound(int x, int y)
    {
        using var bitmap = Opaque();
        bitmap.SetPixel(x, y, new SKColor(0, 128, 128, 254));

        BitmapTransparency.HasTransparentPixels(bitmap).Should().BeTrue();
    }

    [Fact]
    public void WhenTheLayoutIsBgraThenAlphaIsStillFound()
    {
        using var bitmap = Opaque(SKColorType.Bgra8888);
        bitmap.SetPixel(Width - 1, Height - 1, SKColors.Transparent);

        BitmapTransparency.HasTransparentPixels(bitmap).Should().BeTrue();
    }

    [Fact]
    public void WhenTheColourTypeIsNot8888ThenItIsConvertedAndChecked()
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.RgbaF16, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Teal);
        bitmap.SetPixel(3, 1, SKColors.Transparent);

        BitmapTransparency.HasTransparentPixels(bitmap).Should().BeTrue();
    }
}
