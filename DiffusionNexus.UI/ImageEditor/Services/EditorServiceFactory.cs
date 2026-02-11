using DiffusionNexus.UI.ImageEditor.Events;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Factory for creating and wiring the editor service graph.
/// Creates all services with a shared <see cref="IEventBus"/> instance.
/// </summary>
public static class EditorServiceFactory
{
    /// <summary>
    /// Creates a complete set of editor services wired to a shared event bus.
    /// </summary>
    /// <returns>An <see cref="EditorServices"/> instance containing all services.</returns>
    public static EditorServices Create()
    {
        var eventBus = new EventBus();
        var viewportManager = new ViewportManager(eventBus);
        var toolManager = new ToolManager(eventBus);
        var documentService = new DocumentService(eventBus);
        var layerManager = new LayerManager(eventBus);

        return new EditorServices(eventBus, viewportManager, toolManager, documentService, layerManager);
    }
}

/// <summary>
/// Aggregates all editor services for dependency injection into the ViewModel.
/// </summary>
/// <param name="EventBus">The shared event bus.</param>
/// <param name="Viewport">Zoom, pan, and coordinate transforms.</param>
/// <param name="Tools">Tool activation and mutual exclusion.</param>
/// <param name="Document">Save, load, and export.</param>
/// <param name="Layers">Layer stack management.</param>
public record EditorServices(
    IEventBus EventBus,
    IViewportManager Viewport,
    IToolManager Tools,
    IDocumentService Document,
    ILayerManager Layers) : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        (Layers as IDisposable)?.Dispose();
    }
}
