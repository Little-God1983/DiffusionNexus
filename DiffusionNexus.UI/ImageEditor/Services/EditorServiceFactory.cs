namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Factory for creating and wiring the editor service graph.
/// </summary>
public static class EditorServiceFactory
{
    /// <summary>
    /// Creates a complete set of editor services.
    /// </summary>
    /// <returns>An <see cref="EditorServices"/> instance containing all services.</returns>
    public static EditorServices Create()
    {
        var viewportManager = new ViewportManager();
        var toolManager = new ToolManager();
        var documentService = new DocumentService();
        var layerManager = new LayerManager();

        return new EditorServices(viewportManager, toolManager, documentService, layerManager);
    }
}

/// <summary>
/// Aggregates all editor services for dependency injection into the ViewModel.
/// </summary>
/// <param name="Viewport">Zoom, pan, and coordinate transforms.</param>
/// <param name="Tools">Tool activation and mutual exclusion.</param>
/// <param name="Document">Save, load, and export.</param>
/// <param name="Layers">Layer stack management.</param>
public record EditorServices(
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
