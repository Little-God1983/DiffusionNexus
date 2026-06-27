using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Tests that linear shapes (arrow, line) are manipulated by their two endpoints rather than
/// a rotated bounding box. The old box model canonicalized the points to min/max corners on
/// resize, which flipped the arrow's head and tail; endpoint dragging preserves direction.
/// </summary>
public class ShapeToolArrowTests
{
    private const int Size = 1000; // square image, rendered at 100% zoom (screen == image px)

    private static ShapeTool CreatePlacedLinear(SKPoint from, SKPoint to, ShapeType type)
    {
        var tool = new ShapeTool { IsActive = true, ShapeType = type, ImagePixelWidth = Size };
        tool.SetImageBounds(new SKRect(0, 0, Size, Size));
        tool.OnPointerPressed(from);   // Idle -> Drawing
        tool.OnPointerMoved(to);
        tool.OnPointerReleased();      // Drawing -> Placed
        return tool;
    }

    private static SKPoint Screen(SKPoint normalized) => new(normalized.X * Size, normalized.Y * Size);

    [Fact]
    public void DrawingArrow_PreservesPointedDirection()
    {
        // Arrow pointing up-right: tail bottom-left, head top-right.
        var tool = CreatePlacedLinear(new SKPoint(200, 800), new SKPoint(800, 200), ShapeType.Arrow);

        tool.PlacedShape.Should().NotBeNull();
        tool.PlacedShape!.NormalizedStart.X.Should().BeApproximately(0.2f, 0.001f);
        tool.PlacedShape.NormalizedStart.Y.Should().BeApproximately(0.8f, 0.001f);
        tool.PlacedShape.NormalizedEnd.X.Should().BeApproximately(0.8f, 0.001f);
        tool.PlacedShape.NormalizedEnd.Y.Should().BeApproximately(0.2f, 0.001f);
    }

    [Fact]
    public void HitTest_ReturnsEndpointHandles_NotCornersOrRotation()
    {
        var tool = CreatePlacedLinear(new SKPoint(200, 800), new SKPoint(800, 200), ShapeType.Arrow);

        tool.HitTestHandle(Screen(tool.PlacedShape!.NormalizedStart)).Should().Be(ShapeManipulationHandle.Start);
        tool.HitTestHandle(Screen(tool.PlacedShape.NormalizedEnd)).Should().Be(ShapeManipulationHandle.End);

        // A bounding-box corner that is not on the line is not a handle for a linear shape.
        tool.HitTestHandle(new SKPoint(200, 200)).Should().Be(ShapeManipulationHandle.None);

        // There is no rotation handle for a linear shape.
        var bboxTopCenter = new SKPoint(500, 200 - 30);
        tool.HitTestHandle(bboxTopCenter).Should().NotBe(ShapeManipulationHandle.Rotate);
    }

    [Fact]
    public void DraggingHeadEndpoint_MovesOnlyHead_AndKeepsTailFixed()
    {
        var tool = CreatePlacedLinear(new SKPoint(200, 800), new SKPoint(800, 200), ShapeType.Arrow);

        // Grab the head (end) and drag it further up-right.
        tool.OnPointerPressed(new SKPoint(800, 200));
        tool.OnPointerMoved(new SKPoint(900, 100));
        tool.OnPointerReleased();

        // Tail unchanged; head followed the drag — direction preserved, no flip.
        tool.PlacedShape!.NormalizedStart.X.Should().BeApproximately(0.2f, 0.001f);
        tool.PlacedShape.NormalizedStart.Y.Should().BeApproximately(0.8f, 0.001f);
        tool.PlacedShape.NormalizedEnd.X.Should().BeApproximately(0.9f, 0.001f);
        tool.PlacedShape.NormalizedEnd.Y.Should().BeApproximately(0.1f, 0.001f);
    }

    [Fact]
    public void DraggingTailEndpoint_MovesOnlyTail_AndKeepsHeadFixed()
    {
        var tool = CreatePlacedLinear(new SKPoint(200, 800), new SKPoint(800, 200), ShapeType.Arrow);

        tool.OnPointerPressed(new SKPoint(200, 800));
        tool.OnPointerMoved(new SKPoint(100, 900));
        tool.OnPointerReleased();

        tool.PlacedShape!.NormalizedEnd.X.Should().BeApproximately(0.8f, 0.001f);
        tool.PlacedShape.NormalizedEnd.Y.Should().BeApproximately(0.2f, 0.001f);
        tool.PlacedShape.NormalizedStart.X.Should().BeApproximately(0.1f, 0.001f);
        tool.PlacedShape.NormalizedStart.Y.Should().BeApproximately(0.9f, 0.001f);
    }

    [Fact]
    public void Arrow_NeverGainsRotation_WhenEndpointsAreDragged()
    {
        var tool = CreatePlacedLinear(new SKPoint(200, 800), new SKPoint(800, 200), ShapeType.Arrow);

        tool.OnPointerPressed(new SKPoint(800, 200));
        tool.OnPointerMoved(new SKPoint(300, 700));
        tool.OnPointerReleased();

        tool.PlacedShape!.RotationDegrees.Should().Be(0f);
    }

    [Fact]
    public void Line_AlsoUsesEndpointHandles()
    {
        var tool = CreatePlacedLinear(new SKPoint(100, 100), new SKPoint(900, 900), ShapeType.Line);

        tool.HitTestHandle(Screen(tool.PlacedShape!.NormalizedStart)).Should().Be(ShapeManipulationHandle.Start);
        tool.HitTestHandle(Screen(tool.PlacedShape.NormalizedEnd)).Should().Be(ShapeManipulationHandle.End);
    }

    [Fact]
    public void Rectangle_StillUsesCornerHandles()
    {
        var tool = new ShapeTool { IsActive = true, ShapeType = ShapeType.Rectangle, ImagePixelWidth = Size };
        tool.SetImageBounds(new SKRect(0, 0, Size, Size));
        tool.OnPointerPressed(new SKPoint(200, 200));
        tool.OnPointerMoved(new SKPoint(800, 600));
        tool.OnPointerReleased();

        // Box shapes keep their corner handles and never expose endpoint handles.
        tool.HitTestHandle(new SKPoint(200, 200)).Should().Be(ShapeManipulationHandle.TopLeft);
        tool.HitTestHandle(new SKPoint(800, 600)).Should().Be(ShapeManipulationHandle.BottomRight);
    }
}
