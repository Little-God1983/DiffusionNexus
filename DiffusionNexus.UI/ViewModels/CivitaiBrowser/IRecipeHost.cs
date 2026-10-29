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

    /// <summary>
    /// False until a snapshot of the library has been applied: before that <see cref="Installed"/> is empty,
    /// and judging against it would call every LoRA missing.
    /// </summary>
    bool IsInstalledLoaded { get; }

    /// <summary>Reads the library unless it has been read; concurrent calls share one read. Starts no search.</summary>
    Task EnsureInstalledLoadedAsync();

    /// <summary>Raised after the installed index was replaced and applied to the browser's views.</summary>
    event EventHandler? InstalledIndexChanged;

    CivitaiResultViewModel CreateRecipeCard(CivitaiModel model);

    Task EnqueueRecipeAsync(List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs);

    void OpenUrl(string url);

    Task<string?> GetApiKeyAsync();
}
