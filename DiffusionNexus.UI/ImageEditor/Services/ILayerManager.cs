using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Manages the layer stack and provides a high-level API for layer operations.
/// Acts as a facade over <see cref="LayerStack"/>.
/// </summary>
public interface ILayerManager
{
    /// <summary>
    /// Gets the underlying layer stack. May be null if no image is loaded.
    /// </summary>
    LayerStack? Stack { get; }

    /// <summary>
    /// Gets or sets whether layer mode is enabled.
    /// </summary>
    bool IsLayerMode { get; set; }

    /// <summary>
    /// Gets or sets the currently active layer.
    /// </summary>
    Layer? ActiveLayer { get; set; }

    /// <summary>
    /// Gets the number of layers, or 0 if no stack exists.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the image width from the layer stack.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the image height from the layer stack.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Initializes a new layer stack from a working bitmap.
    /// </summary>
    /// <param name="workingBitmap">The source bitmap.</param>
    /// <param name="layerName">Name for the initial layer.</param>
    void EnableLayerMode(SKBitmap workingBitmap, string layerName);

    /// <summary>
    /// Disables layer mode, flattening all layers into a single bitmap.
    /// </summary>
    /// <returns>The flattened bitmap, or null.</returns>
    SKBitmap? DisableLayerMode();

    /// <summary>
    /// Adds a new empty layer.
    /// </summary>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The created layer, or null if not in layer mode.</returns>
    Layer? AddLayer(string? name = null);

    /// <summary>
    /// Adds a layer from a bitmap.
    /// </summary>
    /// <param name="bitmap">Source bitmap.</param>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The created layer, or null if not in layer mode.</returns>
    Layer? AddLayerFromBitmap(SKBitmap bitmap, string? name = null);

    /// <summary>
    /// Removes a layer.
    /// </summary>
    /// <param name="layer">The layer to remove.</param>
    /// <returns>True if removed.</returns>
    bool RemoveLayer(Layer layer);

    /// <summary>
    /// Duplicates a layer.
    /// </summary>
    /// <param name="layer">The layer to duplicate.</param>
    /// <returns>The duplicated layer, or null.</returns>
    Layer? DuplicateLayer(Layer layer);

    /// <summary>
    /// Moves a layer up (towards front).
    /// </summary>
    bool MoveLayerUp(Layer layer);

    /// <summary>
    /// Moves a layer down (towards back).
    /// </summary>
    bool MoveLayerDown(Layer layer);

    /// <summary>
    /// Merges the layer with the one below it.
    /// </summary>
    bool MergeLayerDown(Layer layer);

    /// <summary>
    /// Merges all visible layers.
    /// </summary>
    void MergeVisibleLayers();

    /// <summary>
    /// Flattens all layers to a single bitmap without modifying the stack.
    /// </summary>
    /// <returns>Flattened bitmap, or null.</returns>
    SKBitmap? Flatten();

    /// <summary>
    /// Flattens all layers into a single layer, keeping layer mode active.
    /// </summary>
    void FlattenAllLayers();

    /// <summary>
    /// Tears down the layer stack and resets to non-layer mode without flattening.
    /// Use when the current image is being discarded (e.g., loading a new image).
    /// </summary>
    void Reset();

    /// <summary>
    /// Raised when the layer stack structure changes (add/remove/reorder).
    /// </summary>
    event EventHandler? LayersChanged;

    /// <summary>
    /// Raised when any layer's content changes.
    /// </summary>
    event EventHandler? ContentChanged;

    /// <summary>
    /// Raised when layer mode is toggled.
    /// </summary>
    event EventHandler? LayerModeChanged;
}
