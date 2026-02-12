namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Manages viewport state: zoom level, pan offsets, fit-to-canvas mode,
/// and coordinate transforms between screen and image space.
/// </summary>
public interface IViewportManager
{
    /// <summary>
    /// Gets or sets the current zoom level (1.0 = 100%).
    /// Clamped between <see cref="MinZoom"/> and <see cref="MaxZoom"/>.
    /// </summary>
    float ZoomLevel { get; set; }

    /// <summary>
    /// Gets the zoom level as a percentage integer (10–1000).
    /// </summary>
    int ZoomPercentage { get; }

    /// <summary>
    /// Gets or sets the horizontal pan offset in screen pixels.
    /// </summary>
    float PanX { get; set; }

    /// <summary>
    /// Gets or sets the vertical pan offset in screen pixels.
    /// </summary>
    float PanY { get; set; }

    /// <summary>
    /// Gets or sets whether the image should fit to the canvas.
    /// Setting to true resets pan offsets.
    /// </summary>
    bool IsFitMode { get; set; }

    /// <summary>
    /// Minimum allowed zoom level.
    /// </summary>
    float MinZoom { get; }

    /// <summary>
    /// Maximum allowed zoom level.
    /// </summary>
    float MaxZoom { get; }

    /// <summary>
    /// Increases the zoom level by one step.
    /// </summary>
    void ZoomIn();

    /// <summary>
    /// Decreases the zoom level by one step.
    /// </summary>
    void ZoomOut();

    /// <summary>
    /// Sets the zoom level to fit the image within the canvas.
    /// </summary>
    void ZoomToFit();

    /// <summary>
    /// Sets the zoom level to 100% and resets pan.
    /// </summary>
    void ZoomToActual();

    /// <summary>
    /// Resets zoom, pan, and fit mode to initial state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Sets fit mode with a pre-calculated zoom level.
    /// </summary>
    /// <param name="fitZoom">The calculated zoom level for fit mode.</param>
    void SetFitModeWithZoom(float fitZoom);

    /// <summary>
    /// Pans the image by the specified delta values.
    /// Ignored when in fit mode.
    /// </summary>
    /// <param name="deltaX">Horizontal pan delta.</param>
    /// <param name="deltaY">Vertical pan delta.</param>
    void Pan(float deltaX, float deltaY);

    /// <summary>
    /// Raised when any viewport property changes.
    /// </summary>
    event EventHandler? Changed;
}
