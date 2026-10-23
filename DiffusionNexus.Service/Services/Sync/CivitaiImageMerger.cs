using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>What one <see cref="CivitaiImageMerger.Merge"/> call changed on a version.</summary>
/// <param name="Added">Images Civitai listed that the version did not hold.</param>
/// <param name="Rated">Images already held whose rating was filled in or changed.</param>
/// <param name="Removed">Duplicate copies of a URL that were dropped.</param>
public readonly record struct ImageMergeResult(int Added, int Rated, int Removed)
{
    /// <summary>Whether anything needs saving.</summary>
    public bool Changed => Added + Rated + Removed > 0;
}

/// <summary>
/// The one way Civitai image records land on a <see cref="ModelVersion"/> (#631): matched by URL,
/// an image already held gets Civitai's rating, a new one is added with it, and duplicate copies of
/// a URL are collapsed. Every writer of Civitai images goes through here — the sync, the download
/// and the detail view — so they cannot drift apart again.
/// </summary>
/// <remarks>
/// The match used to be by <see cref="ModelImage.CivitaiId"/>, but the API stopped sending image
/// ids, so every forced re-fetch appended the whole gallery again and libraries collected up to
/// nineteen copies of one picture. A Civitai id still matches when one is present. Duplicates are
/// removed from the navigation collection; the relationship is required, so EF deletes the orphans
/// on save.
/// </remarks>
public static class CivitaiImageMerger
{
    /// <summary>Merges <paramref name="civitaiImages"/> into <paramref name="dbVersion"/>'s images.</summary>
    public static ImageMergeResult Merge(ModelVersion dbVersion, IReadOnlyList<CivitaiModelImage> civitaiImages)
    {
        var removed = RemoveDuplicateCopies(dbVersion);

        var byUrl = dbVersion.Images
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .ToDictionary(i => i.Url, StringComparer.OrdinalIgnoreCase);
        var byCivitaiId = dbVersion.Images
            .Where(i => i.CivitaiId.HasValue)
            .GroupBy(i => i.CivitaiId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var sortOrder = dbVersion.Images.Count == 0 ? 0 : dbVersion.Images.Max(i => i.SortOrder) + 1;
        var added = 0;
        var rated = 0;

        foreach (var civImage in civitaiImages)
        {
            if (string.IsNullOrEmpty(civImage.Url)) continue;

            var level = (NsfwLevel)(civImage.NsfwLevel ?? 0);

            if (byUrl.TryGetValue(civImage.Url, out var existing)
                || (civImage.Id.HasValue && byCivitaiId.TryGetValue(civImage.Id.Value, out existing)))
            {
                // An unrated answer is no answer: it never erases a rating already stored.
                if (level != NsfwLevel.None && existing.NsfwLevel != level)
                {
                    existing.NsfwLevel = level;
                    rated++;
                }
                continue;
            }

            var image = new ModelImage
            {
                ModelVersionId = dbVersion.Id,
                CivitaiId = civImage.Id,
                Url = civImage.Url,
                MediaType = civImage.Type,
                NsfwLevel = level,
                Width = civImage.Width ?? 0,
                Height = civImage.Height ?? 0,
                BlurHash = civImage.Hash,
                SortOrder = sortOrder++,
                CreatedAt = civImage.CreatedAt,
                PostId = civImage.PostId,
                Username = civImage.Username,
                Prompt = civImage.Meta?.Prompt,
                NegativePrompt = civImage.Meta?.NegativePrompt,
                Seed = civImage.Meta?.Seed,
                Steps = civImage.Meta?.Steps,
                Sampler = civImage.Meta?.Sampler,
                CfgScale = civImage.Meta?.CfgScale,
            };
            dbVersion.Images.Add(image);
            byUrl[image.Url] = image;
            added++;
        }

        return new ImageMergeResult(added, rated, removed);
    }

    /// <summary>
    /// Keeps one row per URL: the copy that holds a thumbnail (loaded or deferred), else the first
    /// by sort order and id. Returns how many copies were dropped.
    /// </summary>
    private static int RemoveDuplicateCopies(ModelVersion dbVersion)
    {
        var extras = dbVersion.Images
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .GroupBy(i => i.Url, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g
                .OrderByDescending(i => i.HasThumbnail || i.IsThumbnailDeferred)
                .ThenBy(i => i.SortOrder)
                .ThenBy(i => i.Id)
                .Skip(1))
            .ToList();

        foreach (var extra in extras) dbVersion.Images.Remove(extra);
        return extras.Count;
    }
}
