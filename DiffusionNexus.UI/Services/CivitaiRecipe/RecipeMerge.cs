namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// What a re-paste does to a saved recipe (#667): the fresh load wins, except where it is worse than
/// what is already saved (a transient failure, a missing weight, no API key this time, no file data).
/// </summary>
public static class RecipeMerge
{
    public static CivitaiImageRecipe Refresh(CivitaiImageRecipe saved, CivitaiImageRecipe fresh)
    {
        var savedById = new Dictionary<int, CivitaiRecipeResource>();
        foreach (var r in saved.Resources) savedById.TryAdd(r.VersionId, r);

        var kept = new List<CivitaiRecipeResource>();
        var resources = new List<CivitaiRecipeResource>();
        foreach (var f in fresh.Resources)
        {
            var merged = f;
            if (savedById.TryGetValue(f.VersionId, out var s))
            {
                if (f.LoadError is not null && !f.IsDefiniteFailure && s.LoadError is null)
                {
                    merged = s;
                    kept.Add(s);
                }
                else if (!f.IsDefiniteFailure && f.Use?.Weight is null && s.Use?.Weight is not null)
                {
                    merged = f with { Use = s.Use };
                    kept.Add(merged);
                }
            }
            resources.Add(merged);
        }

        // Without the API key the public list can be shorter than the page's own list.
        if (!fresh.HasApiKey && saved.HasApiKey)
        {
            var present = resources.Select(r => r.VersionId).ToHashSet();
            foreach (var s in saved.Resources)
            {
                if (!present.Add(s.VersionId)) continue;
                resources.Add(s);
                kept.Add(s);
            }
        }

        var keepData = fresh.Data.Source == RecipeDataSource.None && saved.Data.Source != RecipeDataSource.None;
        var data = keepData ? saved.Data with { Format = fresh.Data.Format } : fresh.Data;

        var keptNames = kept.Select(r => r.Use?.Name).Where(n => n is not null).ToHashSet();
        // The unlinked LoRAs are derived from the file data, so they follow whichever data won.
        var unlinked = (keepData ? saved.UnlinkedLoras : fresh.UnlinkedLoras).Where(l => !keptNames.Contains(l.Name)).ToList();

        // Key-only resources kept above stay in the recipe, so the next keyless paste must keep them too.
        return fresh with { Resources = resources, Data = data, UnlinkedLoras = unlinked, HasApiKey = fresh.HasApiKey || saved.HasApiKey };
    }
}
