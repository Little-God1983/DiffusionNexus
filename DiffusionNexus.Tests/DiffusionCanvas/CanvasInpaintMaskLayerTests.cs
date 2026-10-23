using System.Collections.ObjectModel;
using Avalonia;
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>The inpaint mask layer (#595) and how the layer stack holds it.</summary>
public class CanvasInpaintMaskLayerTests
{
    private readonly ObservableCollection<GenerationFrameViewModel> _frames = [];
    private readonly List<string> _trace = [];

    private CanvasLayerStackViewModel Create() => new(_frames, _trace.Add);

    private static GenerationFrameViewModel Frame(string name) =>
        new() { Name = name, Width = 64, Height = 64, State = GenerationFrameState.Completed };

    private static CanvasMaskStroke Dot(double x, double y, double size = 10, bool erase = false) =>
        new([new Point(x, y)], size, erase);

    // ────────────────────────────── The mask layer ──────────────────────────────

    [Fact]
    public void ANewMaskIsAVisibleEmptyInpaintMaskLayer()
    {
        var mask = new InpaintMaskLayerViewModel();

        mask.Kind.Should().Be(CanvasLayerKind.InpaintMask);
        mask.Name.Should().Be("Inpaint mask");
        mask.IsVisible.Should().BeTrue();
        mask.HasStrokes.Should().BeFalse();
        mask.PaintedBounds.Should().BeNull();
        mask.OpacityText.Should().BeEmpty("a mask has no opacity");
        mask.Thumbnail.Should().BeNull();
        mask.Denoise.Should().Be(InpaintMaskLayerViewModel.DefaultDenoise);
    }

    [Fact]
    public void AddingAStrokeBumpsTheRevisionAndGrowsThePaintedBounds()
    {
        var mask = new InpaintMaskLayerViewModel();

        mask.AddStroke(Dot(10, 10));
        mask.AddStroke(Dot(100, 50));

        mask.Revision.Should().Be(2);
        mask.Strokes.Should().HaveCount(2);
        mask.PaintedBounds.Should().Be(new Rect(5, 5, 100, 50));
    }

    [Fact]
    public void AnEraserStrokeDoesNotGrowThePaintedBounds()
    {
        var mask = new InpaintMaskLayerViewModel();
        mask.AddStroke(Dot(10, 10));

        mask.AddStroke(Dot(500, 500, erase: true));

        mask.PaintedBounds.Should().Be(new Rect(5, 5, 10, 10));
        mask.HasStrokes.Should().BeTrue();
    }

    [Fact]
    public void StrokesAreReplacedNotMutated()
    {
        // A snapshot handed to the render thread or a background rasterise must not change under it.
        var mask = new InpaintMaskLayerViewModel();
        mask.AddStroke(Dot(10, 10));
        var snapshot = mask.Strokes;

        mask.AddStroke(Dot(20, 20));

        snapshot.Should().HaveCount(1);
        mask.Strokes.Should().NotBeSameAs(snapshot);
    }

    [Fact]
    public void ClearStrokesEmptiesTheMaskAndIsDisabledWhenEmpty()
    {
        var trace = new List<string>();
        var mask = new InpaintMaskLayerViewModel(trace.Add);
        mask.ClearStrokesCommand.CanExecute(null).Should().BeFalse();
        mask.AddStroke(Dot(10, 10));
        mask.ClearStrokesCommand.CanExecute(null).Should().BeTrue();

        mask.ClearStrokesCommand.Execute(null);

        mask.HasStrokes.Should().BeFalse();
        mask.PaintedBounds.Should().BeNull();
        mask.ClearStrokesCommand.CanExecute(null).Should().BeFalse();
        trace.Should().Contain(line => line.StartsWith("Cleared the mask", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryStrokeIsTraced()
    {
        var trace = new List<string>();
        var mask = new InpaintMaskLayerViewModel(trace.Add);

        mask.AddStroke(Dot(10, 10, size: 32));
        mask.AddStroke(Dot(10, 10, size: 16, erase: true));

        trace.Should().HaveCount(2);
        trace[0].Should().StartWith("Painted a 32 px stroke");
        trace[1].Should().StartWith("Erased a 16 px stroke");
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(12.4, 12)]
    [InlineData(500, 64)]
    [InlineData(double.NaN, 0)]
    public void FeatherIsClampedAndWhole(double value, double expected)
    {
        var mask = new InpaintMaskLayerViewModel { Feather = value };

        mask.Feather.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0.05)]
    [InlineData(0.333, 0.33)]
    [InlineData(2, 1.0)]
    [InlineData(double.NaN, 0.75)]
    public void DenoiseIsClampedToARunnableRange(double value, double expected)
    {
        var mask = new InpaintMaskLayerViewModel { Denoise = value };

        mask.Denoise.Should().Be(expected);
    }

    [Fact]
    public void TextsAreInvariant()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var mask = new InpaintMaskLayerViewModel { Denoise = 0.5, Feather = 8 };

            mask.DenoiseText.Should().Be("0.50");
            mask.FeatherText.Should().Be("8 px");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void InvertIsTraced()
    {
        var trace = new List<string>();
        var mask = new InpaintMaskLayerViewModel(trace.Add);

        mask.Invert = true;

        trace.Should().ContainSingle().Which.Should().Contain("inverted");
    }

    // ────────────────────────────── In the stack ──────────────────────────────

    [Fact]
    public void AddMaskPutsItOnTopAndSelectsIt()
    {
        _frames.Add(Frame("a"));
        var stack = Create();

        var mask = stack.AddMask();

        stack.Mask.Should().BeSameAs(mask);
        stack.DisplayLayers[0].Should().BeSameAs(mask);
        stack.SelectedLayer.Should().BeSameAs(mask);
        stack.SelectedMask.Should().BeSameAs(mask);
        stack.SelectedRaster.Should().BeNull();
        _trace.Should().Contain(line => line.StartsWith("Added the mask", StringComparison.Ordinal));
    }

    [Fact]
    public void AddMaskTwiceSelectsTheOneMask()
    {
        var stack = Create();
        var first = stack.AddMask();
        var frame = Frame("a");
        stack.AddAccepted(frame);

        var second = stack.AddMask();

        second.Should().BeSameAs(first);
        stack.DisplayLayers.OfType<InpaintMaskLayerViewModel>().Should().ContainSingle();
        stack.SelectedLayer.Should().BeSameAs(first);
    }

    [Fact]
    public void TheMaskStaysAboveEveryRaster()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        var mask = stack.AddMask();
        var b = Frame("b");
        var c = Frame("c");

        stack.AddAccepted(b);
        _frames.Add(c);
        stack.DisplayLayers.Should().Equal(mask, c, b, a);

        _frames.Move(0, 2);                      // a to the top of the rasters
        stack.DisplayLayers.Should().Equal(mask, a, c, b);

        _frames.Remove(c);
        stack.DisplayLayers.Should().Equal(mask, a, b);

        _frames.Clear();
        stack.DisplayLayers.Should().Equal(mask);
        stack.HasLayers.Should().BeTrue("the mask is still a layer");
    }

    [Fact]
    public void UpAndDownDoNotApplyToTheMaskAndTheTopRasterCannotRiseAboveIt()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        stack.AddAccepted(a);
        stack.AddAccepted(b);
        var mask = stack.AddMask();

        stack.MoveUpCommand.CanExecute(null).Should().BeFalse();
        stack.MoveDownCommand.CanExecute(null).Should().BeFalse();

        stack.SelectedLayer = b;
        stack.MoveUpCommand.CanExecute(null).Should().BeFalse("b is the top raster; the mask row is above it");
        stack.MoveDownCommand.CanExecute(null).Should().BeTrue();

        stack.MoveDownCommand.Execute(null);
        stack.DisplayLayers.Should().Equal(mask, a, b);
    }

    [Fact]
    public void DeletingTheMaskSelectsTheRowThatTookItsPlace()
    {
        var stack = Create();
        var a = Frame("a");
        stack.AddAccepted(a);
        var mask = stack.AddMask();
        mask.AddStroke(Dot(1, 1));

        stack.DeleteSelectedCommand.Execute(null);

        stack.Mask.Should().BeNull();
        stack.DisplayLayers.Should().Equal(a);
        stack.SelectedLayer.Should().BeSameAs(a);
        _trace.Should().Contain("Deleted the mask 'Inpaint mask' (1 stroke(s)).");
    }

    [Fact]
    public void ALockedMaskCannotBeDeleted()
    {
        var stack = Create();
        var mask = stack.AddMask();
        mask.IsLocked = true;

        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
        stack.DeleteMask().Should().BeFalse();

        stack.Mask.Should().BeSameAs(mask);
        _trace.Should().Contain(line => line.StartsWith("Refused to delete the mask", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearUnlockedRemovesAnUnlockedMaskWithTheRasters()
    {
        var stack = Create();
        stack.AddAccepted(Frame("a"));
        stack.AddMask();

        var (removed, kept) = stack.ClearUnlocked();

        (removed, kept).Should().Be((2, 0));
        stack.Mask.Should().BeNull();
        stack.HasLayers.Should().BeFalse();
        stack.SelectedLayer.Should().BeNull();
    }

    [Fact]
    public void ClearUnlockedKeepsALockedMask()
    {
        var stack = Create();
        stack.AddAccepted(Frame("a"));
        var mask = stack.AddMask();
        mask.IsLocked = true;

        var (removed, kept) = stack.ClearUnlocked();

        (removed, kept).Should().Be((1, 1));
        stack.DisplayLayers.Should().Equal(mask);
        stack.SelectedLayer.Should().BeSameAs(mask);
        stack.HasUnlockedLayers.Should().BeFalse();
    }

    [Fact]
    public void AnUnlockedMaskAloneStillCountsForClearCanvas()
    {
        var stack = Create();
        stack.AddMask();

        stack.HasUnlockedLayers.Should().BeTrue();
    }

    [Fact]
    public void TheMasksStrokesInvertFeatherAndVisibilityRaiseLayersChanged()
    {
        var stack = Create();
        var mask = stack.AddMask();
        var raised = 0;
        stack.LayersChanged += (_, _) => raised++;

        mask.AddStroke(Dot(1, 1));
        mask.Invert = true;
        mask.Feather = 8;
        mask.IsVisible = false;
        mask.Denoise = 0.5;   // only changes the run's strength, not what the readout says

        raised.Should().Be(4);
    }

    [Fact]
    public void TheMasksVisibilityAndLockAreTracedAsAMask()
    {
        var stack = Create();
        var mask = stack.AddMask();
        _trace.Clear();

        mask.IsVisible = false;
        mask.IsLocked = true;
        mask.Name = "Sky";

        _trace.Should().Equal("Mask 'Inpaint mask' hidden.", "Mask 'Inpaint mask' locked.", "Renamed a mask to 'Sky'.");
    }

    [Fact]
    public void ADeletedMaskIsNoLongerObserved()
    {
        var stack = Create();
        var mask = stack.AddMask();
        stack.DeleteMask();
        var raised = 0;
        stack.LayersChanged += (_, _) => raised++;

        mask.AddStroke(Dot(1, 1));

        raised.Should().Be(0);
    }
}
