using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// What the Recipes tab (#667) needs from Browse Civitai: the installed library, cards for a
/// recipe's resources, the browser's own queue path, and its URL and API-key plumbing.
/// </summary>
public interface IRecipeHost
{
    CivitaiInstalledIndex Installed { get; }

    /// <summary>Raised after the installed index was replaced and applied to the browser's views.</summary>
    event EventHandler? InstalledIndexChanged;

    CivitaiResultViewModel CreateRecipeCard(CivitaiModel model);

    Task EnqueueRecipeAsync(List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs);

    void OpenUrl(string url);

    Task<string?> GetApiKeyAsync();
}
