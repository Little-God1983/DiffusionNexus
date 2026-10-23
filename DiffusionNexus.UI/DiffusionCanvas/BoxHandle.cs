namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// What a pointer is grabbing on the generation bounding box. The eight compass values are the
/// resize handles; <see cref="Move"/> is the box body.
/// </summary>
public enum BoxHandle
{
    /// <summary>The pointer is not over the box at all.</summary>
    None,

    /// <summary>The box body — dragging moves the whole box without resizing it.</summary>
    Move,

    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
}
