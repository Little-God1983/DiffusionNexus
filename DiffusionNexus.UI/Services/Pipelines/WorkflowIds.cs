namespace DiffusionNexus.UI.Services.Pipelines;

/// <summary>
/// The ids of the built-in Workflows (pipelines), as their manifests declare them and as the
/// "Send To → Workflows" menu passes them. One spelling for every caller: a check against a
/// misspelt copy fails open.
/// </summary>
public static class WorkflowIds
{
    public const string AnimeToReal = "anime-to-real";
    public const string QwenImage2512 = "qwen-image-2512";
    public const string ImageToImage = "image-to-image";
    public const string BatchMetadataDistiller = "batch-metadata-distiller";
}
