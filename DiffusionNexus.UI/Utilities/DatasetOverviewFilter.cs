using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Pure filter behind the Dataset Management overview. Takes the grouped cards exactly as the
/// overview built them (one card per dataset in collapsed view, one card per version in flattened
/// view) and applies the text, type and NSFW filters.
/// </summary>
/// <remarks>
/// Besides deciding which cards are shown, it reports what the filters hid in two buckets so the
/// toolbar can say "1 dataset, 2 versions hidden" instead of miscounting a hidden version as a
/// hidden dataset, and it stamps <see cref="DatasetCardViewModel.HiddenVersionCount"/> on every
/// shown card so the card itself can say "1 NSFW version hidden".
///
/// Counting rule, per dataset (cards sharing a <see cref="DatasetCardViewModel.FolderPath"/>):
/// <list type="bullet">
/// <item>No card of the dataset survives the filters: one hidden dataset.</item>
/// <item>At least one card survives: every dropped card counts as a hidden version. A collapsed
/// card has no sibling cards to drop, so its NSFW-flagged versions count instead (the card silently
/// swaps to an older safe version when the current one is NSFW).</item>
/// </list>
/// </remarks>
public static class DatasetOverviewFilter
{
    public static DatasetOverviewFilterResult Apply(
        IEnumerable<DatasetGroupViewModel> groups,
        string? filterText,
        DatasetType? filterType,
        bool showNsfw)
    {
        var text = filterText?.Trim() ?? string.Empty;
        var hasTextFilter = text.Length > 0;

        var hiddenDatasets = 0;
        var hiddenVersions = 0;
        var result = new List<DatasetGroupViewModel>();

        foreach (var group in groups)
        {
            var shownCards = new List<DatasetCardViewModel>();

            foreach (var datasetCards in group.Datasets.GroupBy(c => c.FolderPath, StringComparer.OrdinalIgnoreCase))
            {
                var shownForDataset = new List<DatasetCardViewModel>();
                var dropped = 0;

                foreach (var card in datasetCards)
                {
                    var cardToShow = showNsfw ? card : card.GetSafeSnapshot();

                    if (cardToShow is null || !MatchesBasicFilters(cardToShow, text, hasTextFilter, filterType))
                    {
                        dropped++;
                        continue;
                    }

                    shownForDataset.Add(cardToShow);
                }

                if (shownForDataset.Count == 0)
                {
                    hiddenDatasets++;
                    continue;
                }

                var hiddenNsfwVersions = showNsfw ? 0 : datasetCards.First().NsfwVersionCount;
                foreach (var card in shownForDataset)
                {
                    card.HiddenVersionCount = hiddenNsfwVersions;
                }

                // Flattened: the dropped sibling cards are the hidden versions (NSFW or text/type).
                // Collapsed: nothing is dropped, but the NSFW-flagged versions are still unreachable.
                var collapsed = shownForDataset.Count == 1 && !shownForDataset[0].IsVersionCard;
                hiddenVersions += collapsed ? hiddenNsfwVersions : dropped;

                shownCards.AddRange(shownForDataset);
            }

            if (shownCards.Count == 0)
            {
                continue;
            }

            var filteredGroup = new DatasetGroupViewModel
            {
                CategoryId = group.CategoryId,
                Name = group.Name,
                Description = group.Description,
                SortOrder = group.SortOrder
            };
            foreach (var card in shownCards)
            {
                filteredGroup.Datasets.Add(card);
            }
            result.Add(filteredGroup);
        }

        return new DatasetOverviewFilterResult(result, hiddenDatasets, hiddenVersions);
    }

    private static bool MatchesBasicFilters(DatasetCardViewModel card, string text, bool hasTextFilter, DatasetType? filterType)
    {
        if (filterType.HasValue && card.Type != filterType)
        {
            return false;
        }

        if (!hasTextFilter)
        {
            return true;
        }

        return card.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
            || card.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>
/// Outcome of <see cref="DatasetOverviewFilter.Apply"/>: the groups to display plus what was hidden.
/// </summary>
public sealed record DatasetOverviewFilterResult(
    IReadOnlyList<DatasetGroupViewModel> Groups,
    int HiddenDatasetCount,
    int HiddenVersionCount)
{
    public bool HasHidden => HiddenDatasetCount > 0 || HiddenVersionCount > 0;

    /// <summary>
    /// Toolbar text, e.g. "1 dataset hidden", "2 versions hidden" or "1 dataset, 2 versions hidden".
    /// Empty when nothing is hidden.
    /// </summary>
    public string HiddenText
    {
        get
        {
            var parts = new List<string>(2);
            if (HiddenDatasetCount > 0)
            {
                parts.Add(HiddenDatasetCount == 1 ? "1 dataset" : $"{HiddenDatasetCount} datasets");
            }
            if (HiddenVersionCount > 0)
            {
                parts.Add(HiddenVersionCount == 1 ? "1 version" : $"{HiddenVersionCount} versions");
            }

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts) + " hidden";
        }
    }
}
