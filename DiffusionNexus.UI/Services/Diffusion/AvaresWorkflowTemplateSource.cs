using Avalonia.Platform;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Loads the API-format workflow template embedded as an Avalonia resource under
/// <c>Assets/Pipelines/</c> — the same mechanism <c>PipelineManifestProvider</c> uses for its
/// manifests. Not unit-tested: it is a thin adapter over <see cref="AssetLoader"/>, which needs
/// an initialized Avalonia runtime. Consumers depend on <see cref="IWorkflowTemplateSource"/>
/// and are tested against a stub.
/// </summary>
public sealed class AvaresWorkflowTemplateSource : IWorkflowTemplateSource
{
    private readonly Uri _uri;

    public AvaresWorkflowTemplateSource(string assetFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetFileName);
        _uri = new Uri($"avares://DiffusionNexus.UI/Assets/Pipelines/{assetFileName}");
    }

    public bool HasTemplate
    {
        get
        {
            try { return AssetLoader.Exists(_uri); }
            catch { return false; }
        }
    }

    public string? LoadTemplateJson()
    {
        if (!HasTemplate) return null;

        using var stream = AssetLoader.Open(_uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
