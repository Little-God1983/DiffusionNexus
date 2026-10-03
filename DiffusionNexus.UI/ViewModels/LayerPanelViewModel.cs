using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly Action<string>? _trace;

    /// <summary>
    /// Last traced (name, locked) per row. LayerViewModel raises each change twice (its own setter plus
    /// the forwarded Layer event), so the trace compares against this to log once.
    /// </summary>
    private readonly Dictionary<LayerViewModel, (string Name, bool Locked)> _traced = [];
    private bool _isLayerMode;
    private LayerViewModel? _selectedLayer;
    private ObservableCollection<LayerViewModel> _layers = new();

    public LayerPanelViewModel(Func<bool> hasImage, Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        _hasImage = hasImage;
        _trace = trace;

        ToggleLayerModeCommand = new RelayCommand(ExecuteToggleLayerMode, () => _hasImage());
        AddLayerCommand = new RelayCommand(ExecuteAddLayer, () => _hasImage());
        DeleteLayerCommand = new RelayCommand(ExecuteDeleteLayer, () => CanDelete(SelectedLayer));
        DuplicateLayerCommand = new RelayCommand(ExecuteDuplicateLayer, () => _hasImage() && SelectedLayer is not null && !SelectedLayer.Layer.IsInpaintMask);
        MoveLayerUpCommand = new RelayCommand(ExecuteMoveLayerUp, () => _hasImage() && SelectedLayer is not null && CanMoveLayerUp);
        MoveLayerDownCommand = new RelayCommand(ExecuteMoveLayerDown, () => _hasImage() && SelectedLayer is not null && CanMoveLayerDown);
        MergeLayerDownCommand = new RelayCommand(ExecuteMergeLayerDown, () => _hasImage() && SelectedLayer is not null && CanMergeDown);
        MergeVisibleLayersCommand = new RelayCommand(ExecuteMergeVisibleLayers, () => _hasImage() && Layers.Count > 1 && !HasLockedLayers);
        FlattenLayersCommand = new RelayCommand(ExecuteFlattenLayers, () => _hasImage() && Layers.Count > 1 && !HasLockedLayers);
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

    /// <summary>
    /// Currently selected layer. A null clears the editor core's active layer, so strokes go nowhere; the
    /// layer panel never writes one on its own (a ListBox's Ctrl+click, clear or detach deselect stays
    /// inside <c>LayerStackPanel</c>), and <see cref="SyncLayers"/> only sets null for an empty stack.
    /// </summary>
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

    /// <summary>
    /// Whether the selected layer can be merged down. Not when it is locked: merging removes it, and lock
    /// protects a layer from removal. Merging into a locked layer below is allowed; that layer stays.
    /// </summary>
    public bool CanMergeDown
    {
        get
        {
            if (_selectedLayer is null || _selectedLayer.IsLocked) return false;
            var index = _layers.IndexOf(_selectedLayer);
            return index < _layers.Count - 1;
        }
    }

    /// <summary>
    /// Whether any layer, the inpaint mask included, is locked. Flatten All and Merge Visible replace the
    /// stack's layers with one, so they are unavailable while a locked layer could be removed by them.
    /// (Merge Visible keeps the mask, but it has no button whose tooltip could explain that exception.)
    /// </summary>
    public bool HasLockedLayers => _layers.Any(l => l.IsLocked);

    /// <summary>
    /// The one delete rule, for the toolbar's command and a row's own delete alike: there is an image, the
    /// row is listed, it is not the last layer, not the inpaint mask and not locked.
    /// </summary>
    public bool CanDelete(LayerViewModel? row) =>
        _hasImage()
        && row is not null
        && _layers.Count > 1
        && _layers.Contains(row)
        && !row.Layer.IsInpaintMask
        && !row.IsLocked;

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
    /// Synchronizes the layer view models with the editor core's layer stack, top layer first.
    /// </summary>
    /// <remarks>
    /// Incremental: a layer still in the stack keeps its row object, and only the rows that changed are
    /// inserted, moved or removed (and only removed rows are disposed). The editor syncs after every edit,
    /// including when a pick commits a pending Move/Transform, so a rebuild would answer that pick with a
    /// new row object, reset the list while it is changing its selection, and hand an open rename's row
    /// container to another layer.
    /// </remarks>
    public void SyncLayers(LayerStack? layerStack)
    {
        var desired = new List<Layer>();
        if (layerStack is not null)
        {
            for (var i = layerStack.Count - 1; i >= 0; i--)
                desired.Add(layerStack[i]);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var existing = IndexOfRow(desired[i], from: i);
            if (existing == i)
                continue;

            if (existing > i)
            {
                _layers.Move(existing, i);
                continue;
            }

            var row = new LayerViewModel(desired[i], OnLayerSelectionRequested, OnLayerDeleteRequested);
            row.PropertyChanged += OnRowPropertyChanged;
            _traced[row] = (row.Name, row.IsLocked);
            _layers.Insert(i, row);
        }

        // Every kept row now sits in front; what is left behind belongs to layers that have gone.
        while (_layers.Count > desired.Count)
        {
            var gone = _layers[^1];
            _layers.RemoveAt(_layers.Count - 1);
            gone.PropertyChanged -= OnRowPropertyChanged;
            _traced.Remove(gone);
            gone.Dispose();
        }

        var active = layerStack?.ActiveLayer;
        SelectedLayer = active is not null
            ? _layers.FirstOrDefault(row => row.Layer == active)
            : _layers.FirstOrDefault();
    }

    private int IndexOfRow(Layer layer, int from)
    {
        for (var i = from; i < _layers.Count; i++)
        {
            if (ReferenceEquals(_layers[i].Layer, layer))
                return i;
        }

        return -1;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LayerViewModel row || !_traced.TryGetValue(row, out var seen))
            return;

        if (e.PropertyName == nameof(LayerViewModel.IsLocked) && row.IsLocked != seen.Locked)
        {
            _traced[row] = (seen.Name, row.IsLocked);
            _trace?.Invoke($"Layer '{row.Name}' {(row.IsLocked ? "locked" : "unlocked")}.");
            NotifyCommandsCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(LayerViewModel.Name) && row.Name != seen.Name)
        {
            _traced[row] = (row.Name, seen.Locked);
            _trace?.Invoke($"Renamed a layer to '{row.Name}'.");
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
        OnPropertyChanged(nameof(HasLockedLayers));
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

    private void ExecuteDeleteLayer() => TryDelete(SelectedLayer);

    /// <summary>
    /// Deletes <paramref name="row"/>'s layer if <see cref="CanDelete"/> allows it. Every delete goes
    /// through here: RelayCommand.Execute does not consult CanExecute, and a row's own delete has none.
    /// </summary>
    private void TryDelete(LayerViewModel? row)
    {
        if (!CanDelete(row))
        {
            if (row is { IsLocked: true })
                _trace?.Invoke($"Refused to delete layer '{row.Name}': it is locked.");
            return;
        }

        DeleteLayerRequested?.Invoke(this, row!.Layer);
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
        if (SelectedLayer is null || !CanMergeDown) return;
        MergeLayerDownRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMergeVisibleLayers()
    {
        if (HasLockedLayers) return;
        MergeVisibleLayersRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteFlattenLayers()
    {
        if (HasLockedLayers) return;
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

    /// <summary>A row's own delete: that row's layer, not the selected one, under <see cref="CanDelete"/>.</summary>
    private void OnLayerDeleteRequested(LayerViewModel vm) => TryDelete(vm);

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
