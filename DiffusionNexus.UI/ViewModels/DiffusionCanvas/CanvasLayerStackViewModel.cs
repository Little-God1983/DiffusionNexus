using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// The canvas's layer stack (#594, #518 region D): ordering, selection, lock and naming rules over the
/// canvas's <c>Frames</c>, plus its one inpaint <see cref="Mask"/> (#595).
/// </summary>
/// <remarks>
/// <para>
/// <c>Frames</c> stays the canonical order, <b>bottom to top</b>, because the surface, the hit test and
/// the region compositor all iterate it that way. <see cref="DisplayLayers"/> is a top-first mirror for
/// the layer panel. It is maintained incrementally rather than rebuilt, so a change touches only the rows
/// it concerns and the panel's rows and selection stay in step with the surface and the hit test.
/// </para>
/// <para>
/// The mask is not in <c>Frames</c>: it is an instruction to the model, not pixels in the picture, so
/// nothing that composites the picture may see it. Its row is pinned to the top of
/// <see cref="DisplayLayers"/>; ↑/↓ move rasters only, and a raster cannot rise above it.
/// </para>
/// <para>
/// Lock protects a layer from removal only: Delete refuses it and <see cref="ClearUnlocked"/> keeps it.
/// </para>
/// </remarks>
public sealed partial class CanvasLayerStackViewModel : ObservableObject
{
    private readonly ObservableCollection<GenerationFrameViewModel> _frames;
    private readonly Action<string> _trace;

    /// <summary>Last number handed out by <see cref="AddAccepted"/>. Never decremented, so a number is never reused.</summary>
    private int _lastNumber;

    public CanvasLayerStackViewModel(ObservableCollection<GenerationFrameViewModel> frames, Action<string>? trace = null)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _trace = trace ?? (_ => { });

        foreach (var frame in _frames)
        {
            DisplayLayers.Insert(0, frame);
            Observe(frame);
        }

