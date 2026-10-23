using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// The inpaint image is built off the UI thread (#606 smoke: a 3840×2176 canvas froze the window for
/// seconds). The snapshot of base and mask is still taken at call time, so strokes painted while the
/// encode runs do not leak into the image that is sent.
/// </summary>
public class ImageEditorCoreInpaintPrepareAsyncTests : IDisposable
{
    private readonly ImageEditorCore _sut;

    public ImageEditorCoreInpaintPrepareAsyncTests()
    {
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());
    }

    public void Dispose() => _sut.Dispose();

    private static int TransparentPixels(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png);
        return decoded.Pixels.Count(p => p.Alpha == 0);
    }

    [Fact]
    public async Task PrepareAsync_MatchesTheMaskPaintedAtCallTime_NotStrokesPaintedAfterwards()
    {
        _sut.ApplyInpaintStroke([new SKPoint(0.25f, 0.25f)], 0.1f);

        var task = _sut.PrepareInpaintMaskedImageAsync(0f);
        _sut.ApplyInpaintStroke([new SKPoint(0.75f, 0.75f)], 0.1f);
        var result = await task;

        result.Success.Should().BeTrue();
        var later = await _sut.PrepareInpaintMaskedImageAsync(0f);
        TransparentPixels(result.MaskedImagePng!).Should().BeGreaterThan(0)
            .And.BeLessThan(TransparentPixels(later.MaskedImagePng!),
                "the second stroke was painted after the first call took its snapshot");
    }

    [Fact]
    public async Task PrepareAsync_CapturesTheBaseBeforeReturning()
    {
        _sut.ApplyInpaintStroke([new SKPoint(0.5f, 0.5f)], 0.1f);
        var versionBefore = _sut.InpaintBaseVersion;

        var task = _sut.PrepareInpaintMaskedImageAsync(4f);

        _sut.InpaintBaseVersion.Should().NotBe(versionBefore, "the view reads the version right after the call");
        (await task).BaseWasCaptured.Should().BeTrue();
    }

    [Fact]
    public async Task PrepareAsync_WithoutAMask_FailsWithTheHint()
    {
        var result = await _sut.PrepareInpaintMaskedImageAsync(0f);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("No inpaint mask painted");
    }

    [Fact]
    public async Task BaseAsPngAsync_ReturnsTheCapturedBase()
    {
        _sut.ApplyInpaintStroke([new SKPoint(0.5f, 0.5f)], 0.1f);
        await _sut.PrepareInpaintMaskedImageAsync(0f);

        var png = await _sut.GetInpaintBaseAsPngAsync();

        png.Should().NotBeNull();
        using var decoded = SKBitmap.Decode(png);
        decoded.Width.Should().Be(64);
        decoded.GetPixel(10, 10).Should().Be(SKColors.Red);
    }
}
