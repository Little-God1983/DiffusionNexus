using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Fit mode must keep an extension tool's frame (image + extension + handle margin) on
/// screen, and must reduce to the plain image fit when nothing is extended.
/// </summary>
public class ViewportFitTests
{
    [Fact]
    public void NoExtensionNoMargin_EqualsPlainFit()
    {
        var (rect, scale) = ImageEditorCore.CalculateFitRectWithExtension(1000, 500, 0, 0, 0, 0, 0f, 800f, 800f);

        scale.Should().BeApproximately(0.8f, 0.0001f);
        rect.Left.Should().BeApproximately(0f, 0.001f);
        rect.Top.Should().BeApproximately(200f, 0.001f);
        rect.Width.Should().BeApproximately(800f, 0.001f);
        rect.Height.Should().BeApproximately(400f, 0.001f);
    }

    [Fact]
    public void Extension_ShrinksScaleSoTheFrameFits_AndOffsetsTheImage()
    {
        // 1000x1000 image + 500 px on the right => 1500x1000 frame in an 800x800 box with a 32 px margin
        var (rect, scale) = ImageEditorCore.CalculateFitRectWithExtension(1000, 1000, 0, 0, 500, 0, 32f, 800f, 800f);

        scale.Should().BeApproximately(736f / 1500f, 0.0001f);
        var frameWidth = 1500f * scale;
        var frameLeft = (800f - frameWidth) / 2f;
        rect.Left.Should().BeApproximately(frameLeft, 0.001f);           // no left extension: image starts at the frame
        rect.Right.Should().BeApproximately(frameLeft + 1000f * scale, 0.001f);
        (frameLeft + frameWidth).Should().BeLessThanOrEqualTo(800f - 32f + 0.001f);
    }

    [Fact]
    public void LeftAndTopExtension_MoveTheImageInsideTheFrame()
    {
        var (rect, scale) = ImageEditorCore.CalculateFitRectWithExtension(1000, 1000, 200, 100, 0, 0, 0f, 600f, 600f);

        // frame 1200x1100 in 600x600 => scale 0.5, frame is 600x550 at (0, 25)
        scale.Should().BeApproximately(0.5f, 0.0001f);
        rect.Left.Should().BeApproximately(100f, 0.001f);  // 0 + 200*0.5
        rect.Top.Should().BeApproximately(75f, 0.001f);    // 25 + 100*0.5
    }

    [Fact]
    public void RenderWithZoom_InFitMode_KeepsFitModeOn()
    {
        // Pre-existing defect: the fit branch wrote through Viewport.ZoomLevel, whose setter
        // clears IsFitMode, so fit mode died on the first render. The extend rule needs it alive.
        using var core = new ImageEditorCore();
        var services = EditorServiceFactory.Create();
        core.SetServices(services);
        using (var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            core.LoadImage(data.ToArray());
        }
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        services.Viewport.IsFitMode.Should().BeTrue();
        core.ZoomLevel.Should().BeApproximately(4f, 0.0001f);
    }

    [Fact]
    public void RenderWithZoom_InFitMode_ZoomsOutWhenTheExtendToolGrowsTheFrame()
    {
        using var core = new ImageEditorCore();
        core.SetServices(EditorServiceFactory.Create());
        using (var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            core.LoadImage(data.ToArray());
        }
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        var plain = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        var plainZoom = core.ZoomLevel;

        core.CanvasExtendTool.IsActive = true;
        core.CanvasExtendTool.ImagePixelWidth = 100;
        core.CanvasExtendTool.ImagePixelHeight = 100;
        core.CanvasExtendTool.SetExtension(0, 100, 0, 0); // 200x100 frame

        var extended = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        plain.Width.Should().BeApproximately(400f, 0.001f);
        extended.Width.Should().BeLessThan(plain.Width);
        core.ZoomLevel.Should().BeLessThan(plainZoom);
        // frame = image rect + 100 px * scale to the right must stay inside 400 - 32
        (extended.Right + 100f * core.ZoomLevel).Should().BeLessThanOrEqualTo(400f - 32f + 0.001f);
    }

