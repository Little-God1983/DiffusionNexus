using System.Collections.ObjectModel;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>The layer stack's rules (#594): order, selection, lock, and the top-first mirror.</summary>
public class CanvasLayerStackViewModelTests
{
    private readonly ObservableCollection<GenerationFrameViewModel> _frames = [];
    private readonly List<string> _trace = [];

    private CanvasLayerStackViewModel Create() => new(_frames, _trace.Add);

    private static GenerationFrameViewModel Frame(string name) =>
        new() { Name = name, Width = 64, Height = 64, State = GenerationFrameState.Completed };

    [Fact]
    public void DisplayLayers_ListsTheTopLayerFirst_IncludingFramesThatPredateTheStack()
    {
        var a = Frame("a");
        _frames.Add(a);
        var stack = Create();
        var b = Frame("b");
        var c = Frame("c");
        _frames.Add(b);
        _frames.Add(c);

        stack.DisplayLayers.Should().Equal(c, b, a);
        stack.HasLayers.Should().BeTrue();
    }

    [Fact]
    public void DisplayLayers_FollowsInsertRemoveMoveAndReset()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        var c = Frame("c");
        _frames.Add(a);
        _frames.Add(c);
        _frames.Insert(1, b);
        stack.DisplayLayers.Should().Equal(c, b, a);

        _frames.Move(0, 2);                      // a to the top
        stack.DisplayLayers.Should().Equal(a, c, b);

        _frames.Remove(c);
        stack.DisplayLayers.Should().Equal(a, b);

        _frames.Clear();
        stack.DisplayLayers.Should().BeEmpty();
        stack.HasLayers.Should().BeFalse();
    }

    [Fact]
    public void AddAccepted_LandsOnTopEvenWhenALowerLayerIsSelected()
    {
        var stack = Create();
        var lower = Frame("");
        stack.AddAccepted(lower);
        stack.AddAccepted(Frame(""));
        stack.SelectedLayer = lower;

        var accepted = Frame("");
        stack.AddAccepted(accepted);

        _frames[^1].Should().BeSameAs(accepted);
        stack.DisplayLayers[0].Should().BeSameAs(accepted);
        stack.SelectedLayer.Should().BeSameAs(accepted);
        accepted.Name.Should().Be("Layer 3");
    }

    [Fact]
    public void AddAccepted_NeverReusesANumber()
    {
        var stack = Create();
        var first = Frame("");
        stack.AddAccepted(first);
        stack.Delete(first);

        var second = Frame("");
        stack.AddAccepted(second);

        second.Name.Should().Be("Layer 2", "a reused number would make two different layers look the same in the log");
    }

    [Fact]
    public void MoveUp_RaisesTheSelectedLayerOneStepAndStopsAtTheTop()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        _frames.Add(a);
        _frames.Add(b);
        stack.SelectedLayer = a;

        stack.MoveUpCommand.CanExecute(null).Should().BeTrue();
        stack.MoveUpCommand.Execute(null);

        stack.DisplayLayers.Should().Equal(a, b);
        stack.SelectedLayer.Should().BeSameAs(a);
        stack.MoveUpCommand.CanExecute(null).Should().BeFalse("it is already the top layer");
        _trace.Should().Contain(t => t.Contains("Moved layer 'a' up"));
    }

    [Fact]
    public void MoveDown_LowersTheSelectedLayerOneStepAndStopsAtTheBottom()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        _frames.Add(a);
        _frames.Add(b);
        stack.SelectedLayer = b;

        stack.MoveDownCommand.Execute(null);

        stack.DisplayLayers.Should().Equal(a, b);
        stack.MoveDownCommand.CanExecute(null).Should().BeFalse("it is already the bottom layer");
    }

    [Fact]
    public void MoveCommands_AreDisabledWithNoSelection()
    {
        var stack = Create();
        _frames.Add(Frame("a"));
        _frames.Add(Frame("b"));

        stack.MoveUpCommand.CanExecute(null).Should().BeFalse();
        stack.MoveDownCommand.CanExecute(null).Should().BeFalse();
        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Delete_RefusesALockedLayer()
    {
        var stack = Create();
        var locked = Frame("keeper");
        _frames.Add(locked);
        locked.IsLocked = true;
        stack.SelectedLayer = locked;

        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
        CanvasLayerStackViewModel.CanDelete(locked).Should().BeFalse();
        stack.Delete(locked).Should().BeFalse();

        _frames.Should().Contain(locked);
        _trace.Should().Contain(t => t.StartsWith("Refused to delete layer 'keeper'"));
    }

    [Fact]
    public void LockingTheSelectedLayer_DisablesDeleteImmediately()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        stack.SelectedLayer = a;
        var notified = false;
        stack.DeleteSelectedCommand.CanExecuteChanged += (_, _) => notified = true;

        a.IsLocked = true;

        notified.Should().BeTrue();
        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void DeleteSelected_SelectsTheLayerThatTookItsPlace()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        var c = Frame("c");
        _frames.Add(a);
        _frames.Add(b);
        _frames.Add(c);                           // display: c, b, a

        stack.SelectedLayer = b;
        stack.DeleteSelectedCommand.Execute(null);
        stack.SelectedLayer.Should().BeSameAs(a, "a moved up into b's row");

        stack.DeleteSelectedCommand.Execute(null);   // a was the bottom row
        stack.SelectedLayer.Should().BeSameAs(c, "with nothing below, the row above is selected");

        stack.DeleteSelectedCommand.Execute(null);
        stack.SelectedLayer.Should().BeNull();
    }

    [Fact]
    public void Delete_OfAnUnselectedLayerKeepsTheSelection()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        _frames.Add(a);
        _frames.Add(b);
        stack.SelectedLayer = b;

        stack.Delete(a).Should().BeTrue();

        stack.SelectedLayer.Should().BeSameAs(b);
    }

    [Fact]
    public void ClearUnlocked_KeepsLockedLayersAndReportsCounts()
    {
        var stack = Create();
        var keep = Frame("keep");
        _frames.Add(Frame("x"));
        _frames.Add(keep);
        _frames.Add(Frame("y"));
        keep.IsLocked = true;

        var (removed, kept) = stack.ClearUnlocked();

        removed.Should().Be(2);
        kept.Should().Be(1);
        _frames.Should().Equal(keep);
        stack.SelectedLayer.Should().BeSameAs(keep);
        stack.HasUnlockedLayers.Should().BeFalse();
    }

    [Fact]
    public void HasUnlockedLayers_FollowsLockChanges()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        var raised = false;
        stack.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(CanvasLayerStackViewModel.HasUnlockedLayers);

        a.IsLocked = true;

        raised.Should().BeTrue();
        stack.HasUnlockedLayers.Should().BeFalse();
    }

    [Fact]
    public void LayersChanged_IsRaisedForWhatTheModelSees_NotForARename()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        var count = 0;
        stack.LayersChanged += (_, _) => count++;

        a.IsVisible = false;
        a.Opacity = 0.5;
        count.Should().Be(2);

        a.Name = "renamed";
        count.Should().Be(2);
        _trace.Should().Contain("Layer 'a' hidden.");
        _trace.Should().Contain("Renamed a layer to 'renamed'.");
    }

    [Fact]
    public void ARemovedLayerIsNoLongerObserved()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        _frames.Remove(a);
        var count = 0;
        stack.LayersChanged += (_, _) => count++;

        a.IsVisible = false;

        count.Should().Be(0);
    }
}
