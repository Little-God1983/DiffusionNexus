using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Manages a stack of layers for the image editor.
/// Layers are ordered from bottom (index 0) to top (highest index).
/// </summary>
public class LayerStack : IDisposable
{
    private readonly ObservableCollection<Layer> _layers;
    private Layer? _activeLayer;
    private int _width;
    private int _height;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new empty layer stack.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public LayerStack(int width, int height)
    {
        _width = width;
        _height = height;
        _layers = new ObservableCollection<Layer>();
        _layers.CollectionChanged += OnLayersCollectionChanged;
    }

    /// <summary>
    /// Gets the observable collection of layers.
    /// </summary>
    public ObservableCollection<Layer> Layers => _layers;

    /// <summary>
    /// Gets or sets the currently active layer for editing.
    /// </summary>
    public Layer? ActiveLayer
    {
        get => _activeLayer;
        set
        {
            if (_activeLayer != value)
            {
                _activeLayer = value;
                ActiveLayerChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Gets the index of the active layer, or -1 if none.
    /// </summary>
    public int ActiveLayerIndex => _activeLayer != null ? _layers.IndexOf(_activeLayer) : -1;

    /// <summary>
    /// Gets the number of layers.
    /// </summary>
    public int Count => _layers.Count;

    /// <summary>
    /// Gets the image width in pixels.
    /// </summary>
    public int Width => _width;

    /// <summary>
    /// Gets the image height in pixels.
    /// </summary>
    public int Height => _height;

    /// <summary>
    /// Event raised when layers are added, removed, or reordered.
    /// </summary>
    public event EventHandler? LayersChanged;

    /// <summary>
    /// Event raised when the active layer changes.
    /// </summary>
    public event EventHandler? ActiveLayerChanged;

    /// <summary>
    /// Event raised when any layer content changes.
    /// </summary>
    public event EventHandler? ContentChanged;

    /// <summary>
    /// Adds a new empty layer at the top of the stack.
    /// If an inpaint mask layer exists it stays on top; the new layer is inserted below it.
    /// </summary>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The newly created layer.</returns>
    public Layer AddLayer(string? name = null)
    {
        var layerName = name ?? $"Layer {_layers.Count + 1}";
        var layer = new Layer(_width, _height, layerName);
        layer.ContentChanged += OnLayerContentChanged;

        // Keep inpaint mask as the topmost layer
        var maskIndex = GetInpaintMaskIndex();
        if (maskIndex >= 0)
        {
            _layers.Insert(maskIndex, layer);
        }
        else
        {
            _layers.Add(layer);
        }

        ActiveLayer = layer;
        return layer;
    }

    /// <summary>
    /// Adds a layer from an existing bitmap.
    /// If an inpaint mask layer exists it stays on top; the new layer is inserted below it.
    /// </summary>
    /// <param name="bitmap">Source bitmap.</param>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The newly created layer.</returns>
    public Layer AddLayerFromBitmap(SKBitmap bitmap, string? name = null)
    {
        var layerName = name ?? $"Layer {_layers.Count + 1}";
        var layer = new Layer(bitmap, layerName);
        layer.ContentChanged += OnLayerContentChanged;

        // Keep inpaint mask as the topmost layer
        var maskIndex = GetInpaintMaskIndex();
        if (maskIndex >= 0)
        {
            _layers.Insert(maskIndex, layer);
        }
        else
        {
            _layers.Add(layer);
        }

        ActiveLayer = layer;
        return layer;
    }

    /// <summary>
    /// Inserts a layer at the specified index.
    /// </summary>
    /// <param name="index">Index to insert at.</param>
    /// <param name="layer">Layer to insert.</param>
    public void InsertLayer(int index, Layer layer)
    {
        layer.ContentChanged += OnLayerContentChanged;
        _layers.Insert(Math.Clamp(index, 0, _layers.Count), layer);
        ActiveLayer = layer;
    }

    /// <summary>
    /// Removes the specified layer.
    /// The inpaint mask layer cannot be deleted directly; use ClearInpaintMask instead.
    /// </summary>
    /// <param name="layer">Layer to remove.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveLayer(Layer layer)
    {
        if (_layers.Count <= 1)
            return false; // Keep at least one layer

        // TODO: Allow inpaint mask deletion when multi-mask support is added
        if (layer.IsInpaintMask) return false;

        var index = _layers.IndexOf(layer);
        if (index < 0) return false;

        layer.ContentChanged -= OnLayerContentChanged;
        _layers.RemoveAt(index);

        if (_activeLayer == layer)
        {
            ActiveLayer = _layers.Count > 0 
                ? _layers[Math.Min(index, _layers.Count - 1)] 
                : null;
        }

        layer.Dispose();
        return true;
    }

    /// <summary>
    /// Removes the layer at the specified index.
    /// </summary>
    /// <param name="index">Index of layer to remove.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveLayerAt(int index)
    {
        if (index < 0 || index >= _layers.Count || _layers.Count <= 1)
            return false;

        return RemoveLayer(_layers[index]);
    }

    /// <summary>
    /// Duplicates the specified layer.
    /// Inpaint mask layers cannot be duplicated (only one mask allowed).
    /// </summary>
    /// <param name="layer">Layer to duplicate.</param>
    /// <returns>The duplicated layer, or null if the layer is an inpaint mask.</returns>
    public Layer? DuplicateLayer(Layer layer)
    {
        var index = _layers.IndexOf(layer);
        if (index < 0 || layer.IsInpaintMask) return null;

        var clone = layer.Clone();
        clone.ContentChanged += OnLayerContentChanged;
        _layers.Insert(index + 1, clone);
        ActiveLayer = clone;
        return clone;
    }

    /// <summary>
    /// Moves a layer up in the stack (towards front).
    /// A layer cannot move above the inpaint mask layer.
    /// </summary>
    /// <param name="layer">Layer to move.</param>
    /// <returns>True if moved successfully.</returns>
    public bool MoveLayerUp(Layer layer)
    {
        var index = _layers.IndexOf(layer);
        if (index < 0 || index >= _layers.Count - 1) return false;

        // Don't allow moving above the inpaint mask (must stay topmost)
        var targetIndex = index + 1;
        if (_layers[targetIndex].IsInpaintMask) return false;

        _layers.Move(index, targetIndex);
        return true;
    }

    /// <summary>
    /// Moves a layer down in the stack (towards back).
    /// The inpaint mask layer cannot be moved down (must stay topmost).
    /// </summary>
    /// <param name="layer">Layer to move.</param>
    /// <returns>True if moved successfully.</returns>
    public bool MoveLayerDown(Layer layer)
    {
        var index = _layers.IndexOf(layer);
        if (index <= 0 || layer.IsInpaintMask) return false;

        _layers.Move(index, index - 1);
        return true;
    }

    /// <summary>
    /// Merges the specified layer with the layer below it.
    /// The inpaint mask layer cannot be merged down.
    /// </summary>
    /// <param name="layer">Layer to merge down.</param>
    /// <returns>True if merged successfully.</returns>
    public bool MergeDown(Layer layer)
    {
        var index = _layers.IndexOf(layer);
        if (index <= 0 || layer.IsInpaintMask) return false;

        var belowLayer = _layers[index - 1];
        if (belowLayer.Bitmap == null || layer.Bitmap == null) return false;

        // Draw the top layer onto the bottom layer
        using var canvas = belowLayer.CreateCanvas();
        if (canvas == null) return false;

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)),
            BlendMode = layer.BlendMode.ToSKBlendMode()
        };

        if (layer.IsVisible)
        {
            canvas.DrawBitmap(layer.Bitmap, 0, 0, paint);
        }

        belowLayer.NotifyContentChanged();

        // Remove the merged layer
        layer.ContentChanged -= OnLayerContentChanged;
        _layers.RemoveAt(index);
        layer.Dispose();

        ActiveLayer = belowLayer;
        return true;
    }

