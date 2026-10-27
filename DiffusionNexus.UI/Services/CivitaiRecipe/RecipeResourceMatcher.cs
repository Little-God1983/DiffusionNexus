using System.Text.RegularExpressions;
using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>Which file entry belongs to which Civitai version, and the entries no version claimed.</summary>
internal sealed record RecipeMatchResult(IReadOnlyDictionary<int, RecipeLoraUse> ByVersionId, IReadOnlyList<RecipeLoraUse> Unlinked);

/// <summary>
/// Pairs the LoRAs an image file names with the resources Civitai lists for it (#628). A hash
/// decides first — compared against every hash kind of every file of the version, since image tools
/// write AutoV2, AutoV3 or SHA256_12 — then the file name without folders, extension, case, or a
/// download copy suffix (Civitai lists <c>AIM-KREA-V1 (1).safetensors</c> for a file the example's
/// graph calls <c>Krea2\Style\AIM-KREA-V1.safetensors</c>). Each side is used at most once.
/// </summary>
internal static partial class RecipeResourceMatcher
{
    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex CopySuffix();

    public static RecipeMatchResult Match(IReadOnlyList<CivitaiModelVersion> versions, IReadOnlyList<RecipeLoraUse> uses)
    {
        var byVersion = new Dictionary<int, RecipeLoraUse>();
        var left = uses.ToList();

        foreach (var use in uses.Where(u => !string.IsNullOrWhiteSpace(u.Hash)))
        {
            var version = versions.FirstOrDefault(v => !byVersion.ContainsKey(v.Id)
                && v.Files.Any(f => f.Hashes?.AllValues().Contains(use.Hash!, StringComparer.OrdinalIgnoreCase) == true));
            if (version is null) continue;
            byVersion[version.Id] = use;
            left.Remove(use);
        }

        foreach (var use in left.ToList())
        {
            var version = versions.FirstOrDefault(v => !byVersion.ContainsKey(v.Id)
                && v.Files.Any(f => Stem(f.Name).Equals(use.Name, StringComparison.OrdinalIgnoreCase)));
            if (version is null) continue;
            byVersion[version.Id] = use;
            left.Remove(use);
        }

        return new RecipeMatchResult(byVersion, left);
    }

    private static string Stem(string fileName) => CopySuffix().Replace(ComfyLoraScan.StemOf(fileName), "");
}
