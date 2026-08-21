using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// The single category-inference helper for the whole app (spec §3): an explicit user
/// override wins, otherwise the first tag that names a <see cref="CivitaiCategory"/>
/// (spaces→underscores, case-insensitive) is used. The Civitai download pipeline
/// (<c>CivitaiResultViewModel</c>, <c>DownloadLoraDialogViewModel</c>) delegates here
/// via <see cref="InferFolderName"/>, so a sorted library and freshly downloaded files
/// cannot drift into different folders.
/// </summary>
public static class SorterCategoryResolver
{
    public static CivitaiCategory Resolve(CivitaiCategory? userCategory, IEnumerable<string?> tagNames)
    {
        if (userCategory is { } explicitCategory && explicitCategory != CivitaiCategory.Unknown
            && Enum.IsDefined(explicitCategory))
            return explicitCategory;

        foreach (var tagName in tagNames)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var normalized = tagName.Replace(" ", "_").Trim();
            if (!LooksLikeCategoryName(normalized)) continue;
            if (Enum.TryParse<CivitaiCategory>(normalized, ignoreCase: true, out var category)
                && category != CivitaiCategory.Unknown
                && Enum.IsDefined(category))
            {
                return category;
            }
        }
        return CivitaiCategory.Unknown;
    }

    /// <summary>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> also accepts an enum's
    /// numeric form and comma-separated lists, which is harmless for a display label and
    /// actively destructive here, where the result names a directory files are physically
    /// moved into: the real Civitai tag <c>"2000"</c> parsed to the undefined value 2000
    /// and produced a folder literally called <c>2000</c>, <c>"5"</c> became Clothing, and
    /// <c>"character,style"</c> became Celebrity (1|2 == 3). Category names are plain
    /// identifiers, so a tag that does not start with a letter, or that carries a comma,
    /// is not one. <see cref="Enum.IsDefined{TEnum}(TEnum)"/> backs this up.
    /// </summary>
    private static bool LooksLikeCategoryName(string normalized)
        => normalized.Length > 0 && char.IsLetter(normalized[0]) && !normalized.Contains(',');

    public static CivitaiCategory ResolveForModel(Model model)
        => Resolve(model.UserCategory, model.Tags.Select(t => t.Tag?.Name));

    public static string ToFolderName(CivitaiCategory category)
        => category == CivitaiCategory.BaseModel ? "Base Model" : category.ToString();

    /// <summary>
    /// Folder name for a category, or <c>null</c> when it is unresolved — the download
    /// pipeline's contract, where a null category means "no category segment"
    /// (<c>DownloadDestinationViewModel.BuildTargetDirectory</c>).
    /// </summary>
    public static string? ToFolderNameOrNull(CivitaiCategory category)
        => category == CivitaiCategory.Unknown ? null : ToFolderName(category);

    /// <summary>
    /// Tag inference in the download pipeline's own shape: the folder name, or null when
    /// no tag names a category. The downloader VMs call this instead of keeping private
    /// copies, so the sorter and the downloader cannot disagree about where a file belongs.
    /// </summary>
    public static string? InferFolderName(IEnumerable<string?> tagNames)
        => ToFolderNameOrNull(Resolve(null, tagNames));
}
