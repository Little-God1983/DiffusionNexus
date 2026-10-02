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
    public void SyncLayers_IgnoresTheListBoxNullWriteBackAndKeepsTheActiveLayer()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        var top = stack.AddLayer("Top");
        stack.AddLayer("Third");
        stack.ActiveLayer = top;

        // A first sync leaves a layer selected, as in the running editor. Without it the null write-back
        // below is a no-op (null to null) and the test proves nothing.
        _sut.SyncLayers(stack);
        var raisedNull = false;
        _sut.LayerSelectionChanged += (_, layer) =>
        {
            if (layer is null)
            {
                raisedNull = true;
                stack.ActiveLayer = null;   // what ImageEditView's handler does with a null selection
            }
        };

        // Mimic the ListBox: when its ItemsSource is cleared it writes null into SelectedItem.
        _sut.Layers.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                _sut.SelectedLayer = null;
        };

        _sut.SyncLayers(stack);

        raisedNull.Should().BeFalse("a sync is not the user clearing the selection");
        _sut.SelectedLayer!.Layer.Should().BeSameAs(top);
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
