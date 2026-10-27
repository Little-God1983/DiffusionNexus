using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// One resource Civitai lists as used for the image, as far as it could be loaded.
/// </summary>
/// <param name="VersionId">The id from the image's <c>modelVersionIds</c>.</param>
/// <param name="Version">The version (the model's own entry when it lists it); null when Civitai has none.</param>
/// <param name="Model">The full model, for a browser card; null when it could not be loaded or is gone.</param>
/// <param name="Use">The image file's entry for it (weight, hash); null when none matched.</param>
/// <param name="LoadError">Why <paramref name="Model"/> is missing; null when it loaded.</param>
public sealed record CivitaiRecipeResource(
    int VersionId, CivitaiModelVersion? Version, CivitaiModel? Model, RecipeLoraUse? Use, string? LoadError);

/// <summary>
/// Everything the recipe view shows for one Civitai image (#628). The rating is Civitai's — the
/// images call's <c>browsingLevel</c> — judged by <see cref="NsfwPolicy"/>, so a recipe is SFW or
/// NSFW by the same rule as every other picture in the app.
/// </summary>
/// <param name="Rating">Civitai's bitmask (1 PG, 2 PG-13, 4 R, 8 X, 16 XXX); null when unrated.</param>
/// <param name="OriginalFilePath">The cached original file; null for a video or a failed download.</param>
/// <param name="UnlinkedLoras">LoRAs the file names that match no listed resource (private or deleted).</param>
public sealed record CivitaiImageRecipe(
    long ImageId,
    CivitaiModelImage Image,
    int? Rating,
    string? OriginalFilePath,
    ImageRecipeData Data,
    IReadOnlyList<CivitaiRecipeResource> Resources,
    IReadOnlyList<RecipeLoraUse> UnlinkedLoras)
{
    /// <summary>PG / PG-13. Unrated counts as NSFW, as everywhere in the app.</summary>
    public bool IsSafe => NsfwPolicy.IsSafe(Rating);

    /// <summary>The strongest level, as Civitai names it ("PG-13", "X", "unrated").</summary>
    public string RatingLabel => NsfwPolicy.RatingLabel((NsfwLevel)(Rating ?? 0));

    /// <summary>The image page; NSFW images live on civitai.red.</summary>
    public string CivitaiUrl => $"https://{(IsSafe ? "civitai.com" : "civitai.red")}/images/{ImageId}";
}

/// <summary>Result of <see cref="CivitaiImageRecipeService.LoadAsync"/>: the recipe, or why there is none.</summary>
public sealed record CivitaiImageRecipeLoadResult(CivitaiImageRecipe? Recipe, string? Message);
