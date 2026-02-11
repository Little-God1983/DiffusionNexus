using System.Collections.Concurrent;
using DiffusionNexus.UI.ImageEditor.Events;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Manages tool activation with mutual exclusion.
/// Publishes <see cref="ToolChangedEvent"/> and <see cref="ToolPanelToggledEvent"/> via the event bus.
/// </summary>
internal sealed class ToolManager : IToolManager
{
    private readonly IEventBus _eventBus;
    private readonly ConcurrentDictionary<string, Action> _deactivationCallbacks = new();
    private string? _activeToolId;

    public ToolManager(IEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        _eventBus = eventBus;
    }

    /// <inheritdoc />
    public string? ActiveToolId => _activeToolId;

    /// <inheritdoc />
    public void Activate(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        var oldTool = _activeToolId;
        if (oldTool == toolId) return;

        // Deactivate the current tool
        if (oldTool is not null)
        {
            InvokeDeactivation(oldTool);
            _eventBus.Publish(new ToolPanelToggledEvent(oldTool, false));
        }

        _activeToolId = toolId;
        _eventBus.Publish(new ToolPanelToggledEvent(toolId, true));
        _eventBus.Publish(new ToolChangedEvent(oldTool, toolId));
        ActiveToolChanged?.Invoke(this, new ToolChangedEventArgs(oldTool, toolId));
    }

    /// <inheritdoc />
    public void Deactivate(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        if (_activeToolId != toolId) return;

        InvokeDeactivation(toolId);
        _activeToolId = null;
        _eventBus.Publish(new ToolPanelToggledEvent(toolId, false));
        _eventBus.Publish(new ToolChangedEvent(toolId, null!));
        ActiveToolChanged?.Invoke(this, new ToolChangedEventArgs(toolId, null));
    }

    /// <inheritdoc />
    public void Toggle(string toolId)
    {
        if (IsActive(toolId))
            Deactivate(toolId);
        else
            Activate(toolId);
    }

    /// <inheritdoc />
    public void DeactivateAll()
    {
        if (_activeToolId is null) return;
        Deactivate(_activeToolId);
    }

    /// <inheritdoc />
    public bool IsActive(string toolId) => _activeToolId == toolId;

    /// <inheritdoc />
    public void RegisterDeactivationCallback(string toolId, Action onDeactivate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(onDeactivate);
        _deactivationCallbacks[toolId] = onDeactivate;
    }

    /// <inheritdoc />
    public event EventHandler<ToolChangedEventArgs>? ActiveToolChanged;

    private void InvokeDeactivation(string toolId)
    {
        if (_deactivationCallbacks.TryGetValue(toolId, out var callback))
        {
            callback();
        }
    }
}
