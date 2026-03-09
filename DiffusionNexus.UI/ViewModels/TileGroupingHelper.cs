using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Pure-logic helper that groups <see cref="Model"/> entities into
/// <see cref="ModelTileViewModel"/> tiles, collapsing re-discovery duplicates.
/// Extracted for testability.
/// </summary>
internal static class TileGroupingHelper
{
    /// <summary>
    /// Groups models so that different local files from the same LoRA appear as a single
    /// tile with multiple version buttons.
    /// <para>
    /// Grouping strategy (in priority order):
    /// <list type="number">
    ///   <item>By <c>CivitaiModelPageId</c> when set (preferred, future-proof).</item>
    ///   <item>By <c>Model.Name</c> as fallback (covers existing data where page ID is null).</item>
    /// </list>
    /// </para>
    /// Within each group, re-discovery duplicates (same filename) are collapsed — only the
    /// model with the richest metadata is kept per unique filename.
    /// </summary>
    internal static List<ModelTileViewModel> GroupModelsIntoTiles(IReadOnlyList<Model> allModels)
    {
        var tiles = new List<ModelTileViewModel>();

        // Phase 1: Group by CivitaiModelPageId (preferred key)
        var byPageId = allModels
            .Where(m => m.CivitaiModelPageId is not null)
            .GroupBy(m => m.CivitaiModelPageId!.Value);

        var consumed = new HashSet<int>(); // track model Ids already placed in a tile

        foreach (var group in byPageId)
        {
            var groupModels = group.ToList();
            var deduped = DeduplicateModels(groupModels);

            // Consume ALL original group members, not just the dedup survivors.
            // Dropped duplicates must never leak into Phase 2 as separate tiles.
            foreach (var m in groupModels)
                consumed.Add(m.Id);

            tiles.Add(deduped.Count == 1
                ? ModelTileViewModel.FromModel(deduped[0])
                : ModelTileViewModel.FromModelGroup(deduped));
        }

        // Phase 2: Group remaining models by Name (case-insensitive)
        var remaining = allModels
            .Where(m => !consumed.Contains(m.Id))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in remaining)
        {
            var groupModels = group.ToList();
            var deduped = DeduplicateModels(groupModels);

            foreach (var m in groupModels)
                consumed.Add(m.Id);

            tiles.Add(deduped.Count == 1
                ? ModelTileViewModel.FromModel(deduped[0])
                : ModelTileViewModel.FromModelGroup(deduped));
        }

        return tiles;
    }

    /// <summary>
    /// Collapses re-discovery duplicates within a group. Models whose primary file has the
    /// same filename are considered duplicates — only the model with the richest metadata is
    /// kept per unique filename.
    /// </summary>
    internal static List<Model> DeduplicateModels(List<Model> models)
    {
        if (models.Count <= 1)
            return models;

        // Key: primary filename (lowered). Value: best model for that file.
        var byFile = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            var fileName = model.Versions
                .SelectMany(v => v.Files)
                .Where(f => f.IsPrimary)
                .Select(f => f.FileName)
                .FirstOrDefault()
                ?? model.Versions
                    .SelectMany(v => v.Files)
                    .Select(f => f.FileName)
                    .FirstOrDefault()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                // No file info — keep it as-is (unique key)
                byFile[model.Id.ToString()] = model;
                continue;
            }

            if (byFile.TryGetValue(fileName, out var existing))
            {
                // Keep the one with richer data
                if (IsBetterModel(model, existing))
                    byFile[fileName] = model;
            }
            else
            {
                byFile[fileName] = model;
            }
        }

        return byFile.Values.ToList();
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> has richer metadata than <paramref name="current"/>.
    /// </summary>
    internal static bool IsBetterModel(Model candidate, Model current)
    {
        // Prefer the one with CivitaiId
        if (candidate.CivitaiId.HasValue && !current.CivitaiId.HasValue) return true;
        if (!candidate.CivitaiId.HasValue && current.CivitaiId.HasValue) return false;

        // Prefer more images
        var candidateImages = candidate.Versions.Sum(v => v.Images.Count);
        var currentImages = current.Versions.Sum(v => v.Images.Count);
        if (candidateImages != currentImages) return candidateImages > currentImages;

        // Prefer the one that was synced
        if (candidate.LastSyncedAt.HasValue && !current.LastSyncedAt.HasValue) return true;

        return false;
    }
}
