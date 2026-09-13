using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// "Add as Layer" (drop choice and thumbnail context menu, #567) decodes each file and stacks it
/// on the current canvas as its own layer. Undecodable files are reported, not fatal.
/// </summary>
public class ImageEditorCoreAddLayersFromFilesTests : IDisposable
{
    private readonly ImageEditorCore _sut;
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory();

    public ImageEditorCoreAddLayersFromFilesTests()
    {
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());
    }

    public void Dispose()
    {
        _sut.Dispose();
        _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void AddsOneNamedLayerPerFile_InOrder_AndActivatesTheLast()
    {
        _sut.LoadImage(Png("base", 40, 30));
        var overlay = WritePng("overlay.png", 12, 12);
        var badge = WritePng("badge.png", 6, 6);

        var result = _sut.AddLayersFromFiles([overlay, badge]);

        result.Added.Should().Be(2);
        result.Failed.Should().BeEmpty();
        _sut.Layers!.Count.Should().Be(3);
        _sut.Layers[1].Name.Should().Be("overlay");
        _sut.Layers[2].Name.Should().Be("badge");
        _sut.Layers[2].Bitmap.Width.Should().Be(6);
        _sut.Layers.ActiveLayer.Should().BeSameAs(_sut.Layers[2]);
    }

    [Fact]
    public void CentresEachLayerOnTheCanvas()
    {
        _sut.LoadImage(Png("base", 40, 30));
        var badge = WritePng("badge.png", 6, 6);
        var poster = WritePng("poster.png", 60, 10);

        _sut.AddLayersFromFiles([badge, poster]);

        (_sut.Layers![1].OffsetX, _sut.Layers[1].OffsetY).Should().Be((17, 12));
        (_sut.Layers[2].OffsetX, _sut.Layers[2].OffsetY).Should().Be((-10, 10), "an oversized layer is centred too, spilling both sides");
    }

    [Fact]
    public void MarksTheCanvasDirty_AndRaisesImageChanged()
    {
        _sut.LoadImage(Png("base", 40, 30));
        var overlay = WritePng("overlay.png", 12, 12);
        var raised = 0;
        _sut.ImageChanged += (_, _) => raised++;

        _sut.AddLayersFromFiles([overlay]);

        _sut.IsDirty.Should().BeTrue();
        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReportsUndecodableFiles_AndStillAddsTheRest()
    {
        _sut.LoadImage(Png("base", 40, 30));
        var overlay = WritePng("overlay.png", 12, 12);
        var missing = Path.Combine(_tempDir.FullName, "missing.png");
        var garbage = Path.Combine(_tempDir.FullName, "garbage.png");
        File.WriteAllText(garbage, "not an image");

        var result = _sut.AddLayersFromFiles([missing, overlay, garbage]);

        result.Added.Should().Be(1);
        result.Failed.Should().Equal(missing, garbage);
        _sut.Layers!.Count.Should().Be(2);
    }

    [Fact]
    public void WithoutACanvas_AddsNothing()
    {
        var overlay = WritePng("overlay.png", 12, 12);

        var result = _sut.AddLayersFromFiles([overlay]);

        result.Added.Should().Be(0);
        result.Failed.Should().BeEmpty("the file was fine; there was simply no canvas to add it to");
        _sut.HasImage.Should().BeFalse();
    }

    private string WritePng(string name, int width, int height)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllBytes(path, Png(name, width, height));
        return path;
    }

    private static byte[] Png(string _, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.SlateBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
