namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// What a canvas layer holds. Issue #518 region D names four kinds. Control layers (#596) and regional
/// prompts (#597) add theirs here.
/// </summary>
public enum CanvasLayerKind
{
    /// <summary>Pixels: an accepted generation result.</summary>
    Raster,

    /// <summary>Where to repaint: the canvas's one inpaint mask (#595).</summary>
    InpaintMask,
}
