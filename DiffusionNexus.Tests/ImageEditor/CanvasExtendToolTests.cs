using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// The Canvas Extend tool: crop-style round handles that sit ON the extended frame,
/// outward-only dragging, a checkerboard preview of the new (transparent) area, and a
/// frame that is visible from the moment the tool activates.
/// </summary>
public class CanvasExtendToolTests
{
    private const int Size = 1000;

    private static CanvasExtendTool CreateActive(float scale = 1f)
    {
        var tool = new CanvasExtendTool { IsActive = true, ImagePixelWidth = Size, ImagePixelHeight = Size };
        tool.SetImageBounds(new SKRect(0, 0, Size * scale, Size * scale));
        return tool;
    }

    [Fact]
    public void FitMargin_IsThirtyTwo()
    {
        CreateActive().FitMargin.Should().Be(32f);
    }

    [Fact]
    public void Handles_SitOnFrameCornersAndEdgeMidpoints_WithTwelvePixelHitRadius()
    {
        var tool = CreateActive();

        tool.GetCursorForPoint(new SKPoint(0, 0)).Should().Be(OutpaintHandle.TopLeft);
        tool.GetCursorForPoint(new SKPoint(Size / 2f, 0)).Should().Be(OutpaintHandle.Top);
        tool.GetCursorForPoint(new SKPoint(Size, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size, Size)).Should().Be(OutpaintHandle.BottomRight);
        tool.GetCursorForPoint(new SKPoint(Size + 11, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size + 13, Size / 2f)).Should().Be(OutpaintHandle.None);
        tool.GetCursorForPoint(new SKPoint(Size / 2f, Size / 2f)).Should().Be(OutpaintHandle.None); // no "move" inside
    }

    [Fact]
    public void Handles_FollowTheExtendedFrame()
    {
        var tool = CreateActive();
        tool.SetExtension(0, 200, 0, 0);

        tool.GetCursorForPoint(new SKPoint(Size + 200, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size, Size / 2f)).Should().Be(OutpaintHandle.None);
    }

    [Fact]
    public void DraggingRightHandle_AtHalfZoom_AddsTwoImagePixelsPerScreenPixel()
    {
        var tool = CreateActive(scale: 0.5f); // 1000 px image drawn 500 px wide

        tool.OnPointerPressed(new SKPoint(500, 250)).Should().BeTrue();
        tool.OnPointerMoved(new SKPoint(550, 250));
        tool.OnPointerReleased();

        tool.ExtendRight.Should().Be(100);
        tool.GetNewDimensions().Should().Be((1100, 1000));
    }

    [Fact]
    public void InactiveTool_IgnoresPointer()
    {
        var tool = CreateActive();
        tool.IsActive = false;

        tool.OnPointerPressed(new SKPoint(Size, Size / 2f)).Should().BeFalse();
        tool.GetCursorForPoint(new SKPoint(Size, Size / 2f)).Should().Be(OutpaintHandle.None);
    }

    [Fact]
    public void Render_WhenActiveWithoutExtension_DrawsTheFrameOnTheImageEdge()
    {
        var tool = CreateActive();
        using var bitmap = new SKBitmap(1200, 1200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        tool.Render(canvas, new SKRect(0, 0, 1200, 1200));

        // A handle is drawn at the top-left corner of the frame (white fill).
        bitmap.GetPixel(0, 0).Should().Be(SKColors.White);
        // Nothing is drawn far away from the frame.
        bitmap.GetPixel(1150, 1150).Should().Be(SKColors.Black);
    }

    [Fact]
    public void Render_WithExtension_PaintsTheNewAreaAndLeavesTheImageAlone()
    {
        var tool = CreateActive();
        tool.SetExtension(0, 100, 0, 0); // right strip: x in [1000, 1100)
        using var bitmap = new SKBitmap(1200, 1200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        tool.Render(canvas, new SKRect(0, 0, 1200, 1200));

        bitmap.GetPixel(1050, 500).Should().NotBe(SKColors.Black, "the new area gets the checker + tint");
        bitmap.GetPixel(500, 500).Should().Be(SKColors.Black, "the image area is not painted over");
    }

    [Fact]
    public void ToolId_IsRegistered()
    {
        DiffusionNexus.UI.ImageEditor.Services.ToolIds.CanvasExtend.Should().Be("CanvasExtend");
    }
}