    [Fact]
    public void RenderWithZoom_WhileTheFrameIsDragged_PinsTheImage_AndRefitsOnRelease()
    {
        using var core = new ImageEditorCore();
        core.SetServices(EditorServiceFactory.Create());
        core.LoadImage(EncodePng(100, 100));
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        var tool = core.CanvasExtendTool;
        tool.IsActive = true;
        tool.ImagePixelWidth = 100;
        tool.ImagePixelHeight = 100;
        tool.SetAnchor(CanvasAnchor.Center);
        tool.SetExtension(0, 100, 0, 100); // 300 x 100 frame, image in the middle
        var before = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        // Grab the frame in the new area and drag it right: the image must not move on screen.
        tool.OnPointerPressed(new SKPoint(before.Right + 5, before.MidY)).Should().BeTrue();
        tool.OnPointerMoved(new SKPoint(before.Right + 45, before.MidY));
        var during = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        during.Should().Be(before);
        tool.ExtendRight.Should().BeGreaterThan(100);

        // On release fit mode re-centres the frame, so the image ends up further left.
        tool.OnPointerReleased();
        var after = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        after.Left.Should().BeLessThan(before.Left);
        after.Width.Should().BeApproximately(before.Width, 0.001f, "the total size, and so the fit scale, did not change");
    }

    [Fact]
    public void LeavingFitMode_ThenPanning_MovesTheViewportWithoutChangingZoom()
    {
        // The control's middle-drag pan clears fit mode before calling Pan, because
        // ViewportManager.Pan is a no-op while fit mode is on. This pins that contract.
        using var core = new ImageEditorCore();
        var services = EditorServiceFactory.Create();
        core.SetServices(services);
        core.LoadImage(EncodePng(100, 100));
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        var zoomBeforePan = services.Viewport.ZoomLevel;

        core.IsFitMode = false;
        core.Pan(10, 5);

        services.Viewport.PanX.Should().BeApproximately(10f, 0.0001f);
        services.Viewport.PanY.Should().BeApproximately(5f, 0.0001f);
        services.Viewport.ZoomLevel.Should().BeApproximately(zoomBeforePan, 0.0001f);
    }

    [Fact]
    public void RenderWithZoom_FitBelowMinZoom_FiresChangedAtMostOnce()
    {
        // 8000x8000 in a 400x400 canvas fits at 0.05, below MinZoom (0.1). The fit branch
        // compares against the CLAMPED value, so it must not re-fire Changed every frame.
        using var core = new ImageEditorCore();
        var services = EditorServiceFactory.Create();
        core.SetServices(services);
        core.LoadImage(EncodePng(8000, 8000));
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        var changed = 0;
        services.Viewport.Changed += (_, _) => changed++;

        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        changed.Should().BeLessThanOrEqualTo(1);
        services.Viewport.IsFitMode.Should().BeTrue();
    }

    [Fact]
    public void ActiveExtensionTool_PrefersCanvasExtendOverOutpaint_AndFallsBackToOutpaint()
    {
        using var core = new ImageEditorCore();
        core.SetServices(EditorServiceFactory.Create());
        core.LoadImage(EncodePng(100, 100));
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        core.OutpaintTool.IsActive = true;
        core.OutpaintTool.ImagePixelWidth = 100;
        core.OutpaintTool.ImagePixelHeight = 100;
        core.OutpaintTool.SetExtension(0, 100, 0, 0);

        var outpaintOnly = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        // Outpaint's 72 px margin: the frame's right edge lands exactly on 400 - 72.
        (outpaintOnly.Right + 100f * core.ZoomLevel).Should().BeApproximately(400f - 72f, 0.01f);

        core.CanvasExtendTool.IsActive = true;
        core.CanvasExtendTool.ImagePixelWidth = 100;
        core.CanvasExtendTool.ImagePixelHeight = 100;
        core.CanvasExtendTool.SetExtension(0, 100, 0, 0);

        var bothActive = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        // Canvas Extend wins: its 32 px margin leaves the frame wider.
        (bothActive.Right + 100f * core.ZoomLevel).Should().BeApproximately(400f - 32f, 0.01f);
    }

    private static byte[] EncodePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
