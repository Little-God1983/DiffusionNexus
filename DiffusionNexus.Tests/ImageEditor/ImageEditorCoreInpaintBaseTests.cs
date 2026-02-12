using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Tests that the inpaint base bitmap is correctly invalidated
/// when the image is reset or cleared.
/// </summary>
public class ImageEditorCoreInpaintBaseTests : IDisposable
{
    private readonly ImageEditorCore _sut;

    public ImageEditorCoreInpaintBaseTests()
    {
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        // Load a test image from bytes
        using var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());
    }

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public void WhenResetToOriginal_InpaintBaseIsCleared()
    {
        // Arrange — capture an inpaint base
        _sut.SetInpaintBaseBitmap();
        _sut.HasInpaintBase.Should().BeTrue();

        // Act
        _sut.ResetToOriginal();

        // Assert
        _sut.HasInpaintBase.Should().BeFalse();
    }

    [Fact]
    public void WhenClear_InpaintBaseIsCleared()
    {
        // Arrange — capture an inpaint base
        _sut.SetInpaintBaseBitmap();
        _sut.HasInpaintBase.Should().BeTrue();

        // Act
        _sut.Clear();

        // Assert
        _sut.HasInpaintBase.Should().BeFalse();
    }
}
