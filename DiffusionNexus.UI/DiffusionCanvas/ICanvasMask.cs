using System.ComponentModel;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// The inpaint mask as the surface sees it (#595): strokes to draw, and a place to put a finished one.
/// Kept as an interface so the control does not need to know about the layer's view model, as
/// <see cref="ICanvasRaster"/> does for results.
/// </summary>
public interface ICanvasMask : INotifyPropertyChanged
{
    /// <summary>The strokes in drawing order. Replaced, never mutated, on every change.</summary>
    IReadOnlyList<CanvasMaskStroke> Strokes { get; }

    /// <summary>Whether the mask is drawn. A hidden mask is neither drawn nor painted on.</summary>
    bool IsVisible { get; }

    /// <summary>Whether the repaint area is everything except the painting.</summary>
    bool Invert { get; }

    /// <summary>Appends a finished stroke: one drag of the brush or eraser.</summary>
    void AddStroke(CanvasMaskStroke stroke);
}
