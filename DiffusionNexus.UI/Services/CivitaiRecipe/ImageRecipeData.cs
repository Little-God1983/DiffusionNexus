namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// One LoRA an image file says it used.
/// </summary>
/// <param name="Name">File stem without folders and extension ("AIM-KREA-V1"), or the A1111 tag name.</param>
/// <param name="FileName">The file as the image wrote it (<c>Krea2\Style\AIM-KREA-V1.safetensors</c>); null for an A1111 tag.</param>
/// <param name="Weight">Model strength; null when the file does not say (never a default).</param>
/// <param name="Hash">The hash the settings text lists for it (AutoV2, AutoV3 or SHA256_12), or null.</param>
/// <param name="VersionId">The Civitai version id, when the file names it (Civitai's own generator does).</param>
public sealed record RecipeLoraUse(string Name, string? FileName, double? Weight, string? Hash, int? VersionId = null);

/// <summary>Where a recipe's prompt and settings came from.</summary>
public enum RecipeDataSource
{
    /// <summary>The file carries no generation data.</summary>
    None,

    /// <summary>The ComfyUI graph embedded in the file.</summary>
    ComfyUiWorkflow,

    /// <summary>The A1111/Forge "parameters" text.</summary>
    SettingsText,
}

/// <summary>
/// The generation data the recipe view shows (#628), read from the original image file. Every
/// value the file does not carry is null — the view says "unknown", it never fills in a default.
/// </summary>
public sealed record ImageRecipeData
{
    public RecipeFileFormat Format { get; init; }
    public RecipeDataSource Source { get; init; }

    /// <summary>One line saying where the data came from; null when <see cref="Source"/> is None.</summary>
    public string? SourceNote { get; init; }

    public string? Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public string? Checkpoint { get; init; }
    public string? Vae { get; init; }
    public string? Sampler { get; init; }
    public string? Scheduler { get; init; }
    public int? Steps { get; init; }
    public double? Cfg { get; init; }
    public long? Seed { get; init; }
    public IReadOnlyList<RecipeLoraUse> Loras { get; init; } = [];

    /// <summary>Set when something was there but could not be read (bad JSON, failed download).</summary>
    public string? ReadError { get; init; }

    public static ImageRecipeData Empty(RecipeFileFormat format, string? readError = null) =>
        new() { Format = format, Source = RecipeDataSource.None, ReadError = readError };
}
