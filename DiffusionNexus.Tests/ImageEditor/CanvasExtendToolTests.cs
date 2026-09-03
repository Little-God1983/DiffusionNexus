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
    public void SetTargetSize_OnlyResplitsTheAxisThatChanged()
    {
        var tool = CreateActive();
        tool.SetAnchor(CanvasAnchor.Center);
        tool.SetExtension(0, 300, 0, 0); // 1300 x 1000, all of it on the right

        tool.SetTargetSize(1300, 1200); // width unchanged, height grows

        tool.ExtendRight.Should().Be(300, "the width was not touched, so it must not be re-centred");
        tool.ExtendLeft.Should().Be(0);
        tool.ExtendTop.Should().Be(100);
        tool.ExtendBottom.Should().Be(100);
    }

    [Fact]
    public void DefaultPlacement_IsTopLeft_SoATypedSizeGrowsRightAndDown()
    {
        var tool = CreateActive();
        tool.Anchor.Should().Be(CanvasAnchor.TopLeft);

        tool.SetTargetSize(2000, 1500);

        (tool.ExtendLeft, tool.ExtendTop, tool.ExtendRight, tool.ExtendBottom).Should().Be((0, 0, 1000, 500));
    }

    [Theory]
    [InlineData(CanvasAnchor.TopLeft, 0, 0, 400, 200)]
    [InlineData(CanvasAnchor.Top, 200, 0, 200, 200)]
    [InlineData(CanvasAnchor.TopRight, 400, 0, 0, 200)]
    [InlineData(CanvasAnchor.Left, 0, 100, 400, 100)]
    [InlineData(CanvasAnchor.Center, 200, 100, 200, 100)]
    [InlineData(CanvasAnchor.Right, 400, 100, 0, 100)]
    [InlineData(CanvasAnchor.BottomLeft, 0, 200, 400, 0)]
    [InlineData(CanvasAnchor.Bottom, 200, 200, 200, 0)]
    [InlineData(CanvasAnchor.BottomRight, 400, 200, 0, 0)]
    public void SetAnchor_MovesTheImageInsideTheCurrentFrame_WithoutChangingItsSize(
        CanvasAnchor anchor, int left, int top, int right, int bottom)
    {
        var tool = CreateActive();
        tool.SetTargetSize(1400, 1200); // 400 x 200 extra, top-left by default

        tool.SetAnchor(anchor);

        (tool.ExtendLeft, tool.ExtendTop, tool.ExtendRight, tool.ExtendBottom).Should().Be((left, top, right, bottom));
        tool.GetNewDimensions().Should().Be((1400, 1200));
        tool.Anchor.Should().Be(anchor);
    }

    [Fact]
    public void SetAnchor_Custom_IsIgnored()
    {
        var tool = CreateActive();
        tool.SetAnchor(CanvasAnchor.Center);

        tool.SetAnchor(CanvasAnchor.Custom);

        tool.Anchor.Should().Be(CanvasAnchor.Center);
    }

    [Fact]
    public void AspectPreset_GrowsAwayFromTheAnchor()
    {
        var tool = CreateActive();
        tool.SetAnchor(CanvasAnchor.Right);

        tool.SetAspectRatio(2, 1); // 1000x1000 -> 2000x1000, image pinned to the right edge

        (tool.ExtendLeft, tool.ExtendRight).Should().Be((1000, 0));
        tool.GetNewDimensions().Should().Be((2000, 1000));
    }

    [Fact]
    public void DraggingTheImage_ShiftsTheExtensionToTheOppositeEdges_AndMakesThePlacementCustom()
    {
        var tool = CreateActive(scale: 0.5f); // 1000 px image drawn 500 px wide
        tool.SetTargetSize(1400, 1200);       // 400 right, 200 bottom

        tool.IsMovePoint(new SKPoint(250, 250)).Should().BeTrue();
        tool.OnPointerPressed(new SKPoint(250, 250)).Should().BeTrue();
        tool.IsMovingImage.Should().BeTrue();
        tool.OnPointerMoved(new SKPoint(300, 270)); // +50 screen px = +100 image px right, +20 = +40 px down
        tool.OnPointerReleased();

        (tool.ExtendLeft, tool.ExtendTop, tool.ExtendRight, tool.ExtendBottom).Should().Be((100, 40, 300, 160));
        tool.GetNewDimensions().Should().Be((1400, 1200), "moving the image never changes the canvas size");
        tool.Anchor.Should().Be(CanvasAnchor.Custom);
        tool.IsMovingImage.Should().BeFalse();
    }

    [Fact]
    public void DraggingTheImage_StopsAtTheFrameEdges()
    {
        var tool = CreateActive();
        tool.SetTargetSize(1400, 1000); // 400 right, nothing vertical

        tool.OnPointerPressed(new SKPoint(500, 500));
        tool.OnPointerMoved(new SKPoint(1500, 900)); // way past the right edge, and down where there is no room
        tool.OnPointerReleased();

        (tool.ExtendLeft, tool.ExtendRight).Should().Be((400, 0));
        (tool.ExtendTop, tool.ExtendBottom).Should().Be((0, 0));
    }

    [Fact]
    public void DraggingTheImage_WithoutMoving_KeepsTheChosenAnchor()
    {
        var tool = CreateActive();
        tool.SetAnchor(CanvasAnchor.Center);
        tool.SetTargetSize(1400, 1000);

        tool.OnPointerPressed(new SKPoint(500, 500));
        tool.OnPointerMoved(new SKPoint(500, 500));
        tool.OnPointerReleased();

        tool.Anchor.Should().Be(CanvasAnchor.Center);
    }

    [Fact]
    public void MovePoint_NeedsRoomToMove_AndLosesToHandles()
    {
        var tool = CreateActive();

        tool.IsMovePoint(new SKPoint(500, 500)).Should().BeFalse("without an extension there is nowhere to go");
        tool.OnPointerPressed(new SKPoint(500, 500)).Should().BeFalse();

        tool.SetTargetSize(1400, 1000);
        tool.IsMovePoint(new SKPoint(500, 500)).Should().BeTrue();
        tool.IsMovePoint(new SKPoint(0, 0)).Should().BeFalse("the top-left handle sits on the image corner");
        tool.IsMovePoint(new SKPoint(1200, 500)).Should().BeFalse("that point is in the new area, not on the image");
    }

    [Fact]
    public void CustomPlacement_KeepsTheImageOffset_AndGrowsOrShrinksAtRightAndBottom()
    {
        var tool = CreateActive();
        tool.SetTargetSize(1400, 1200);
        tool.OnPointerPressed(new SKPoint(500, 500));
        tool.OnPointerMoved(new SKPoint(600, 550)); // image now 100 from the left, 50 from the top
        tool.OnPointerReleased();

        tool.SetTargetSize(2000, 1200);
        (tool.ExtendLeft, tool.ExtendRight).Should().Be((100, 900));

        tool.SetTargetSize(1050, 1020); // less room than the offset: the offset gives way
        (tool.ExtendLeft, tool.ExtendRight).Should().Be((50, 0));
        (tool.ExtendTop, tool.ExtendBottom).Should().Be((20, 0));
    }

    [Fact]
    public void Reset_RevertsACustomPlacement_ButKeepsAChosenAnchor()
    {
        var tool = CreateActive();
        tool.SetTargetSize(1400, 1000);
        tool.OnPointerPressed(new SKPoint(500, 500));
        tool.OnPointerMoved(new SKPoint(600, 500));
        tool.OnPointerReleased();
        tool.Anchor.Should().Be(CanvasAnchor.Custom);

        tool.Reset();
        tool.Anchor.Should().Be(CanvasAnchor.TopLeft);

        tool.SetAnchor(CanvasAnchor.BottomRight);
        tool.Reset();
        tool.Anchor.Should().Be(CanvasAnchor.BottomRight);
    }

    [Fact]
    public void ToolId_IsRegistered()
    {
        DiffusionNexus.UI.ImageEditor.Services.ToolIds.CanvasExtend.Should().Be("CanvasExtend");
    }
}
