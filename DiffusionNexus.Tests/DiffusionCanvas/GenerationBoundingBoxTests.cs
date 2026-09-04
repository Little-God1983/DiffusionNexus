using Avalonia;
using DiffusionNexus.UI.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The bounding box is the canvas's whole spatial model, so its move/resize contract is pinned here.
/// The invariant that matters most: resizing pins the edge opposite the handle being dragged.
/// </summary>
public class GenerationBoundingBoxTests
{
    private static GenerationBoundingBox At(double x, double y, int w = 1024, int h = 1024)
    {
        var box = new GenerationBoundingBox();
        box.SetSize(w, h);
        box.SetPosition(x, y);
        return box;
    }

    [Fact]
    public void DefaultsToASquareLatentOnTheLattice()
    {
        var box = new GenerationBoundingBox();

        box.Width.Should().Be(1024);
        box.Height.Should().Be(1024);
        box.Alignment.Should().Be(GenerationBoundingBox.DefaultAlignment);
        box.SnapToGrid.Should().BeTrue();
    }

    [Fact]
    public void Move_DragsTheWholeBoxWithoutResizingIt()
    {
        var box = At(0, 0);
        box.BeginDrag(BoxHandle.Move, new Point(512, 512));

        box.DragTo(new Point(512 + 256, 512 - 128));
        box.EndDrag();

        box.X.Should().Be(256);
        box.Y.Should().Be(-128);
        box.Width.Should().Be(1024);
        box.Height.Should().Be(1024);
    }

    [Fact]
    public void Move_SnapsThePositionToTheLattice()
    {
        var box = At(0, 0);
        box.BeginDrag(BoxHandle.Move, new Point(0, 0));

        box.DragTo(new Point(100, 100));

        box.X.Should().Be(128, "100 rounds to the nearest multiple of 64");
        box.Y.Should().Be(128);
    }

    [Fact]
    public void Move_WithSnapDisabledLandsExactly()
    {
        var box = At(0, 0);
        box.SnapToGrid = false;
        box.BeginDrag(BoxHandle.Move, new Point(0, 0));

        box.DragTo(new Point(100, 37));

        box.X.Should().Be(100);
        box.Y.Should().Be(37);
    }

    [Theory]
    [InlineData(BoxHandle.SouthEast)]
    [InlineData(BoxHandle.East)]
    [InlineData(BoxHandle.South)]
    public void ResizingFromTheSouthOrEastPinsTheTopLeftCorner(BoxHandle handle)
    {
        var box = At(320, 640);
        box.BeginDrag(handle, box.GetHandleCenter(handle));

        box.DragTo(box.GetHandleCenter(handle) + new Point(256, 256));
        box.EndDrag();

        box.X.Should().Be(320);
        box.Y.Should().Be(640);
    }

    [Fact]
    public void ResizingFromTheNorthWestPinsTheBottomRightCorner()
    {
        var box = At(1024, 1024);
        var bottomRightBefore = new Point(box.X + box.Width, box.Y + box.Height);
        box.BeginDrag(BoxHandle.NorthWest, box.GetHandleCenter(BoxHandle.NorthWest));

        box.DragTo(box.GetHandleCenter(BoxHandle.NorthWest) - new Point(256, 512));
        box.EndDrag();

        box.Width.Should().Be(1280);
        box.Height.Should().Be(1536);
        (box.X + box.Width).Should().Be(bottomRightBefore.X);
        (box.Y + box.Height).Should().Be(bottomRightBefore.Y);
    }

    [Fact]
    public void ResizingFromTheNorthEastPinsTheBottomLeftCorner()
    {
        var box = At(500, 500, 1024, 1024);
        box.SnapToGrid = false;
        var bottomLeftBefore = new Point(box.X, box.Y + box.Height);
        box.BeginDrag(BoxHandle.NorthEast, box.GetHandleCenter(BoxHandle.NorthEast));

        box.DragTo(box.GetHandleCenter(BoxHandle.NorthEast) + new Point(100, -200));
        box.EndDrag();

        box.Width.Should().Be(1124);
        box.Height.Should().Be(1224);
        box.X.Should().Be(bottomLeftBefore.X);
        (box.Y + box.Height).Should().Be(bottomLeftBefore.Y);
    }

