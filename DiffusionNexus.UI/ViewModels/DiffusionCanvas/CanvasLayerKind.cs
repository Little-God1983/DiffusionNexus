namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// What a canvas layer holds. Issue #518 region D names four kinds; only <see cref="Raster"/> exists so
/// far. Inpaint masks (#595), control layers (#596) and regional prompts (#597) add theirs here.
/// </summary>
public enum CanvasLayerKind
{
    /// <summary>Pixels: an accepted generation result.</summary>
    Raster,
}
