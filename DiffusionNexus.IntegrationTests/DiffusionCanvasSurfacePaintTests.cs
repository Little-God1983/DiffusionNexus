using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using DiffusionNexus.UI.Views.Controls;
using FluentAssertions;

namespace DiffusionNexus.IntegrationTests;

/// <summary>
/// The canvas surface's paint gesture (#595): one drag is one stroke on the mask, and while a paint tool
/// is active a left drag never moves the box. Events are raised on the control directly, as in
/// <c>ScrollKeyNavigationTests</c>: hit-testing is dead in this suite, routing and capture are real.
/// </summary>
public class DiffusionCanvasSurfacePaintTests
{
    private static (Window Window, DiffusionCanvasSurface Surface, GenerationBoundingBox Box, InpaintMaskLayerViewModel Mask) Host(
        CanvasPaintTool tool)
    {
        var box = new GenerationBoundingBox();
        box.SetSize(256, 256);
        box.SetPosition(0, 0);
        var mask = new InpaintMaskLayerViewModel();
        var surface = new DiffusionCanvasSurface { Box = box, Mask = mask, PaintTool = tool, BrushSize = 40 };
        var window = new Window { Width = 600, Height = 600, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, surface, box, mask);
    }

    private static readonly Pointer Mouse = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    private static void Press(DiffusionCanvasSurface surface, Window window, Point at, RawInputModifiers button = RawInputModifiers.LeftMouseButton) =>
        surface.RaiseEvent(new PointerPressedEventArgs(
            surface, Mouse, window, at, timestamp: 0,
            new PointerPointProperties(button, button == RawInputModifiers.LeftMouseButton
                ? PointerUpdateKind.LeftButtonPressed
                : PointerUpdateKind.RightButtonPressed),
            KeyModifiers.None));

    private static void Move(DiffusionCanvasSurface surface, Window window, Point to) =>
        surface.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, surface, Mouse, window, to, timestamp: 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

    private static void Release(DiffusionCanvasSurface surface, Window window, Point at) =>
        surface.RaiseEvent(new PointerReleasedEventArgs(
            surface, Mouse, window, at, timestamp: 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

    private static void Drag(DiffusionCanvasSurface surface, Window window, params Point[] path)
    {
        Press(surface, window, path[0]);
        foreach (var point in path.Skip(1))
            Move(surface, window, point);
        Release(surface, window, path[^1]);
    }

    [AvaloniaFact]
    public void WithoutAPaintToolADragInsideTheBoxMovesIt()
    {
        // The control for the next test: the same drag does move the box when no tool is active.
        var (window, surface, box, mask) = Host(CanvasPaintTool.None);
        try
        {
            Drag(surface, window, new Point(128, 128), new Point(178, 128), new Point(228, 148));

            box.X.Should().NotBe(0);
            mask.Strokes.Should().BeEmpty();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ABrushDragIsOneStrokeAndLeavesTheBoxWhereItIs()
    {
        var (window, surface, box, mask) = Host(CanvasPaintTool.Brush);
        try
        {
            Drag(surface, window, new Point(128, 128), new Point(178, 128), new Point(228, 148));

            box.X.Should().Be(0);
            box.Y.Should().Be(0);
            var stroke = mask.Strokes.Should().ContainSingle().Subject;
            stroke.IsErase.Should().BeFalse();
            stroke.Size.Should().Be(40);
            stroke.Points.Should().HaveCount(3);
            stroke.Points[0].Should().Be(new Point(128, 128), "the viewport starts at 1:1 with no pan");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AnEraserDragIsAnEraserStroke()
    {
        var (window, surface, _, mask) = Host(CanvasPaintTool.Eraser);
        try
        {
            Drag(surface, window, new Point(50, 50), new Point(90, 50));

            mask.Strokes.Should().ContainSingle().Which.IsErase.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickWithoutMovingPaintsADot()
    {
        var (window, surface, _, mask) = Host(CanvasPaintTool.Brush);
        try
        {
            Drag(surface, window, new Point(60, 60));

            mask.Strokes.Should().ContainSingle().Which.Points.Should().ContainSingle();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PointsCloserThanAQuarterOfTheBrushAreSkipped()
    {
        var (window, surface, _, mask) = Host(CanvasPaintTool.Brush);
        try
        {
            // Brush 40: anything under 10 world px from the last kept point is dropped.
            Drag(surface, window, new Point(50, 50), new Point(53, 50), new Point(56, 50), new Point(70, 50));

            mask.Strokes.Single().Points.Should().Equal(new Point(50, 50), new Point(70, 50));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeMidStrokeTakesTheStrokeBack()
    {
        var (window, surface, _, mask) = Host(CanvasPaintTool.Brush);
        try
        {
            Press(surface, window, new Point(50, 50));
            Move(surface, window, new Point(100, 50));
            surface.CancelActiveGesture();
            Release(surface, window, new Point(100, 50));

            mask.Strokes.Should().BeEmpty();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PuttingTheToolDownMidStrokeDropsTheStroke()
    {
        var (window, surface, _, mask) = Host(CanvasPaintTool.Brush);
        try
        {
            Press(surface, window, new Point(50, 50));
            Move(surface, window, new Point(100, 50));
            surface.PaintTool = CanvasPaintTool.None;
            Release(surface, window, new Point(100, 50));

            mask.Strokes.Should().BeEmpty();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AHiddenMaskIsNotPaintedAndTheBoxStillDoesNotMove()
    {
        var (window, surface, box, mask) = Host(CanvasPaintTool.Brush);
        try
        {
            mask.IsVisible = false;

            Drag(surface, window, new Point(128, 128), new Point(200, 128));

            mask.Strokes.Should().BeEmpty();
            box.X.Should().Be(0, "a paint tool owns the left button even when it cannot paint");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShiftWheelResizesTheBrushWhileAToolIsActive()
    {
        var (window, surface, _, _) = Host(CanvasPaintTool.Brush);
        try
        {
            var zoom = surface.Viewport.Zoom;

            surface.RaiseEvent(new PointerWheelEventArgs(
                surface, Mouse, window, new Point(100, 100), timestamp: 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.Shift, new Vector(0, 1)));

            surface.BrushSize.Should().Be(50);
            surface.Viewport.Zoom.Should().Be(zoom, "Shift+wheel resizes the brush instead of zooming");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShiftWheelStopsAtTheLargestBrush()
    {
        // The host clamps too, but at the limit its value does not change, so it raises nothing and a
        // two-way binding would leave the surface holding 640 while every label says 512.
        var (window, surface, _, _) = Host(CanvasPaintTool.Brush);
        try
        {
            surface.BrushSize = CanvasBrush.MaxSize;

            surface.RaiseEvent(new PointerWheelEventArgs(
                surface, Mouse, window, new Point(100, 100), timestamp: 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.Shift, new Vector(0, 1)));

            surface.BrushSize.Should().Be(CanvasBrush.MaxSize);
        }
        finally
        {
            window.Close();
        }
    }
}
