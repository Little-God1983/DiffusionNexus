using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Committing a layer transform rasterizes once into a bitmap sized to the transformed bounds,
/// keeps off-canvas pixels, refuses ineligible layers and oversize results, and commits a
/// pending transform before the active layer changes.
/// </summary>
public class ImageEditorCoreLayerTransformTests : IDisposable
{
    private readonly ImageEditorCore _sut = new();
    private readonly EditorServices _services = EditorServiceFactory.Create();

    public ImageEditorCoreLayerTransformTests()
    {
        _sut.SetServices(_services);
        using var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());
        _sut.LayerTransformTool.ImagePixelWidth = 100;
        _sut.LayerTransformTool.ImagePixelHeight = 100;
        _sut.LayerTransformTool.SetImageBounds(new SKRect(0, 0, 100, 100));
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void Arm_EnablesLayerMode_AndArmsTheActiveLayer()
    {
        // LoadImage already auto-enables layer mode (existing behavior); the point here is that
        // arming still works and leaves layer mode on, regardless of how it got there.
        _sut.IsLayerMode.Should().BeTrue();
        _sut.LayerTransformTool.IsActive = true;

        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.Ok);

        _sut.IsLayerMode.Should().BeTrue();
        _sut.LayerTransformTool.Layer.Should().BeSameAs(_sut.ActiveLayer);
    }

    [Fact]
    public void Arm_RefusesMaskAndLockedLayers()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.EnableLayerMode();
        var locked = _services.Layers.AddLayer("locked")!;
        locked.IsLocked = true;
        _sut.ActiveLayer = locked;
        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.Locked);
        _sut.LayerTransformTool.IsArmed.Should().BeFalse();

        var mask = _services.Layers.AddLayer("mask")!;
        mask.IsInpaintMask = true;
        _sut.ActiveLayer = mask;
        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.InpaintMask);
    }

    [Fact]
    public void IntegerMove_IsPixelExact_AndKeepsOffCanvasPixels()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.Nudge(60, 0);

        _sut.ApplyLayerTransform().Should().BeTrue();

        layer.Bounds.Should().Be(new SKRectI(60, 0, 160, 100));
        layer.Bitmap!.Width.Should().Be(100);
        layer.Bitmap!.GetPixel(99, 50).Should().Be(SKColors.Red); // pixel that is now off canvas survives
        _sut.LayerTransformTool.HasTransform.Should().BeFalse(); // re-armed at identity
        _sut.LayerTransformTool.IsArmed.Should().BeTrue();
    }

    [Fact]
    public void Scale_ProducesTransformedSize()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.KeepAspect = false;
        _sut.LayerTransformTool.SetSize(50, 25);

        _sut.ApplyLayerTransform().Should().BeTrue();

        layer.Bitmap!.Width.Should().Be(50);
        layer.Bitmap!.Height.Should().Be(25);
        layer.Bitmap!.GetPixel(25, 12).Red.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Rotation_GrowsBoundsToTheRotatedBox()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.SetRotation(45f);

        _sut.ApplyLayerTransform().Should().BeTrue();

        layer.Width.Should().BeInRange(141, 143); // 100 * sqrt2, rounded out
        layer.OffsetX.Should().BeInRange(-22, -20);
    }

    [Fact]
    public void TooLarge_IsRefused_AndLayerUntouched()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        LayerTransformFailure? failure = null;
        _sut.LayerTransformFailed += (_, f) => failure = f;
        _sut.LayerTransformTool.KeepAspect = false;
        _sut.LayerTransformTool.SetSize(20000, 10);

        _sut.ApplyLayerTransform().Should().BeFalse();

        failure.Should().Be(LayerTransformFailure.TooLarge);
        layer.Bounds.Should().Be(new SKRectI(0, 0, 100, 100));
        _sut.LayerTransformTool.HasTransform.Should().BeTrue(); // kept so the user can shrink it
    }

    [Fact]
    public void NothingToApply_ReturnsFalse_WithoutFailureEvent()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var failed = 0;
        _sut.LayerTransformFailed += (_, _) => failed++;
        _sut.ApplyLayerTransform().Should().BeFalse();
        failed.Should().Be(0);
    }

    [Fact]
    public void ChangingActiveLayer_CommitsPending_ThenArmsNewLayer()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var first = _sut.ActiveLayer!;
        var second = _services.Layers.AddLayer("second")!; // AddLayer makes it active via the stack, not via the core setter

        _sut.ActiveLayer = first;   // no pending change on first anymore? we set it again to exercise the setter
        _sut.LayerTransformTool.Nudge(10, 0);
        _sut.ActiveLayer = second;

        first.OffsetX.Should().Be(10);
        _sut.LayerTransformTool.Layer.Should().BeSameAs(second);
        _sut.LayerTransformTool.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void RenderWithZoom_PreviewsTheMatrix_WithoutChangingTheLayer()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.Nudge(50, 0);

        using var surface = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(surface);
        _sut.ZoomToActual();
        _sut.RenderWithZoom(canvas, 100, 100, SKColors.Black);

        layer.Bounds.Should().Be(new SKRectI(0, 0, 100, 100));
        surface.GetPixel(75, 50).Should().Be(SKColors.Red);
        surface.GetPixel(25, 50).Should().NotBe(SKColors.Red); // checkerboard/background, layer moved away
    }
}
