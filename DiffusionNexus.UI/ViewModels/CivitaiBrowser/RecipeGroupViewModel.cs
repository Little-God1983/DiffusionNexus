namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>One date header of the Recipes tab with the cards it shows (#667).</summary>
public sealed class RecipeGroupViewModel(string header, IReadOnlyList<RecipeCardViewModel> cards)
{
    public string Header { get; } = header;
    public IReadOnlyList<RecipeCardViewModel> Cards { get; } = cards;

    /// <summary>How many recipes the group shows, not how many it holds.</summary>
    public string CountText => RecipesTabViewModel.Recipes(Cards.Count);
}
