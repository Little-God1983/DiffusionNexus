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

        _sut.ActiveLayer = first;   // re-select the already-active layer: a no-op through the setter
        _sut.LayerTransformTool.Nudge(10, 0);
        _sut.ActiveLayer = second;

        first.OffsetX.Should().Be(10);
        _sut.LayerTransformTool.Layer.Should().BeSameAs(second);
        _sut.LayerTransformTool.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void ChangingActiveLayer_UpdatesTheStackFirst_SoTheCommitSeesTheNewActiveLayer()
    {
        // F1: assigning the stack's ActiveLayer before committing the pending transform means a
        // SyncLayers triggered (re-entrantly) by the commit sees the stack already pointing at
        // the new layer, not the one being nudged away from. Captured via LayerTransformApplied,
        // which ApplyLayerTransform raises synchronously from inside the Commit() call.
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var first = _sut.ActiveLayer!;
        var second = _services.Layers.AddLayer("second")!;
        _sut.ActiveLayer = first;
        _sut.LayerTransformTool.Nudge(10, 0);

        Layer? activeLayerDuringCommit = null;
        EventHandler onApplied = (_, _) => activeLayerDuringCommit = _services.Layers.ActiveLayer;
        _sut.LayerTransformApplied += onApplied;
        try
        {
            _sut.ActiveLayer = second;
        }
        finally
        {
            _sut.LayerTransformApplied -= onApplied;
        }

        activeLayerDuringCommit.Should().BeSameAs(second);
        _services.Layers.ActiveLayer.Should().BeSameAs(second);
        first.OffsetX.Should().Be(10);
    }

    [Fact]
    public void RemoveLayer_CommitsPending_AndStaysArmedOnTheActiveLayer()
    {
        // F2: RemoveLayer mutates the stack directly (bypassing the ActiveLayer setter), so it
        // must commit a pending transform itself or the nudge is silently lost.
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var armed = _sut.ActiveLayer!;
        var other = _services.Layers.AddLayer("other")!;
        _sut.ActiveLayer = armed;
        _sut.LayerTransformTool.Nudge(15, 0);

        _sut.RemoveLayer(other).Should().BeTrue();

        armed.OffsetX.Should().Be(15);
        _sut.LayerTransformTool.IsArmed.Should().BeTrue();
        _sut.LayerTransformTool.Layer.Should().BeSameAs(_sut.ActiveLayer);
    }

    [Fact]
    public void MergeLayerDown_CommitsThePendingNudge_ThenArmsTheMergedResult()
    {
        // F2: merging the armed layer disposes it; the pending nudge must be rasterized into it
        // before the merge, and the tool must re-arm on the surviving (merged) layer, never the
        // disposed one.
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var below = _sut.ActiveLayer!;
        var top = _services.Layers.AddLayer("top")!;
        _sut.ActiveLayer = top;
        _sut.LayerTransformTool.Nudge(20, 0);

        _sut.MergeLayerDown(top).Should().BeTrue();

        _sut.LayerTransformTool.IsArmed.Should().BeTrue();
        _sut.LayerTransformTool.Layer.Should().BeSameAs(below);
        _sut.LayerTransformTool.Layer!.Bitmap.Should().NotBeNull();
        below.Bounds.Should().Be(new SKRectI(0, 0, 120, 100)); // union of (0,0,100,100) and the nudged top (20,0,120,100)
    }

    [Fact]
    public void RotateRight_CommitsAPendingTransform_ThenRearmsAtIdentityOnTheNewBounds()
    {
        // F3: a whole-image rotate/flip changes every layer's bounds out from under the tool. A
        // pending transform must land first (never silently lost), and the tool must re-arm
        // against the post-rotation bounds instead of keeping stale _sourceBounds.
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        _sut.LayerTransformTool.SetRotation(45f);

        _sut.RotateRight().Should().BeTrue();

        _sut.LayerTransformTool.IsArmed.Should().BeTrue();
        _sut.LayerTransformTool.SourceBounds.Should().Be(_sut.LayerTransformTool.Layer!.Bounds);
        _sut.LayerTransformTool.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void Crop_CommitsAPendingNudge_ThenRearmsAtIdentityOnTheNewBounds()
    {
        // F: Crop replaces layer bitmaps behind the tool's back (like RotateRight). A pending
        // nudge must land first, and the tool must re-arm against the cropped bounds.
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        _sut.LayerTransformTool.Nudge(10, 0);
        var layer = _sut.ActiveLayer!;

        _sut.CropTool.IsActive = true;
        _sut.CropTool.SetImageBounds(new SKRect(0, 0, 100, 100));
        _sut.CropTool.OnPointerPressed(new SKPoint(20, 20));
        _sut.CropTool.OnPointerMoved(new SKPoint(80, 80));
        _sut.CropTool.OnPointerReleased();
        _sut.ApplyCrop().Should().BeTrue();

        // Nudge(10,0) shifts bounds to (10,0)-(110,100) before the crop clips to (20,20)-(80,80).
        layer.Bounds.Should().Be(new SKRectI(0, 0, 60, 60));
        layer.Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
        _sut.LayerTransformTool.SourceBounds.Should().Be(_sut.LayerTransformTool.Layer!.Bounds);
        _sut.LayerTransformTool.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void LoadLayeredTiff_DisarmsThenRearms_OnTheNewActiveLayer()
    {
        // F: loading a layered TIFF discards the current stack and rebuilds it (like
        // SwapLoadedBitmaps); the tool must disarm the dead layer, not commit into it, and
        // re-arm on the freshly loaded active layer so the panel isn't left stale.
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();

        var path = Path.Combine(Path.GetTempPath(), $"diffnexus_test_{Guid.NewGuid():N}.tiff");
        try
        {
            TiffExporter.SaveLayeredTiff(_sut.Layers!, path).Should().BeTrue();

            _sut.LoadLayeredTiff(path).Should().BeTrue();

            _sut.LayerTransformTool.IsArmed.Should().BeTrue();
            _sut.LayerTransformTool.Layer!.Bitmap.Should().NotBeNull();
            _sut.LayerTransformTool.Layer.Should().BeSameAs(_sut.ActiveLayer);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FlipHorizontal_OnOddSizedLayerAtOrigin_AppliesExactlyOntoItself()
    {
        // Pivot truncation (int MidX/MidY) would rasterize the flip half a pixel off, growing
        // the applied bounds beyond the original 5x5 box.
        using var editor = new ImageEditorCore();
        editor.SetServices(EditorServiceFactory.Create());
        using var bitmap = new SKBitmap(5, 5, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        editor.LoadImage(data.ToArray());
        editor.LayerTransformTool.ImagePixelWidth = 5;
        editor.LayerTransformTool.ImagePixelHeight = 5;
        editor.LayerTransformTool.SetImageBounds(new SKRect(0, 0, 5, 5));
        editor.LayerTransformTool.IsActive = true;
        editor.ArmLayerTransform();
        var layer = editor.ActiveLayer!;

        editor.LayerTransformTool.FlipHorizontal();
        editor.ApplyLayerTransform().Should().BeTrue();

        layer.Bounds.Should().Be(new SKRectI(0, 0, 5, 5));
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
