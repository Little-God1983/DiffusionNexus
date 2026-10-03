using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// The canvas's layer stack (#594, #518 region D): ordering, selection, lock and naming rules over the
/// canvas's <c>Frames</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Frames</c> stays the canonical order, <b>bottom to top</b>, because the surface, the hit test and
/// the region compositor all iterate it that way. <see cref="DisplayLayers"/> is a top-first mirror for
/// the layer panel. It is maintained incrementally rather than rebuilt, so a change touches only the rows
/// it concerns and the panel's rows and selection stay in step with the surface and the hit test.
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
    /// collection, or a layer's visibility, opacity or lock. Not raised for a rename.
    /// </summary>
    public event EventHandler? LayersChanged;

    /// <summary>The layers top first, as the panel lists them.</summary>
    public ObservableCollection<GenerationFrameViewModel> DisplayLayers { get; } = [];

    private GenerationFrameViewModel? _selectedLayer;

    /// <summary>The layer the inspector edits and the surface outlines.</summary>
    /// <remarks>
    /// The panel never writes null here on its own (a ListBox's Ctrl+click or detach deselect stays inside
    /// <c>LayerStackPanel</c>), so null means the stack chose it: the last layer has gone.
    /// </remarks>
    public GenerationFrameViewModel? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (SetProperty(ref _selectedLayer, value))
                NotifyCommands();
        }
    }

    /// <summary>True when the canvas holds at least one layer.</summary>
    public bool HasLayers => _frames.Count > 0;

    /// <summary>True when Clear canvas would remove something.</summary>
    public bool HasUnlockedLayers => _frames.Any(f => !f.IsLocked);

    /// <summary>Whether <paramref name="layer"/> may be deleted: it exists and is not locked.</summary>
    public static bool CanDelete(GenerationFrameViewModel? layer) => layer is { IsLocked: false };

    /// <summary>Raises the selected layer one step toward the top.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(+1);

    private bool CanMoveUp() =>
        SelectedLayer is { } layer && _frames.IndexOf(layer) is var index && index >= 0 && index < _frames.Count - 1;

    /// <summary>Lowers the selected layer one step toward the bottom.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(-1);

    private bool CanMoveDown() => SelectedLayer is { } layer && _frames.IndexOf(layer) > 0;

    /// <summary>Deletes the selected layer unless it is locked.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected() => Delete(SelectedLayer);

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
        {
            SelectedLayer = DisplayLayers.Count == 0
                ? null
                : DisplayLayers[Math.Clamp(displayIndex, 0, DisplayLayers.Count - 1)];
        }

        _trace($"Deleted layer '{layer.Name}' ({_frames.Count} layer(s) left).");
        return true;
    }

    /// <summary>Removes and disposes every unlocked layer; returns how many went and how many stayed.</summary>
    public (int Removed, int Kept) ClearUnlocked()
    {
        var removed = 0;
        foreach (var layer in _frames.Where(f => !f.IsLocked).ToList())
        {
            _frames.Remove(layer);
            layer.Dispose();
            removed++;
        }

        if (SelectedLayer is null || !_frames.Contains(SelectedLayer))
            SelectedLayer = DisplayLayers.FirstOrDefault();

        return (removed, _frames.Count);
    }

    /// <summary>
    /// Puts an accepted candidate on top of the stack and selects it. An unnamed frame is named
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

    private void Move(int delta)
    {
        if (SelectedLayer is not { } layer)
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
                // Frames index i maps to display index (count - 1 - i); the display list is one short here.
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
                DisplayLayers.Move(last - e.OldStartingIndex, last - e.NewStartingIndex);
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
                foreach (var layer in DisplayLayers)
                    Unobserve(layer);
                DisplayLayers.Clear();
                foreach (var layer in _frames)
                {
                    DisplayLayers.Insert(0, layer);
                    Observe(layer);
                }

                break;
            }
        }

        if (SelectedLayer is not null && !_frames.Contains(SelectedLayer))
            SelectedLayer = null;

        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(HasUnlockedLayers));
        NotifyCommands();
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Observe(GenerationFrameViewModel layer) => layer.PropertyChanged += OnLayerPropertyChanged;

    private void Unobserve(GenerationFrameViewModel layer) => layer.PropertyChanged -= OnLayerPropertyChanged;

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GenerationFrameViewModel layer)
            return;

        switch (e.PropertyName)
        {
            case nameof(GenerationFrameViewModel.IsVisible):
                _trace($"Layer '{layer.Name}' {(layer.IsVisible ? "shown" : "hidden")}.");
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.Opacity):
                // Not traced: a slider drag raises this dozens of times. The region-composite trace at
                // Generate records what the model actually received.
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.IsLocked):
                _trace($"Layer '{layer.Name}' {(layer.IsLocked ? "locked" : "unlocked")}.");
                OnPropertyChanged(nameof(HasUnlockedLayers));
                DeleteSelectedCommand.NotifyCanExecuteChanged();
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.Name):
                _trace($"Renamed a layer to '{layer.Name}'.");
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
