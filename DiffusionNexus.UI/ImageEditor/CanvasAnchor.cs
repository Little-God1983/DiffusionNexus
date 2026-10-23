namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Where the image sits inside an extended canvas. Decides how a typed size, a multiplier
/// or an aspect preset distributes the new pixels between the four edges: the image is
/// pinned to the named side/corner and the canvas grows away from it. <see cref="Custom"/>
/// is set by dragging the image on the canvas; it keeps the image's current offset from
/// the top-left corner and grows (or shrinks back) at the right and bottom edges.
/// </summary>
public enum CanvasAnchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
    Custom
}