        _frames.CollectionChanged += OnFramesChanged;
    }

    /// <summary>
    /// Raised when anything that changes what the model sees or what may be removed changes: the
    /// collection, a layer's visibility, opacity or lock, or the mask's strokes, feather or invert. Not
    /// raised for a rename.
    /// </summary>
    public event EventHandler? LayersChanged;

    /// <summary>The layers top first, as the panel lists them: the mask (when there is one), then the rasters.</summary>
    public ObservableCollection<ILayerStackItem> DisplayLayers { get; } = [];

    /// <summary>The canvas's inpaint mask, or null until <see cref="AddMask"/> creates it.</summary>
    [ObservableProperty]
    private InpaintMaskLayerViewModel? _mask;

    /// <summary>Rows above the first raster in <see cref="DisplayLayers"/>: the mask's, when there is one.</summary>
    private int RasterRowOffset => Mask is null ? 0 : 1;

    private ILayerStackItem? _selectedLayer;

    /// <summary>The layer the inspector edits and the surface outlines.</summary>
    /// <remarks>
    /// The panel never writes null here on its own (a ListBox's Ctrl+click or detach deselect stays inside
    /// <c>LayerStackPanel</c>), so null means the stack chose it: the last layer has gone.
    /// </remarks>
    public ILayerStackItem? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (!SetProperty(ref _selectedLayer, value))
                return;

            OnPropertyChanged(nameof(SelectedRaster));
            OnPropertyChanged(nameof(SelectedMask));
            NotifyCommands();
        }
    }

    /// <summary>The selected layer when it is a raster, for the raster inspector and the surface's outline.</summary>
    public GenerationFrameViewModel? SelectedRaster => SelectedLayer as GenerationFrameViewModel;

    /// <summary>The selected layer when it is the mask, for the mask inspector and the paint tools.</summary>
    public InpaintMaskLayerViewModel? SelectedMask => SelectedLayer as InpaintMaskLayerViewModel;

    /// <summary>True when the canvas holds at least one layer.</summary>
    public bool HasLayers => _frames.Count > 0 || Mask is not null;

    /// <summary>True when Clear canvas would remove something.</summary>
    public bool HasUnlockedLayers => _frames.Any(f => !f.IsLocked) || Mask is { IsLocked: false };

    /// <summary>Whether <paramref name="layer"/> may be deleted: it exists and is not locked.</summary>
    public static bool CanDelete(ILayerStackItem? layer) => layer is { IsLocked: false };

    /// <summary>Raises the selected raster one step toward the top.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(+1);

    private bool CanMoveUp() =>
        SelectedLayer is GenerationFrameViewModel layer
        && _frames.IndexOf(layer) is var index && index >= 0 && index < _frames.Count - 1;

    /// <summary>Lowers the selected raster one step toward the bottom.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(-1);

    private bool CanMoveDown() => SelectedLayer is GenerationFrameViewModel layer && _frames.IndexOf(layer) > 0;

    /// <summary>Deletes the selected layer unless it is locked.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        switch (SelectedLayer)
        {
            case GenerationFrameViewModel frame:
                Delete(frame);
                break;
            case InpaintMaskLayerViewModel:
                DeleteMask();
                break;
        }
    }

    private bool CanDeleteSelected() => CanDelete(SelectedLayer);

    /// <summary>
    /// Removes and disposes <paramref name="layer"/>. Returns false, and traces why, for a locked layer.
    /// Returns false, and touches nothing, for a layer that is no longer on the canvas: the canvas's
    /// right-click menu hands over the raster it captured when it opened, which may be gone by the time
    /// Delete is picked, and disposing it twice or logging a deletion that did not happen is wrong.
    /// If the deleted layer was selected, the row that takes its place is selected, so the inspector
    /// does not go blank.
    /// </summary>
    public bool Delete(GenerationFrameViewModel? layer)
    {
        if (layer is null)
            return false;

        if (layer.IsLocked)
        {
            _trace($"Refused to delete layer '{layer.Name}': it is locked.");
            return false;
        }

        var wasSelected = ReferenceEquals(SelectedLayer, layer);
        var displayIndex = DisplayLayers.IndexOf(layer);

        // Detach before disposing: a bitmap still bound into the visual tree faults the render.
        if (!_frames.Remove(layer))
            return false;

        layer.Dispose();

        if (wasSelected)
            SelectedLayer = RowNear(displayIndex);

        _trace($"Deleted layer '{layer.Name}' ({_frames.Count} layer(s) left).");
        return true;
    }

    /// <summary>
    /// Creates the inpaint mask and selects it, or selects the existing one: the canvas has one mask.
    /// </summary>
    public InpaintMaskLayerViewModel AddMask()
    {
        if (Mask is { } existing)
        {
            SelectedLayer = existing;
            _trace($"Selected the existing mask '{existing.Name}' (the canvas has one).");
            return existing;
        }

        var mask = new InpaintMaskLayerViewModel(_trace);
        Mask = mask;
        DisplayLayers.Insert(0, mask);
        Observe(mask);
        SelectedLayer = mask;

        _trace($"Added the mask '{mask.Name}' on top. Paint where the next image to image run may repaint.");
        StackChanged();
        return mask;
    }

    /// <summary>Removes the mask. Returns false, and traces why, when it is locked or absent.</summary>
    public bool DeleteMask()
    {
        if (Mask is not { } mask)
            return false;

        if (mask.IsLocked)
        {
            _trace($"Refused to delete the mask '{mask.Name}': it is locked.");
            return false;
        }

        var wasSelected = ReferenceEquals(SelectedLayer, mask);
        RemoveMaskRow(mask);

        if (wasSelected)
            SelectedLayer = RowNear(0);

        _trace($"Deleted the mask '{mask.Name}' ({mask.Strokes.Count} stroke(s)).");
        StackChanged();
        return true;
    }

    /// <summary>
    /// Removes every unlocked layer, the mask included, releasing the rasters' bitmaps; returns how many
    /// went and how many stayed.
    /// </summary>
    public (int Removed, int Kept) ClearUnlocked()
    {
        var removed = 0;
        foreach (var layer in _frames.Where(f => !f.IsLocked).ToList())
        {
            _frames.Remove(layer);
            layer.Dispose();
            removed++;
        }

        if (Mask is { IsLocked: false } mask)
        {
            RemoveMaskRow(mask);
            removed++;
            StackChanged();
        }

        if (SelectedLayer is null || !DisplayLayers.Contains(SelectedLayer))
            SelectedLayer = DisplayLayers.FirstOrDefault();

        return (removed, DisplayLayers.Count);
    }

    /// <summary>
    /// Puts an accepted candidate on top of the rasters and selects it. An unnamed frame is named
    /// "Layer N", where N counts up for the session and is never reused.
    /// </summary>
    public void AddAccepted(GenerationFrameViewModel frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (string.IsNullOrWhiteSpace(frame.Name))
            frame.Name = $"Layer {++_lastNumber}";

        _frames.Add(frame);
        SelectedLayer = frame;
        _trace($"Added layer '{frame.Name}' on top ({_frames.Count} layer(s)).");
    }

    private void RemoveMaskRow(InpaintMaskLayerViewModel mask)
    {
        DisplayLayers.Remove(mask);
        Unobserve(mask);
        Mask = null;
    }

    /// <summary>The row at <paramref name="displayIndex"/>, or the nearest one; null when the list is empty.</summary>
    private ILayerStackItem? RowNear(int displayIndex) =>
        DisplayLayers.Count == 0 ? null : DisplayLayers[Math.Clamp(displayIndex, 0, DisplayLayers.Count - 1)];

    private void Move(int delta)
    {
        if (SelectedLayer is not GenerationFrameViewModel layer)
            return;

        var from = _frames.IndexOf(layer);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _frames.Count)
            return;

        _frames.Move(from, to);

        _trace($"Moved layer '{layer.Name}' {(delta > 0 ? "up" : "down")} (now {_frames.Count - to} of {_frames.Count} from the top).");
        NotifyCommands();
    }

    private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ObservableCollection raises single-item Add/Remove/Replace/Move, and Reset for Clear.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
            {
                var added = (GenerationFrameViewModel)e.NewItems![0]!;
                // Frames index i maps to display index (offset + count - 1 - i); the display list is one
                // short here, so that is the display count minus i.
                DisplayLayers.Insert(DisplayLayers.Count - e.NewStartingIndex, added);
                Observe(added);
                break;
            }
            case NotifyCollectionChangedAction.Remove:
            {
                var removed = (GenerationFrameViewModel)e.OldItems![0]!;
                DisplayLayers.Remove(removed);
                Unobserve(removed);
                break;
            }
            case NotifyCollectionChangedAction.Move:
            {
                var last = _frames.Count - 1;
                DisplayLayers.Move(RasterRowOffset + last - e.OldStartingIndex, RasterRowOffset + last - e.NewStartingIndex);
                break;
            }
            case NotifyCollectionChangedAction.Replace:
            {
                var oldItem = (GenerationFrameViewModel)e.OldItems![0]!;
                var newItem = (GenerationFrameViewModel)e.NewItems![0]!;
                DisplayLayers[DisplayLayers.IndexOf(oldItem)] = newItem;
                Unobserve(oldItem);
                Observe(newItem);
                break;
            }
            default:
            {
                // Reset: the mask is not in Frames, so its row stays; every raster row is rebuilt.
                for (var i = DisplayLayers.Count - 1; i >= RasterRowOffset; i--)
                {
                    Unobserve(DisplayLayers[i]);
                    DisplayLayers.RemoveAt(i);
                }

                foreach (var layer in _frames)
                {
                    DisplayLayers.Insert(RasterRowOffset, layer);
                    Observe(layer);
                }

                break;
            }
        }

        if (SelectedLayer is GenerationFrameViewModel selected && !_frames.Contains(selected))
            SelectedLayer = null;

        StackChanged();
    }

    /// <summary>The stack's own membership changed: refresh what depends on it and tell the canvas.</summary>
    private void StackChanged()
    {
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(HasUnlockedLayers));
        NotifyCommands();
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Observe(ILayerStackItem layer) => layer.PropertyChanged += OnLayerPropertyChanged;

    private void Unobserve(ILayerStackItem layer) => layer.PropertyChanged -= OnLayerPropertyChanged;

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ILayerStackItem layer)
            return;

        var noun = layer is InpaintMaskLayerViewModel ? "Mask" : "Layer";

        switch (e.PropertyName)
        {
            case nameof(ILayerStackItem.IsVisible):
                _trace($"{noun} '{layer.Name}' {(layer.IsVisible ? "shown" : "hidden")}.");
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.Opacity):
            case nameof(InpaintMaskLayerViewModel.Feather):
                // Not traced: a slider drag raises this dozens of times. The trace at Generate records
                // what the model actually received.
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(InpaintMaskLayerViewModel.Revision):
            case nameof(InpaintMaskLayerViewModel.Invert):
                // The mask traces its own strokes and invert; the canvas re-reads whether it meets the box.
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(ILayerStackItem.IsLocked):
                _trace($"{noun} '{layer.Name}' {(layer.IsLocked ? "locked" : "unlocked")}.");
                OnPropertyChanged(nameof(HasUnlockedLayers));
                DeleteSelectedCommand.NotifyCanExecuteChanged();
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(ILayerStackItem.Name):
                _trace($"Renamed a {noun.ToLowerInvariant()} to '{layer.Name}'.");
                break;
        }
    }

    private void NotifyCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}
