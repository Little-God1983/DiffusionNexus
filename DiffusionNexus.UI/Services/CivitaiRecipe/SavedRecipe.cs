namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// What <c>recipe.json</c> holds (#667): the recipe as loaded, when it was pasted, and whether the user keeps it.
/// </summary>
/// <param name="FormatVersion">The layout of this file, so a later version can still read an older one.</param>
/// <param name="PastedAt">When the image link was first pasted; a re-paste does not change it.</param>
/// <param name="Kept">Moves the recipe from History to Kept.</param>
public sealed record SavedRecipe(int FormatVersion, DateTimeOffset PastedAt, bool Kept, CivitaiImageRecipe Recipe)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// One recipe folder as found on disk. A folder whose <c>recipe.json</c> is missing or unreadable is
/// still listed (<see cref="IsBroken"/>) so it can be seen, counted and deleted.
/// </summary>
/// <param name="Saved">Null when the folder's recipe could not be read.</param>
/// <param name="SizeBytes">Every file in the folder: the original and the json.</param>
/// <param name="ReadError">Why <paramref name="Saved"/> is null.</param>
public sealed record StoredRecipe(long ImageId, string Folder, SavedRecipe? Saved, long SizeBytes, string? ReadError)
{
    public bool IsBroken => Saved is null;
}
