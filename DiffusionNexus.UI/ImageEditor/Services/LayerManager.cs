using DiffusionNexus.UI.ImageEditor.Events;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Facade over <see cref="LayerStack"/> that publishes layer events via <see cref="IEventBus"/>.
/// Owns the layer stack lifecycle and event subscriptions.
/// </summary>
internal sealed class LayerManager : ILayerManager, IDisposable
{
    private readonly IEventBus _eventBus;
    private LayerStack? _stack;
    private bool _isLayerMode;

    public LayerManager(IEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        _eventBus = eventBus;
    }

    /// <inheritdoc />
    public LayerStack? Stack => _stack;

    /// <inheritdoc />
    public bool IsLayerMode
    {
        get => _isLayerMode;
        set
        {
            if (_isLayerMode == value) return;
            _isLayerMode = value;
            LayerModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public Layer? ActiveLayer
    {
        get => _stack?.ActiveLayer;
        set
        {
            if (_stack is null) return;
            var old = _stack.ActiveLayer;
            _stack.ActiveLayer = value;
            if (old != value)
            {
                _eventBus.Publish(new ActiveLayerChangedEvent(old, value));
            }
        }
    }

    /// <inheritdoc />
    public int Count => _stack?.Count ?? 0;

    /// <inheritdoc />
    public int Width => _stack?.Width ?? 0;

    /// <inheritdoc />
    public int Height => _stack?.Height ?? 0;

    /// <inheritdoc />
    public void EnableLayerMode(SKBitmap workingBitmap, string layerName)
    {
        ArgumentNullException.ThrowIfNull(workingBitmap);

        if (_stack != null) return; // Already enabled

        _stack = new LayerStack(workingBitmap.Width, workingBitmap.Height);
        _stack.AddLayerFromBitmap(workingBitmap, layerName);
        _stack.ContentChanged += OnStackContentChanged;
        _stack.LayersChanged += OnStackLayersChanged;
        _isLayerMode = true;
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
        _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Added, _stack.ActiveLayer));
    }

    /// <inheritdoc />
    public SKBitmap? DisableLayerMode()
    {
        if (_stack is null) return null;

        var flattened = _stack.Flatten();
        UnsubscribeAndDispose();
        _isLayerMode = false;
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
        return flattened;
    }

    /// <inheritdoc />
    public Layer? AddLayer(string? name = null)
    {
        if (!_isLayerMode || _stack is null) return null;
        var layer = _stack.AddLayer(name);
        _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Added, layer));
        return layer;
    }

    /// <inheritdoc />
    public Layer? AddLayerFromBitmap(SKBitmap bitmap, string? name = null)
    {
        if (!_isLayerMode || _stack is null) return null;
        var layer = _stack.AddLayerFromBitmap(bitmap, name);
        _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Added, layer));
        return layer;
    }

    /// <inheritdoc />
    public bool RemoveLayer(Layer layer)
    {
        if (!_isLayerMode || _stack is null) return false;
        var removed = _stack.RemoveLayer(layer);
        if (removed)
            _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Removed, layer));
        return removed;
    }

    /// <inheritdoc />
    public Layer? DuplicateLayer(Layer layer)
    {
        if (!_isLayerMode || _stack is null) return null;
        var clone = _stack.DuplicateLayer(layer);
        if (clone is not null)
            _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Duplicated, clone));
        return clone;
    }

    /// <inheritdoc />
    public bool MoveLayerUp(Layer layer)
    {
        if (!_isLayerMode || _stack is null) return false;
        var moved = _stack.MoveLayerUp(layer);
        if (moved)
            _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Reordered, layer));
        return moved;
    }

    /// <inheritdoc />
    public bool MoveLayerDown(Layer layer)
    {
        if (!_isLayerMode || _stack is null) return false;
        var moved = _stack.MoveLayerDown(layer);
        if (moved)
            _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Reordered, layer));
        return moved;
    }

    /// <inheritdoc />
    public bool MergeLayerDown(Layer layer)
    {
        if (!_isLayerMode || _stack is null) return false;
        var merged = _stack.MergeDown(layer);
        if (merged)
            _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.MergedDown, _stack.ActiveLayer));
        return merged;
    }

    /// <inheritdoc />
    public void MergeVisibleLayers()
    {
        if (!_isLayerMode || _stack is null) return;
        _stack.MergeVisible();
        _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.MergedVisible, _stack.ActiveLayer));
    }

    /// <inheritdoc />
    public SKBitmap? Flatten()
    {
        return _stack?.Flatten();
    }

    /// <inheritdoc />
    public void FlattenAllLayers()
    {
        if (_stack is null || _stack.Count == 0) return;

        var flattened = _stack.Flatten();
        if (flattened is null) return;

        var layerName = _stack.Count > 1 ? "Flattened" : (_stack[0].Name);

        // Tear down old stack
        _stack.ContentChanged -= OnStackContentChanged;
        _stack.LayersChanged -= OnStackLayersChanged;
        _stack.Dispose();

        // Build new stack with single layer
        _stack = new LayerStack(flattened.Width, flattened.Height);
        _stack.AddLayerFromBitmap(flattened, layerName);
        _stack.ContentChanged += OnStackContentChanged;
        _stack.LayersChanged += OnStackLayersChanged;
        flattened.Dispose();

        _eventBus.Publish(new LayerStackChangedEvent(LayerChangeType.Flattened, _stack.ActiveLayer));
        LayersChanged?.Invoke(this, EventArgs.Empty);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? LayersChanged;

    /// <inheritdoc />
    public event EventHandler? ContentChanged;

    /// <inheritdoc />
    public event EventHandler? LayerModeChanged;

    private void OnStackContentChanged(object? sender, EventArgs e)
    {
        ContentChanged?.Invoke(this, EventArgs.Empty);
        _eventBus.Publish(new RenderRequestedEvent());
    }

    private void OnStackLayersChanged(object? sender, EventArgs e)
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnsubscribeAndDispose()
    {
        if (_stack is null) return;
        _stack.ContentChanged -= OnStackContentChanged;
        _stack.LayersChanged -= OnStackLayersChanged;
        _stack.Dispose();
        _stack = null;
    }

    public void Dispose()
    {
        UnsubscribeAndDispose();
    }
}
