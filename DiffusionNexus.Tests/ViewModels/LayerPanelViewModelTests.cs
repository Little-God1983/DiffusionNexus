using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="LayerPanelViewModel"/>.
/// Tests property state, commands, and event raising.
/// </summary>
public class LayerPanelViewModelTests
{
    private readonly LayerPanelViewModel _sut;

    public LayerPanelViewModelTests()
    {
        _sut = new LayerPanelViewModel(hasImage: () => true);
    }

    #region Constructor

    [Fact]
    public void WhenCreated_DefaultStateIsCorrect()
    {
        _sut.IsLayerMode.Should().BeFalse();
        _sut.SelectedLayer.Should().BeNull();
        _sut.Layers.Should().BeEmpty();
    }

    [Fact]
    public void WhenCreatedWithNullHasImage_ThrowsArgumentNullException()
    {
        var act = () => new LayerPanelViewModel(hasImage: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region IsLayerMode

    [Fact]
    public void WhenIsLayerModeSet_PropertyChangedIsRaised()
    {
        var raised = false;
        _sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LayerPanelViewModel.IsLayerMode))
                raised = true;
        };

        _sut.IsLayerMode = true;

        raised.Should().BeTrue();
        _sut.IsLayerMode.Should().BeTrue();
    }

    #endregion

    #region SelectedLayer

    [Fact]
    public void WhenSelectedLayerSet_LayerSelectionChangedEventIsRaised()
    {
        Layer? receivedLayer = null;
        _sut.LayerSelectionChanged += (_, layer) => receivedLayer = layer;

        var layer = new Layer(10, 10, "Test");
        var vm = new LayerViewModel(layer, _ => { }, _ => { });
        _sut.Layers.Add(vm);
        _sut.SelectedLayer = vm;

        receivedLayer.Should().Be(layer);
    }

    [Fact]
    public void WhenSelectedLayerSet_OtherLayersAreDeselected()
    {
        var layer1 = new Layer(10, 10, "Layer 1");
        var layer2 = new Layer(10, 10, "Layer 2");
        var vm1 = new LayerViewModel(layer1, _ => { }, _ => { });
        var vm2 = new LayerViewModel(layer2, _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);

        _sut.SelectedLayer = vm1;
        vm1.IsSelected.Should().BeTrue();
        vm2.IsSelected.Should().BeFalse();

        _sut.SelectedLayer = vm2;
        vm1.IsSelected.Should().BeFalse();
        vm2.IsSelected.Should().BeTrue();
    }

    #endregion

    #region CanMoveLayerUp / CanMoveLayerDown / CanMergeDown

    [Fact]
    public void WhenNoLayerSelected_CanMoveUpIsFalse()
    {
        _sut.CanMoveLayerUp.Should().BeFalse();
    }

    [Fact]
    public void WhenFirstLayerSelected_CanMoveUpIsFalse()
    {
        var vm1 = new LayerViewModel(new Layer(10, 10, "L1"), _ => { }, _ => { });
        var vm2 = new LayerViewModel(new Layer(10, 10, "L2"), _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);
        _sut.SelectedLayer = vm1;

        _sut.CanMoveLayerUp.Should().BeFalse();
    }

    [Fact]
    public void WhenSecondLayerSelected_CanMoveUpIsTrue()
    {
        var vm1 = new LayerViewModel(new Layer(10, 10, "L1"), _ => { }, _ => { });
        var vm2 = new LayerViewModel(new Layer(10, 10, "L2"), _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);
        _sut.SelectedLayer = vm2;

        _sut.CanMoveLayerUp.Should().BeTrue();
    }

    [Fact]
    public void WhenLastLayerSelected_CanMoveDownIsFalse()
    {
        var vm1 = new LayerViewModel(new Layer(10, 10, "L1"), _ => { }, _ => { });
        var vm2 = new LayerViewModel(new Layer(10, 10, "L2"), _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);
        _sut.SelectedLayer = vm2;

        _sut.CanMoveLayerDown.Should().BeFalse();
    }

    [Fact]
    public void WhenFirstLayerSelected_CanMoveDownIsTrue()
    {
        var vm1 = new LayerViewModel(new Layer(10, 10, "L1"), _ => { }, _ => { });
        var vm2 = new LayerViewModel(new Layer(10, 10, "L2"), _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);
        _sut.SelectedLayer = vm1;

        _sut.CanMoveLayerDown.Should().BeTrue();
    }

    #endregion

    #region Commands Raise Events

    [Fact]
    public void WhenToggleLayerModeExecuted_EnableLayerModeRequestedIsRaised()
    {
        bool? received = null;
        _sut.EnableLayerModeRequested += (_, enable) => received = enable;

        _sut.ToggleLayerModeCommand.Execute(null);

        received.Should().BeTrue();
        _sut.IsLayerMode.Should().BeTrue();
    }

    [Fact]
    public void WhenAddLayerExecuted_AddLayerRequestedIsRaised()
    {
        var raised = false;
        _sut.AddLayerRequested += (_, _) => raised = true;

        _sut.AddLayerCommand.Execute(null);

        raised.Should().BeTrue();
    }

    [Fact]
    public void WhenDeleteLayerExecuted_DeleteLayerRequestedIsRaised()
    {
        Layer? deleted = null;
        _sut.DeleteLayerRequested += (_, layer) => deleted = layer;

        var layer1 = new Layer(10, 10, "L1");
        var layer2 = new Layer(10, 10, "L2");
        var vm1 = new LayerViewModel(layer1, _ => { }, _ => { });
        var vm2 = new LayerViewModel(layer2, _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);
        _sut.SelectedLayer = vm1;

        _sut.DeleteLayerCommand.Execute(null);

        deleted.Should().Be(layer1);
    }

    [Fact]
    public void WhenDuplicateLayerExecuted_DuplicateLayerRequestedIsRaised()
    {
        Layer? duplicated = null;
        _sut.DuplicateLayerRequested += (_, layer) => duplicated = layer;

        var layer = new Layer(10, 10, "L1");
        var vm = new LayerViewModel(layer, _ => { }, _ => { });
        _sut.Layers.Add(vm);
        _sut.SelectedLayer = vm;

        _sut.DuplicateLayerCommand.Execute(null);

        duplicated.Should().Be(layer);
    }

    [Fact]
    public void WhenMoveLayerUpExecuted_MoveLayerUpRequestedIsRaised()
    {
        Layer? moved = null;
        _sut.MoveLayerUpRequested += (_, layer) => moved = layer;

        var vm1 = new LayerViewModel(new Layer(10, 10, "L1"), _ => { }, _ => { });
        var vm2 = new LayerViewModel(new Layer(10, 10, "L2"), _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);
        _sut.SelectedLayer = vm2;

        _sut.MoveLayerUpCommand.Execute(null);

        moved.Should().Be(vm2.Layer);
    }

    [Fact]
    public void WhenFlattenLayersExecuted_FlattenLayersRequestedIsRaised()
    {
        var raised = false;
        _sut.FlattenLayersRequested += (_, _) => raised = true;

        var vm1 = new LayerViewModel(new Layer(10, 10, "L1"), _ => { }, _ => { });
        var vm2 = new LayerViewModel(new Layer(10, 10, "L2"), _ => { }, _ => { });
        _sut.Layers.Add(vm1);
        _sut.Layers.Add(vm2);

        _sut.FlattenLayersCommand.Execute(null);

        raised.Should().BeTrue();
    }

    #endregion

    #region Commands CanExecute

    [Fact]
    public void WhenHasImageIsFalse_AddLayerCommandCannotExecute()
    {
        var sut = new LayerPanelViewModel(hasImage: () => false);
        sut.AddLayerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenHasImageIsTrue_AddLayerCommandCanExecute()
    {
        _sut.AddLayerCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WhenOnlyOneLayer_DeleteLayerCommandCannotExecute()
    {
        var layer = new Layer(10, 10, "L1");
        var vm = new LayerViewModel(layer, _ => { }, _ => { });
        _sut.Layers.Add(vm);
        _sut.SelectedLayer = vm;

        _sut.DeleteLayerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenNoLayerSelected_DeleteLayerCommandCannotExecute()
    {
        _sut.DeleteLayerCommand.CanExecute(null).Should().BeFalse();
    }

    #endregion

    #region Layer stack panel (#594)

    [Fact]
    public void DeleteIsDisabledForALockedLayer()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        _sut.SelectedLayer = _sut.Layers[0];
        _sut.DeleteLayerCommand.CanExecute(null).Should().BeTrue();

        _sut.Layers[0].IsLocked = true;

        _sut.DeleteLayerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ARowsOwnDeleteRefusesItsLockedLayerEvenWhenAnotherRowIsSelected()
    {
        var trace = new List<string>();
        var sut = new LayerPanelViewModel(hasImage: () => true, trace: trace.Add);
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        sut.SyncLayers(stack);
        sut.SelectedLayer = sut.Layers[0];          // Top, unlocked
        sut.Layers[1].IsLocked = true;              // Bottom
        var requested = new List<Layer>();
        sut.DeleteLayerRequested += (_, layer) => requested.Add(layer);

        sut.Layers[1].DeleteCommand.Execute(null);

        requested.Should().BeEmpty("the row's own layer is locked; the selected one is not the target");
        trace.Should().Contain("Refused to delete layer 'Bottom': it is locked.");
    }

    [Fact]
    public void ARowsOwnDeleteDeletesThatRowsLayerNotTheSelectedOne()
    {
        var stack = new LayerStack(10, 10);
        var bottom = stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        _sut.SelectedLayer = _sut.Layers[0];        // Top, locked
        _sut.Layers[0].IsLocked = true;
        var requested = new List<Layer>();
        _sut.DeleteLayerRequested += (_, layer) => requested.Add(layer);

        _sut.Layers[1].DeleteCommand.Execute(null);

        requested.Should().Equal(bottom);
    }

    [Fact]
    public void ARowsOwnDeleteRefusesTheInpaintMask()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        var mask = stack.AddLayer("Mask");
        mask!.IsInpaintMask = true;
        _sut.SyncLayers(stack);
        var requested = false;
        _sut.DeleteLayerRequested += (_, _) => requested = true;

        _sut.Layers[0].DeleteCommand.Execute(null);

        requested.Should().BeFalse();
    }

    [Fact]
    public void ALockedInpaintMaskDisablesFlattenAndMergeVisible()
    {
        // Flatten All replaces the whole stack, mask included.
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        var mask = stack.AddLayer("Mask");
        mask.IsInpaintMask = true;
        _sut.SyncLayers(stack);

        _sut.Layers[0].IsLocked = true;

        _sut.FlattenLayersCommand.CanExecute(null).Should().BeFalse();
        _sut.MergeVisibleLayersCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void DeleteLayerCommandExecuteRefusesTheMaskAndTheLastLayer()
    {
        // RelayCommand.Execute does not consult CanExecute, so the rule must hold in Execute as well.
        var stack = new LayerStack(10, 10);
        var only = stack.AddLayer("Only");
        _sut.SyncLayers(stack);
        var requested = new List<Layer>();
        _sut.DeleteLayerRequested += (_, layer) => requested.Add(layer);

        _sut.DeleteLayerCommand.Execute(null);        // the last layer
        requested.Should().BeEmpty();

        var mask = stack.AddLayer("Mask");
        mask.IsInpaintMask = true;
        _sut.SyncLayers(stack);
        _sut.SelectedLayer = _sut.Layers[0];          // the mask

        _sut.DeleteLayerCommand.Execute(null);

        requested.Should().BeEmpty();
        _sut.SelectedLayer = _sut.Layers[1];
        _sut.DeleteLayerCommand.Execute(null);
        requested.Should().Equal(only);
    }

    [Fact]
    public void CommandsFollowAReorderThatKeepsTheSelectedRow()
    {
        // The editor's up arrow moves the layer and syncs; the active layer, and so the selected row,
        // stays the same, so the SelectedLayer setter changes nothing and cannot be what refreshes.
        var stack = new LayerStack(10, 10);
        var bottom = stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        stack.ActiveLayer = bottom;
        _sut.SyncLayers(stack);
        _sut.MoveLayerDownCommand.CanExecute(null).Should().BeFalse();
        _sut.MergeLayerDownCommand.CanExecute(null).Should().BeFalse();
        var refreshed = 0;
        _sut.MoveLayerDownCommand.CanExecuteChanged += (_, _) => refreshed++;

        stack.MoveLayerUp(bottom);
        _sut.SyncLayers(stack);

        refreshed.Should().BeGreaterThan(0);
        _sut.MoveLayerDownCommand.CanExecute(null).Should().BeTrue();
        _sut.MoveLayerUpCommand.CanExecute(null).Should().BeFalse();
        _sut.MergeLayerDownCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CommandsFollowTheCountDroppingToOneLayer()
    {
        var stack = new LayerStack(10, 10);
        var bottom = stack.AddLayer("Bottom");
        var top = stack.AddLayer("Top");
        stack.ActiveLayer = bottom;
        _sut.SyncLayers(stack);
        _sut.DeleteLayerCommand.CanExecute(null).Should().BeTrue();
        // CanExecute is evaluated live; only the notification tells the button to look again.
        var refreshed = 0;
        _sut.DeleteLayerCommand.CanExecuteChanged += (_, _) => refreshed++;

        stack.RemoveLayer(top);
        stack.ActiveLayer = bottom;
        _sut.SyncLayers(stack);

        refreshed.Should().BeGreaterThan(0);
        _sut.DeleteLayerCommand.CanExecute(null).Should().BeFalse("the last layer stays");
        _sut.FlattenLayersCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ALayerLeavingIsOneRemove()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("D");
        stack.AddLayer("C");
        stack.AddLayer("B");
        var a = stack.AddLayer("A");
        _sut.SyncLayers(stack);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        _sut.Layers.CollectionChanged += (_, e) => changes.Add(e.Action);

        stack.RemoveLayer(a);
        _sut.SyncLayers(stack);

        changes.Should().Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Remove);
        _sut.Layers.Select(r => r.Name).Should().Equal("B", "C", "D");
    }

    [Fact]
    public void MovingTheSelectedLayerUpMovesItsNeighbourInstead()
    {
        // A ListBox handles a Move as remove + add and deselects a moved selected row.
        var stack = new LayerStack(10, 10);
        var bottom = stack.AddLayer("Bottom");
        stack.AddLayer("Middle");
        stack.AddLayer("Top");
        stack.ActiveLayer = bottom;
        _sut.SyncLayers(stack);
        var selected = _sut.SelectedLayer;
        var moved = new List<object?>();
        _sut.Layers.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
                moved.Add(e.OldItems![0]);
        };

        stack.MoveLayerUp(bottom);
        _sut.SyncLayers(stack);

        moved.Should().ContainSingle().Which.Should().NotBeSameAs(selected);
        _sut.Layers.Select(r => r.Name).Should().Equal("Top", "Bottom", "Middle");
        _sut.SelectedLayer.Should().BeSameAs(selected);
    }

    [Fact]
    public void SyncLayersKeepsTheRowOfEveryLayerStillInTheStack()
    {
        // A rebuild would answer a pick that commits a pending transform with a new row object, reset the
        // list while it is changing its selection, and hand an open rename to another layer.
        var stack = new LayerStack(10, 10);
        var bottom = stack.AddLayer("Bottom");
        var middle = stack.AddLayer("Middle");
        var top = stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        var rowOf = _sut.Layers.ToDictionary(r => r.Layer);
        var resets = 0;
        _sut.Layers.CollectionChanged += (_, e) =>
            resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        _sut.SyncLayers(stack);
        _sut.Layers.Should().Equal(rowOf[top], rowOf[middle], rowOf[bottom]);

        var added = stack.AddLayer("Added");
        stack.RemoveLayer(middle);
        stack.MoveLayerDown(top);                     // bottom-up: Bottom, Top, Added -> Top, Bottom, Added
        _sut.SyncLayers(stack);

        _sut.Layers.Select(r => r.Name).Should().Equal(
            Enumerable.Range(0, stack.Count).Reverse().Select(i => stack[i].Name));
        _sut.Layers.Should().Contain(rowOf[top]).And.Contain(rowOf[bottom]);
        _sut.Layers.Should().NotContain(rowOf[middle]);
        _sut.Layers.Single(r => r.Layer == added).Should().NotBeNull();
        resets.Should().Be(0);
    }

    [Fact]
    public void MergeDownIsDisabledForALockedLayer()
    {
        // Merging down removes the merged layer, and lock protects a layer from removal.
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        _sut.SelectedLayer = _sut.Layers[0];
        _sut.MergeLayerDownCommand.CanExecute(null).Should().BeTrue();

        _sut.Layers[0].IsLocked = true;

        _sut.MergeLayerDownCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void MergingIntoALockedLayerBelowIsAllowed()
    {
        // The locked layer below stays in the stack; only the unlocked one on top goes.
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        _sut.Layers[1].IsLocked = true;
        _sut.SelectedLayer = _sut.Layers[0];

        _sut.MergeLayerDownCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void MergeVisibleAndFlattenAreDisabledWhileAnyLayerIsLocked()
    {
        // Both replace every layer with one, so a locked layer anywhere would be removed.
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        _sut.SelectedLayer = _sut.Layers[0];
        _sut.FlattenLayersCommand.CanExecute(null).Should().BeTrue();
        _sut.MergeVisibleLayersCommand.CanExecute(null).Should().BeTrue();

        _sut.Layers[1].IsLocked = true;

        _sut.FlattenLayersCommand.CanExecute(null).Should().BeFalse();
        _sut.MergeVisibleLayersCommand.CanExecute(null).Should().BeFalse();

        _sut.Layers[1].IsLocked = false;

        _sut.FlattenLayersCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void SyncLayers_OfAnEmptyStackClearsTheSelection()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Only");
        _sut.SyncLayers(stack);

        _sut.SyncLayers(null);

        _sut.SelectedLayer.Should().BeNull();
    }

    [Fact]
    public void LockAndRenameAreTracedOnce()
    {
        var trace = new List<string>();
        var sut = new LayerPanelViewModel(hasImage: () => true, trace: trace.Add);
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Sky");
        sut.SyncLayers(stack);

        sut.Layers[0].IsLocked = true;
        sut.Layers[0].Name = "Clouds";

        trace.Should().Equal("Layer 'Sky' locked.", "Renamed a layer to 'Clouds'.");
    }

    #endregion
}
