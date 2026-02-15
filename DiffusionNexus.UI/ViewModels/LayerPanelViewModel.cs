using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing the layer panel UI state, commands, and layer stack synchronization.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class LayerPanelViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private bool _isLayerMode;
    private LayerViewModel? _selectedLayer;
    private ObservableCollection<LayerViewModel> _layers = new();

    public LayerPanelViewModel(Func<bool> hasImage)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        _hasImage = hasImage;

        ToggleLayerModeCommand = new RelayCommand(ExecuteToggleLayerMode, () => _hasImage());
        AddLayerCommand = new RelayCommand(ExecuteAddLayer, () => _hasImage());
        DeleteLayerCommand = new RelayCommand(ExecuteDeleteLayer, () => _hasImage() && SelectedLayer is not null && Layers.Count > 1 && !SelectedLayer.Layer.IsInpaintMask);
        DuplicateLayerCommand = new RelayCommand(ExecuteDuplicateLayer, () => _hasImage() && SelectedLayer is not null && !SelectedLayer.Layer.IsInpaintMask);
        MoveLayerUpCommand = new RelayCommand(ExecuteMoveLayerUp, () => _hasImage() && SelectedLayer is not null && CanMoveLayerUp);
        MoveLayerDownCommand = new RelayCommand(ExecuteMoveLayerDown, () => _hasImage() && SelectedLayer is not null && CanMoveLayerDown);
        MergeLayerDownCommand = new RelayCommand(ExecuteMergeLayerDown, () => _hasImage() && SelectedLayer is not null && CanMergeDown);
        MergeVisibleLayersCommand = new RelayCommand(ExecuteMergeVisibleLayers, () => _hasImage() && Layers.Count > 1);
        FlattenLayersCommand = new RelayCommand(ExecuteFlattenLayers, () => _hasImage() && Layers.Count > 1);
        SaveLayeredTiffCommand = new AsyncRelayCommand(ExecuteSaveLayeredTiffAsync, () => _hasImage());
    }

    #region Properties

    /// <summary>Whether layer mode is enabled.</summary>
    public bool IsLayerMode
    {
        get => _isLayerMode;
        set
        {
            if (SetProperty(ref _isLayerMode, value))
            {
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Collection of layer view models.</summary>
    public ObservableCollection<LayerViewModel> Layers
    {
        get => _layers;
        set => SetProperty(ref _layers, value);
    }

    /// <summary>Currently selected layer.</summary>
    public LayerViewModel? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (SetProperty(ref _selectedLayer, value))
            {
                foreach (var layer in _layers)
                {
                    layer.IsSelected = layer == value;
                }
                NotifyCommandsCanExecuteChanged();
                LayerSelectionChanged?.Invoke(this, value?.Layer);
            }
        }
    }

    /// <summary>Whether the selected layer can be moved up (towards top of visual list).</summary>
    public bool CanMoveLayerUp
    {
        get
        {
            if (_selectedLayer is null) return false;
            var index = _layers.IndexOf(_selectedLayer);
            return index > 0;
        }
    }

    /// <summary>Whether the selected layer can be moved down (towards bottom of visual list).</summary>
    public bool CanMoveLayerDown
    {
        get
        {
            if (_selectedLayer is null) return false;
            var index = _layers.IndexOf(_selectedLayer);
            return index < _layers.Count - 1;
        }
    }

    /// <summary>Whether the selected layer can be merged down.</summary>
    public bool CanMergeDown
    {
        get
        {
            if (_selectedLayer is null) return false;
            var index = _layers.IndexOf(_selectedLayer);
            return index < _layers.Count - 1;
        }
    }

    #endregion

    #region Commands

    public IRelayCommand ToggleLayerModeCommand { get; }
    public IRelayCommand AddLayerCommand { get; }
    public IRelayCommand DeleteLayerCommand { get; }
    public IRelayCommand DuplicateLayerCommand { get; }
    public IRelayCommand MoveLayerUpCommand { get; }
    public IRelayCommand MoveLayerDownCommand { get; }
    public IRelayCommand MergeLayerDownCommand { get; }
    public IRelayCommand MergeVisibleLayersCommand { get; }
    public IRelayCommand FlattenLayersCommand { get; }
    public IAsyncRelayCommand SaveLayeredTiffCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when layer selection changes.</summary>
    public event EventHandler<Layer?>? LayerSelectionChanged;

    /// <summary>Event raised when a layered TIFF save is requested.</summary>
    public event Func<string, Task<bool>>? SaveLayeredTiffRequested;

    /// <summary>Event raised when layer mode is toggled.</summary>
    public event EventHandler<bool>? EnableLayerModeRequested;

    /// <summary>Event raised when a new layer should be added.</summary>
    public event EventHandler? AddLayerRequested;

    /// <summary>Event raised when a layer should be deleted.</summary>
    public event EventHandler<Layer>? DeleteLayerRequested;

    /// <summary>Event raised when a layer should be duplicated.</summary>
    public event EventHandler<Layer>? DuplicateLayerRequested;

    /// <summary>Event raised when a layer should be moved up.</summary>
    public event EventHandler<Layer>? MoveLayerUpRequested;

    /// <summary>Event raised when a layer should be moved down.</summary>
    public event EventHandler<Layer>? MoveLayerDownRequested;

    /// <summary>Event raised when a layer should be merged down.</summary>
    public event EventHandler<Layer>? MergeLayerDownRequested;

    /// <summary>Event raised when all visible layers should be merged.</summary>
    public event EventHandler? MergeVisibleLayersRequested;

    /// <summary>Event raised when all layers should be flattened.</summary>
    public event EventHandler? FlattenLayersRequested;

    #endregion

    #region Public Methods

    /// <summary>
    /// Synchronizes the layer view models with the editor core's layer stack.
    /// </summary>
    public void SyncLayers(LayerStack? layerStack)
    {
        foreach (var vm in _layers)
        {
            vm.Dispose();
        }
        _layers.Clear();

        if (layerStack is null || layerStack.Count == 0)
        {
            SelectedLayer = null;
            return;
        }

        for (var i = layerStack.Count - 1; i >= 0; i--)
        {
            var layer = layerStack[i];
            var vm = new LayerViewModel(layer, OnLayerSelectionRequested, OnLayerDeleteRequested);
            _layers.Add(vm);
        }

        if (layerStack.ActiveLayer is not null)
        {
            var activeVm = _layers.FirstOrDefault(vm => vm.Layer == layerStack.ActiveLayer);
            SelectedLayer = activeVm;
        }
        else if (_layers.Count > 0)
        {
            SelectedLayer = _layers[0];
        }
    }

    /// <summary>
    /// Notifies all commands that their CanExecute state may have changed.
    /// Called by the parent ViewModel when HasImage changes.
    /// </summary>
    public void NotifyCommandsCanExecuteChanged()
    {
        AddLayerCommand.NotifyCanExecuteChanged();
        DeleteLayerCommand.NotifyCanExecuteChanged();
        DuplicateLayerCommand.NotifyCanExecuteChanged();
        MoveLayerUpCommand.NotifyCanExecuteChanged();
        MoveLayerDownCommand.NotifyCanExecuteChanged();
        MergeLayerDownCommand.NotifyCanExecuteChanged();
        MergeVisibleLayersCommand.NotifyCanExecuteChanged();
        FlattenLayersCommand.NotifyCanExecuteChanged();
        SaveLayeredTiffCommand.NotifyCanExecuteChanged();
        ToggleLayerModeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanMoveLayerUp));
        OnPropertyChanged(nameof(CanMoveLayerDown));
        OnPropertyChanged(nameof(CanMergeDown));
    }

    #endregion

    #region Command Implementations

    private void ExecuteToggleLayerMode()
    {
        IsLayerMode = !IsLayerMode;
        EnableLayerModeRequested?.Invoke(this, IsLayerMode);
    }

    private void ExecuteAddLayer()
    {
        AddLayerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteDeleteLayer()
    {
        if (SelectedLayer is null) return;
        DeleteLayerRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteDuplicateLayer()
    {
        if (SelectedLayer is null) return;
        DuplicateLayerRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMoveLayerUp()
    {
        if (SelectedLayer is null) return;
        MoveLayerUpRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMoveLayerDown()
    {
        if (SelectedLayer is null) return;
        MoveLayerDownRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMergeLayerDown()
    {
        if (SelectedLayer is null) return;
        MergeLayerDownRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMergeVisibleLayers()
    {
        MergeVisibleLayersRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteFlattenLayers()
    {
        FlattenLayersRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteSaveLayeredTiffAsync()
    {
        if (SaveLayeredTiffRequested is not null && CurrentImagePath is not null)
        {
            var directory = Path.GetDirectoryName(CurrentImagePath);
            var fileName = Path.GetFileNameWithoutExtension(CurrentImagePath);
            var suggestedPath = Path.Combine(directory ?? "", $"{fileName}_layered.tif");

            var success = await SaveLayeredTiffRequested.Invoke(suggestedPath);
            SaveCompleted?.Invoke(this, success ? "Layered TIFF saved successfully" : "Failed to save layered TIFF");
        }
    }

    private void OnLayerSelectionRequested(LayerViewModel vm)
    {
        SelectedLayer = vm;
    }

    private void OnLayerDeleteRequested(LayerViewModel vm)
    {
        if (_layers.Count <= 1) return;
        ExecuteDeleteLayer();
    }

    #endregion

    /// <summary>
    /// Current image path, set by the parent ViewModel. Used for layered TIFF save path generation.
    /// </summary>
    internal string? CurrentImagePath { get; set; }

    /// <summary>
    /// Event raised when a save operation completes with a status message.
    /// </summary>
    public event EventHandler<string>? SaveCompleted;
}