    [Fact]
    public void ResizeSnapsSizesToTheLattice()
    {
        var box = At(0, 0, 1024, 1024);
        box.BeginDrag(BoxHandle.SouthEast, new Point(1024, 1024));

        box.DragTo(new Point(1024 + 100, 1024 + 30));

        box.Width.Should().Be(1152, "1124 rounds up to the nearest multiple of 64");
        box.Height.Should().Be(1024, "1054 rounds back down to 1024");
    }

    [Fact]
    public void ResizeClampsToTheGeneratableRange()
    {
        var box = At(0, 0, 1024, 1024);
        box.BeginDrag(BoxHandle.SouthEast, new Point(1024, 1024));

        box.DragTo(new Point(-100_000, 100_000));

        box.Width.Should().Be(GenerationBoundingBox.MinSize);
        box.Height.Should().Be(GenerationBoundingBox.MaxSize);
    }

    [Fact]
    public void DragIsComputedFromTheGestureOriginSoSnappingCannotDrift()
    {
        var box = At(0, 0, 1024, 1024);
        box.BeginDrag(BoxHandle.SouthEast, new Point(1024, 1024));

        // Ten small steps that each round back to zero must not accumulate into a change.
        for (var i = 1; i <= 10; i++)
            box.DragTo(new Point(1024 + i, 1024 + i));

        box.Width.Should().Be(1024);
        box.Height.Should().Be(1024);
    }

    [Fact]
    public void HitTest_PrefersHandlesOverTheBody()
    {
        var box = At(0, 0, 1024, 1024);

        box.HitTest(new Point(0, 0), 16).Should().Be(BoxHandle.NorthWest);
        box.HitTest(new Point(1024, 1024), 16).Should().Be(BoxHandle.SouthEast);
        box.HitTest(new Point(512, 0), 16).Should().Be(BoxHandle.North);
        box.HitTest(new Point(1024, 512), 16).Should().Be(BoxHandle.East);
        box.HitTest(new Point(512, 512), 16).Should().Be(BoxHandle.Move);
        box.HitTest(new Point(-400, -400), 16).Should().Be(BoxHandle.None);
    }

    [Fact]
    public void HitTest_RadiusIsInWorldUnitsSoHandlesStayGrabbableWhenZoomedOut()
    {
        var box = At(0, 0, 1024, 1024);

        // 40 world units away from the corner: a miss at a 16-unit radius, a hit at 64.
        box.HitTest(new Point(40, 40), 16).Should().Be(BoxHandle.Move);
        box.HitTest(new Point(40, 40), 64).Should().Be(BoxHandle.NorthWest);
    }

    [Fact]
    public void CancelDrag_RestoresTheBoxToWhereTheGestureStarted()
    {
        var box = At(256, 256, 1024, 1024);
        box.BeginDrag(BoxHandle.SouthEast, new Point(1280, 1280));
        box.DragTo(new Point(1900, 1900));
        box.Width.Should().NotBe(1024);

        box.CancelDrag();

        box.X.Should().Be(256);
        box.Y.Should().Be(256);
        box.Width.Should().Be(1024);
        box.Height.Should().Be(1024);
        box.IsDragging.Should().BeFalse();
    }

    [Fact]
    public void DragTo_DoesNothingWithoutBeginDrag()
    {
        var box = At(0, 0, 1024, 1024);

        box.DragTo(new Point(5000, 5000));

        box.WorldRect.Should().Be(new Rect(0, 0, 1024, 1024));
    }

    [Fact]
    public void ChangingTheAlignmentReSnapsTheCurrentSize()
    {
        var box = At(0, 0, 1024, 1024);
        box.SnapToGrid = false;
        box.SetSize(1000, 1000);

        box.SnapToGrid = true;
        box.Alignment = 128;

        box.Width.Should().Be(1024);
        box.Height.Should().Be(1024);
    }

    [Fact]
    public void CenterOn_PlacesTheBoxAroundAWorldPoint()
    {
        var box = At(0, 0, 1024, 512);

        box.CenterOn(new Point(2048, 1024));

        box.X.Should().Be(2048 - 512);
        box.Y.Should().Be(1024 - 256);
    }

    [Fact]
    public void Changed_FiresOnMoveAndResizeButNotOnANoOp()
    {
        var box = At(0, 0, 1024, 1024);
        var raised = 0;
        box.Changed += (_, _) => raised++;

        box.SetPosition(0, 0);
        box.SetSize(1024, 1024);
        raised.Should().Be(0);

        box.SetPosition(64, 64);
        box.SetSize(512, 512);
        raised.Should().Be(2);
    }
}
