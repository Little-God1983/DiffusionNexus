using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Resolves the sorting category for a model using the same rules as the
/// Civitai download pipeline (CivitaiResultViewModel.InferCategoryFromTags):
/// an explicit user override wins, otherwise the first tag that parses to a
/// CivitaiCategory value (spaces→underscores, case-insensitive) is used, so a
/// sorted library and freshly downloaded files land in identical folders.
/// </summary>
public static class SorterCategoryResolver
{
    public static CivitaiCategory Resolve(CivitaiCategory? userCategory, IEnumerable<string?> tagNames)
    {
        if (userCategory is { } explicitCategory && explicitCategory != CivitaiCategory.Unknown)
            return explicitCategory;

        foreach (var tagName in tagNames)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var normalized = tagName.Replace(" ", "").Trim();
            if (Enum.TryParse<CivitaiCategory>(normalized, ignoreCase: true, out var category)
                && category != CivitaiCategory.Unknown)
            {
                return category;
            }
        }
        return CivitaiCategory.Unknown;
    }

    public static CivitaiCategory ResolveForModel(Model model)
        => Resolve(model.UserCategory, model.Tags.Select(t => t.Tag?.Name));

    public static string ToFolderName(CivitaiCategory category)
        => category == CivitaiCategory.BaseModel ? "Base Model" : category.ToString();
}
