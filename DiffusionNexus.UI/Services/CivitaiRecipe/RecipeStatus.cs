using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// What a recipe card says about the LoRAs it needs (#667). A resource that could not be loaded
/// can never be judged, so it blocks Ready: a card must not say Ready while a LoRA is missing.
/// </summary>
/// <param name="Loras">LoRAs the recipe uses, listed or unlinked.</param>
/// <param name="Missing">Of those, the ones not in the library.</param>
/// <param name="MissingNotOnCivitai">Of the missing, the ones Civitai has no page for (unlinked).</param>
/// <param name="FailedToLoad">Resources whose model could not be loaded, so their type is unknown.</param>
public sealed record RecipeStatus(int Loras, int Missing, int MissingNotOnCivitai, int FailedToLoad)
{
    /// <summary>Every LoRA is installed and nothing is unchecked.</summary>
    public bool IsReady => Missing == 0 && FailedToLoad == 0 && Loras > 0;

    /// <summary>Something is missing or unchecked; a recipe without LoRAs is neither Ready nor Missing.</summary>
    public bool IsMissing => !IsReady && !(Loras == 0 && FailedToLoad == 0);

    /// <summary>The card's status line.</summary>
    public string Text
    {
        get
        {
            if (IsReady) return "Ready";
            var parts = new List<string>();
            if (Missing > 0)
            {
                var text = $"{Missing} of {Loras} LoRAs missing";
                if (MissingNotOnCivitai > 0) text += $" ({MissingNotOnCivitai} not on Civitai)";
                parts.Add(text);
            }
            if (FailedToLoad > 0)
                parts.Add(FailedToLoad == 1 ? "1 resource could not be loaded" : $"{FailedToLoad} resources could not be loaded");
            return parts.Count > 0 ? string.Join(" · ", parts) : "No LoRAs";
        }
    }

    /// <summary>Judges <paramref name="recipe"/> against what the library holds.</summary>
    public static RecipeStatus Compute(CivitaiImageRecipe recipe, CivitaiInstalledIndex installed)
    {
        int loras = 0, missing = 0, notOnCivitai = 0, failed = 0;
        foreach (var resource in recipe.Resources)
        {
            if (resource.LoadError is not null) { failed++; continue; }
            if (resource.Model is null || !CivitaiRecipeResourceRowViewModel.LoraTypes.Contains(resource.Model.Type)) continue;
            loras++;
            if (!installed.IsInstalled(resource.Version)) missing++;
        }
        foreach (var use in recipe.UnlinkedLoras)
        {
            loras++;
            if (installed.HasFileHashPrefix(use.Hash)) continue;
            missing++;
            // While a resource failed to load, an unlinked entry may be that very LoRA: it is on Civitai.
            if (failed == 0) notOnCivitai++;
        }
        return new RecipeStatus(loras, missing, notOnCivitai, failed);
    }
}
