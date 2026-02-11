namespace DiffusionNexus.UI.ImageEditor.Events;

/// <summary>
/// Raised when the active layer changes.
/// </summary>
/// <param name="OldLayer">The previously active layer, or null.</param>
/// <param name="NewLayer">The newly active layer.</param>
public record ActiveLayerChangedEvent(Layer? OldLayer, Layer? NewLayer);

/// <summary>
/// Raised when the layer stack is modified (layers added, removed, or reordered).
/// </summary>
/// <param name="ChangeType">The kind of change.</param>
/// <param name="AffectedLayer">The layer involved, or null for bulk operations.</param>
public record LayerStackChangedEvent(LayerChangeType ChangeType, Layer? AffectedLayer);

/// <summary>
/// Raised when any layer's content (pixels) changes.
/// </summary>
/// <param name="Layer">The layer whose content changed.</param>
public record LayerContentChangedEvent(Layer Layer);

/// <summary>
/// Raised when the active tool changes.
/// </summary>
/// <param name="OldToolId">The previous tool identifier, or null.</param>
/// <param name="NewToolId">The new tool identifier.</param>
public record ToolChangedEvent(string? OldToolId, string NewToolId);

/// <summary>
/// Raised when viewport zoom or pan changes.
/// </summary>
/// <param name="ZoomLevel">Current zoom level (1.0 = 100%).</param>
/// <param name="IsFitMode">Whether fit-to-canvas mode is active.</param>
/// <param name="PanX">Horizontal pan offset.</param>
/// <param name="PanY">Vertical pan offset.</param>
public record ViewportChangedEvent(float ZoomLevel, bool IsFitMode, float PanX, float PanY);

/// <summary>
/// Raised when the image or layer content changes and a re-render is needed.
/// </summary>
public record RenderRequestedEvent;

/// <summary>
/// Raised when the document dirty state should be updated.
/// </summary>
/// <param name="IsDirty">Whether the document has unsaved changes.</param>
public record DocumentDirtyEvent(bool IsDirty);

/// <summary>
/// Raised when an image is loaded or cleared.
/// </summary>
/// <param name="FilePath">Path of the loaded image, or null if cleared.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
public record ImageLoadedEvent(string? FilePath, int Width, int Height);

/// <summary>
/// Raised when an image is saved successfully.
/// </summary>
/// <param name="FilePath">The file path where the image was saved.</param>
public record ImageSavedEvent(string FilePath);

/// <summary>
/// Raised when a tool panel (color balance, brightness, etc.) is toggled.
/// </summary>
/// <param name="ToolId">Identifier of the tool panel.</param>
/// <param name="IsActive">Whether the panel is now open.</param>
public record ToolPanelToggledEvent(string ToolId, bool IsActive);

/// <summary>
/// Raised when a status message should be displayed.
/// </summary>
/// <param name="Message">The status message, or null to clear.</param>
public record StatusMessageEvent(string? Message);

/// <summary>
/// Describes the kind of change in a <see cref="LayerStackChangedEvent"/>.
/// </summary>
public enum LayerChangeType
{
    Added,
    Removed,
    Duplicated,
    Reordered,
    MergedDown,
    MergedVisible,
    Flattened
}
