using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Client-side re-sort for Civitai browser results. The REST API silently ignores
/// the <c>sort=</c> parameter whenever <c>query=</c> is present — results arrive in
/// Meilisearch relevance order — so the browser re-sorts the fetched pages locally.
/// </summary>
public static class CivitaiSearchResultSorter
{
    /// <summary>
    /// Orders <paramref name="items"/> by the given Civitai sort option using the
    /// data already present on each model. Unknown sort values return the incoming
    /// order unchanged; ties keep the incoming (relevance) order — OrderByDescending
    /// is a stable sort. Items with a null model sort last.
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> items,
        Func<T, CivitaiModel?> modelSelector,
        string? sort)
    {
        var list = items.ToList();

        return sort switch
        {
            CivitaiModelSort.Newest => list
                .OrderByDescending(i => LatestPublishedAt(modelSelector(i)))
                .ToList(),
            CivitaiModelSort.MostDownloaded => list
                .OrderByDescending(i => modelSelector(i)?.Stats?.DownloadCount ?? 0)
                .ToList(),
            CivitaiModelSort.HighestRated => list
                .OrderByDescending(i => modelSelector(i)?.Stats?.ThumbsUpCount ?? 0)
                .ToList(),
            _ => list
        };
    }

    /// <summary>
    /// Reorders <paramref name="collection"/> in place to match <paramref name="desiredOrder"/>
    /// using Move operations, so bound item containers (and their selection state) survive
    /// instead of being rebuilt by a clear-and-re-add. An already-ordered collection is untouched.
    /// </summary>
    public static void ApplyOrder<T>(
        System.Collections.ObjectModel.ObservableCollection<T> collection,
        IReadOnlyList<T> desiredOrder)
    {
        for (var target = 0; target < desiredOrder.Count; target++)
        {
            var current = collection.IndexOf(desiredOrder[target]);
            if (current >= 0 && current != target) collection.Move(current, target);
        }
    }

    private static DateTimeOffset LatestPublishedAt(CivitaiModel? model)
    {
        if (model is null || model.ModelVersions.Count == 0) return DateTimeOffset.MinValue;

        var latest = DateTimeOffset.MinValue;
        foreach (var version in model.ModelVersions)
        {
            if (version.PublishedAt is { } published && published > latest) latest = published;
        }
        return latest;
    }
}
