using DiffusionNexus.UI.ImageEditor.Events;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Manages tool activation with mutual exclusion.
/// Only one tool/panel can be active at a time.
/// </summary>
public interface IToolManager
{
    /// <summary>
    /// Gets the identifier of the currently active tool, or null if none.
    /// </summary>
    string? ActiveToolId { get; }

    /// <summary>
    /// Activates the tool identified by <paramref name="toolId"/>,
    /// deactivating any currently active tool first.
    /// </summary>
    /// <param name="toolId">The tool identifier to activate.</param>
    void Activate(string toolId);

    /// <summary>
    /// Deactivates the tool identified by <paramref name="toolId"/> if it is currently active.
    /// </summary>
    /// <param name="toolId">The tool identifier to deactivate.</param>
    void Deactivate(string toolId);

    /// <summary>
    /// Toggles the tool identified by <paramref name="toolId"/>.
    /// If it is currently active it will be deactivated; otherwise it will be activated.
    /// </summary>
    /// <param name="toolId">The tool identifier to toggle.</param>
    void Toggle(string toolId);

    /// <summary>
    /// Deactivates whatever tool is currently active.
    /// </summary>
    void DeactivateAll();

    /// <summary>
    /// Checks whether the specified tool is currently active.
    /// </summary>
    /// <param name="toolId">The tool identifier.</param>
    /// <returns>True if the tool is the active one.</returns>
    bool IsActive(string toolId);

    /// <summary>
    /// Registers a deactivation callback for a tool.
    /// Called when the tool is being deactivated (either by another tool activating,
    /// or by an explicit deactivate call).
    /// </summary>
    /// <param name="toolId">The tool identifier.</param>
    /// <param name="onDeactivate">Callback invoked when the tool is deactivated.</param>
    void RegisterDeactivationCallback(string toolId, Action onDeactivate);

    /// <summary>
    /// Raised when the active tool changes.
    /// </summary>
    event EventHandler<ToolChangedEventArgs>? ActiveToolChanged;
}

/// <summary>
/// Event arguments for tool changes.
/// </summary>
/// <param name="OldToolId">The previously active tool, or null.</param>
/// <param name="NewToolId">The newly active tool, or null if deactivated.</param>
public record ToolChangedEventArgs(string? OldToolId, string? NewToolId);
