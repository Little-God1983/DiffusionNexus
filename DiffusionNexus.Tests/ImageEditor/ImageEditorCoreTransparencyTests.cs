using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// JPEG cannot store transparency (#584). <see cref="ImageEditorCore.HasTransparency"/> answers
/// "would a JPEG lose something?" for the image as it would be written — the flattened result,
/// not each layer on its own — and <see cref="ImageEditorCore.SaveImage"/>'s fill colour decides
/// what the transparent areas become instead of the encoder's black.
/// </summary>
public class ImageEditorCoreTransparencyTests : IDisposable
{
    private readonly ImageEditorCore _sut;
    private readonly DirectoryInfo _tempDir;

    public ImageEditorCoreTransparencyTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();

        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(100, 80, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray()).Should().BeTrue();
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { _tempDir.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private void ExtendRightBy(int pixels)
    {
        _sut.CanvasExtendTool.IsActive = true;
        _sut.CanvasExtendTool.ImagePixelWidth = _sut.Width;
        _sut.CanvasExtendTool.ImagePixelHeight = _sut.Height;
        _sut.CanvasExtendTool.SetExtension(top: 0, right: pixels, bottom: 0, left: 0);
        _sut.ApplyCanvasExtend().Should().BeTrue();
    }

    private void ArmMoveTool()
    {
        _sut.LayerTransformTool.ImagePixelWidth = _sut.Width;
        _sut.LayerTransformTool.ImagePixelHeight = _sut.Height;
        _sut.LayerTransformTool.SetImageBounds(new SKRect(0, 0, _sut.Width, _sut.Height));
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
    }

    private SKColor SaveJpegAndReadPixel(SKColor? fillColor, int x, int y)
    {
        var path = Path.Combine(_tempDir.FullName, "export.jpg");
        _sut.SaveImage(path, SKEncodedImageFormat.Jpeg, 95, fillColor).Should().BeTrue();

        using var decoded = SKBitmap.Decode(path);
        return decoded.GetPixel(x, y);
    }

    [Fact]
    public void WhenEveryPixelIsOpaqueThenThereIsNoTransparency()
    {
        _sut.HasTransparency().Should().BeFalse();
    }

    [Fact]
    public void WhenAnEmptyLayerSitsOnAnOpaqueBackgroundThenThereIsNoTransparency()
    {
        // A new layer (or a text layer) is almost entirely transparent, but the opaque layer
        // under it shows through everywhere, so the JPEG loses nothing.
        _sut.AddLayer("Empty").Should().NotBeNull();

        _sut.HasTransparency().Should().BeFalse();
    }

    [Fact]
    public void WhenTheCanvasWasExtendedThenTheNewStripIsTransparent()
    {
        ExtendRightBy(20);

        _sut.HasTransparency().Should().BeTrue();
    }

    [Fact]
    public void WhenTheOnlyLayerIsHalfOpaqueThenThereIsTransparency()
    {
        // Partial alpha is transparency too: the encoder would darken it towards black.
        _sut.Layers![0].Opacity = 0.5f;

        _sut.HasTransparency().Should().BeTrue();
    }

    [Fact]
    public void WhenLayerModeIsOffThenTheWorkingBitmapIsChecked()
    {
        _sut.DisableLayerMode();
        ExtendRightBy(20);

        _sut.HasTransparency().Should().BeTrue();
    }

    [Theory]
    [InlineData(255, 255, 255, true)]
    [InlineData(0, 0, 0, true)]
    [InlineData(255, 255, 255, false)]
    [InlineData(0, 0, 0, false)]
    public void WhenSavingAJpegWithAFillColourThenTransparentAreasTakeThatColour(byte r, byte g, byte b, bool layerMode)
    {
        // Two fill paths: a layer-mode flatten is filled in place, the single working bitmap is
        // copied onto the fill.
        if (!layerMode) _sut.DisableLayerMode();
        ExtendRightBy(20);

        var strip = SaveJpegAndReadPixel(new SKColor(r, g, b), x: 110, y: 40);
        var original = SaveJpegAndReadPixel(new SKColor(r, g, b), x: 50, y: 40);

        // JPEG is lossy: allow a small tolerance per channel.
        strip.Red.Should().BeCloseTo(r, 8);
        strip.Green.Should().BeCloseTo(g, 8);
        strip.Blue.Should().BeCloseTo(b, 8);
        original.Red.Should().BeCloseTo(255, 8);
        original.Green.Should().BeCloseTo(0, 8);
        original.Blue.Should().BeCloseTo(0, 8);
    }

    [Fact]
    public void WhenSavingAJpegWithAFillColourThenHalfTransparentPixelsBlendIntoIt()
    {
        // 50% red over white is (255, 128, 128). Dropping the alpha instead of blending would
        // give the premultiplied (128, 0, 0) — the dark red the encoder produces on its own.
        _sut.Layers![0].Opacity = 0.5f;

        var pixel = SaveJpegAndReadPixel(SKColors.White, x: 50, y: 40);

        pixel.Red.Should().BeCloseTo(255, 8);
        pixel.Green.Should().BeCloseTo(128, 8);
        pixel.Blue.Should().BeCloseTo(128, 8);
    }

    [Fact]
    public void WhenAMoveIsPendingThenTheCheckSeesTheGapItLeavesWithoutCommittingIt()
    {
        // The check runs before the user answers the prompt. Committing there would bake an open
        // Move/Transform (or placed text) into the layer even when the user then cancels.
        ArmMoveTool();
        _sut.LayerTransformTool.Nudge(30, 0);

        _sut.HasTransparency().Should().BeTrue("the layer moved 30 px right, leaving the left edge empty");

        _sut.LayerTransformTool.HasTransform.Should().BeTrue("the move is still pending, not committed");
        _sut.Layers![0].OffsetX.Should().Be(0);
    }

    [Fact]
    public void WhenAPendingScaleEndsBetweenPixelsThenTheCheckAgreesWithTheCommit()
    {
        // A scale is committed with anti-aliasing and Mitchell sampling. An edge that ends 0.25 px
        // inside the canvas comes out partly transparent there, and the JPEG darkens it. A check
        // that drew the pending scale any other way would miss that edge and not ask.
        ArmMoveTool();
        _sut.LayerTransformTool.SetSize(99.5f, 79.6f);
        _sut.LayerTransformTool.SetPosition(0.25f, 0.2f);

        var checkedBeforeCommit = _sut.HasTransparency();
        _sut.CommitPendingOperations();
        using var committed = _sut.Layers!.Flatten()!;

        BitmapTransparency.HasTransparentPixels(committed).Should().BeTrue("the committed edges are anti-aliased");
        checkedBeforeCommit.Should().BeTrue("the check must see what the commit produces");
    }

    [Fact]
    public void WhenAPendingScalePushesTheTransparentStripOffTheCanvasThenThereIsNoTransparency()
    {
        // The pending transform counts as committed: doubling the layer from its top-left corner
        // moves the empty strip past the canvas edge, leaving nothing for a JPEG to lose.
        ExtendRightBy(20);
        ArmMoveTool();
        _sut.LayerTransformTool.SetSize(240f, 160f);

        _sut.HasTransparency().Should().BeFalse();
        _sut.LayerTransformTool.HasTransform.Should().BeTrue("the check commits nothing");
    }

    [Fact]
    public void WhenTheSaveWouldRefuseAPendingTransformThenTheCheckSeesTheLayerUnchanged()
    {
        // Past the size guard the commit is refused and the save writes the layer as it is, strip
        // included. The check has to expect that, not the transform the canvas previews.
        ExtendRightBy(20);
        ArmMoveTool();
        _sut.LayerTransformTool.SetSize(24000f, 16000f);

        var checkedBeforeCommit = _sut.HasTransparency();
        _sut.CommitPendingOperations();
        using var committed = _sut.Layers!.Flatten()!;

        BitmapTransparency.HasTransparentPixels(committed).Should().BeTrue("the commit was refused");
        checkedBeforeCommit.Should().BeTrue("the check must see what the save writes");
    }

    [Fact]
    public void WhenTheFillColourIsTranslucentThenTheFillIsStillOpaque()
    {
        // A half-transparent fill would leave the areas half-transparent, and the encoder would
        // darken them to grey again.
        ExtendRightBy(20);

        var strip = SaveJpegAndReadPixel(new SKColor(255, 255, 255, 128), x: 110, y: 40);

        strip.Red.Should().BeCloseTo(255, 8);
        strip.Green.Should().BeCloseTo(255, 8);
        strip.Blue.Should().BeCloseTo(255, 8);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenSavingWithAFillThenTheCanvasKeepsItsTransparency(bool layerMode)
    {
        // The fill belongs to the file only. Outside layer mode the bitmap written is the
        // document itself, so filling it in place would paint over the canvas.
        if (!layerMode) _sut.DisableLayerMode();
        ExtendRightBy(20);

        SaveJpegAndReadPixel(SKColors.White, x: 110, y: 40);

        _sut.HasTransparency().Should().BeTrue();
    }

    [Fact]
    public void WhenCheckingForTransparencyThenTheLayersAreNotFlattened()
    {
        ExtendRightBy(20);
        _sut.AddLayer("Top").Should().NotBeNull();

        _sut.HasTransparency();

        _sut.Layers!.Count.Should().Be(2);
        _sut.Layers[0].Bitmap!.GetPixel(110, 40).Alpha.Should().Be(0);
    }
}