    /// <summary>
    /// Merges all visible layers into one, preserving any inpaint mask layer.
    /// </summary>
    public void MergeVisible()
    {
        var composite = Flatten();
        if (composite == null) return;

        // Preserve the inpaint mask layer (if any)
        Layer? maskLayer = null;
        foreach (var layer in _layers)
        {
            if (layer.IsInpaintMask)
            {
                maskLayer = layer;
                layer.ContentChanged -= OnLayerContentChanged;
                break;
            }
        }

        // Clear all layers
        foreach (var layer in _layers.ToList())
        {
            if (layer == maskLayer) continue;
            layer.ContentChanged -= OnLayerContentChanged;
            layer.Dispose();
        }
        _layers.Clear();

        // Add the merged result
        var merged = new Layer(composite, "Merged");
        merged.ContentChanged += OnLayerContentChanged;
        _layers.Add(merged);
        ActiveLayer = merged;

        // Re-add inpaint mask on top
        if (maskLayer is not null)
        {
            maskLayer.ContentChanged += OnLayerContentChanged;
            _layers.Add(maskLayer);
        }

        composite.Dispose();
    }

    /// <summary>
    /// Flattens all visible layers into a single bitmap.
    /// </summary>
    /// <returns>A new bitmap with all layers composited.</returns>
    public SKBitmap? Flatten()
    {
        if (_layers.Count == 0) return null;

        var result = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        // Draw layers from bottom to top (skip inpaint mask layers)
        foreach (var layer in _layers)
        {
            if (!layer.IsVisible || layer.Bitmap == null || layer.IsInpaintMask) continue;

            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)),
                BlendMode = layer.BlendMode.ToSKBlendMode()
            };

            canvas.DrawBitmap(layer.Bitmap, 0, 0, paint);
        }

        return result;
    }

    /// <summary>
    /// Clears all layers and creates a new background layer.
    /// </summary>
    public void Clear()
    {
        foreach (var layer in _layers.ToList())
        {
            layer.ContentChanged -= OnLayerContentChanged;
            layer.Dispose();
        }
        _layers.Clear();

        AddLayer("Background");
    }

    /// <summary>
    /// Resizes all layers to new dimensions.
    /// </summary>
    /// <param name="newWidth">New width in pixels.</param>
    /// <param name="newHeight">New height in pixels.</param>
    public void Resize(int newWidth, int newHeight)
    {
        _width = newWidth;
        _height = newHeight;

        foreach (var layer in _layers)
        {
            layer.Resize(newWidth, newHeight);
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Crops all layers to the specified rectangle.
    /// </summary>
    /// <param name="cropRect">The crop rectangle in pixel coordinates.</param>
    public void CropAll(SKRectI cropRect)
    {
        if (cropRect.Width <= 0 || cropRect.Height <= 0) return;

        _width = cropRect.Width;
        _height = cropRect.Height;

        foreach (var layer in _layers)
        {
            layer.Crop(cropRect);
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a transformation function to all layers.
    /// Used for rotate/flip operations that affect all layers.
    /// </summary>
    /// <param name="transform">Function that takes a layer and returns a transformed bitmap.</param>
    public void TransformAll(Func<Layer, SKBitmap?> transform)
    {
        SKBitmap? firstResult = null;
        
        foreach (var layer in _layers)
        {
            var transformed = transform(layer);
            if (transformed != null)
            {
                firstResult ??= transformed;
                layer.ReplaceBitmap(transformed);
            }
        }

        // Update stack dimensions from first transformed layer
        if (firstResult != null)
        {
            _width = firstResult.Width;
            _height = firstResult.Height;
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>
    /// Gets a layer by index.
    /// </summary>
    public Layer this[int index] => _layers[index];

    /// <summary>
    /// Gets the index of the inpaint mask layer, or -1 if none exists.
    /// </summary>
    private int GetInpaintMaskIndex()
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            if (_layers[i].IsInpaintMask)
                return i;
        }
        return -1;
    }

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLayerContentChanged(object? sender, EventArgs e)
    {
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var layer in _layers)
        {
            layer.ContentChanged -= OnLayerContentChanged;
            layer.Dispose();
        }
        _layers.Clear();

        GC.SuppressFinalize(this);
    }
}
