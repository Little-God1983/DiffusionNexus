using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Repository for <see cref="Model"/> entities.
/// </summary>
internal sealed class ModelRepository : RepositoryBase<Model>, IModelRepository
{
    public ModelRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetModelsWithLocalFilesAsync(CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .Where(m => m.Versions.Any(v => v.Files.Any(f => f.LocalPath != null && f.LocalPath != "")))
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetAllWithIncludesAsync(CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> GetModelsWithLocalFilesLightAsync(CancellationToken cancellationToken = default)
    {
        // Step 1: Load models with everything EXCEPT images (the heavy part)
        var models = await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .Where(m => m.Versions.Any(v => v.Files.Any(f => f.LocalPath != null && f.LocalPath != "")))
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (models.Count == 0) return models;

        // Step 2: Load image METADATA only (no ThumbnailData BLOB) via projection
        var versionIds = models
            .SelectMany(m => m.Versions)
            .Select(v => v.Id)
            .ToHashSet();

        var images = await Context.ModelImages
            .Where(i => versionIds.Contains(i.ModelVersionId))
            .AsNoTracking()
            .Select(i => new
            {
                i.Id,
                i.ModelVersionId,
                i.CivitaiId,
                i.Url,
                i.MediaType,
                i.IsNsfw,
                i.NsfwLevel,
                i.Width,
                i.Height,
                i.BlurHash,
                i.SortOrder,
                i.CreatedAt,
                i.PostId,
                i.Username,
                i.ThumbnailMimeType,
                i.ThumbnailWidth,
                i.ThumbnailHeight,
                i.LocalCachePath,
                i.IsLocalCacheValid,
                i.CachedAt,
                i.CachedFileSize,
                i.Prompt,
                i.NegativePrompt,
                i.Seed,
                i.Steps,
                i.Sampler,
                i.CfgScale,
                i.GenerationModel,
                i.DenoisingStrength,
                i.LikeCount,
                i.HeartCount,
                i.CommentCount,
                // ThumbnailData deliberately EXCLUDED — loaded per-tile on demand
                HasThumbnailInDb = i.ThumbnailData != null && i.ThumbnailData.Length > 0,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Step 3: Map projections into real ModelImage entities and attach to versions
        var imagesByVersion = images
            .GroupBy(i => i.ModelVersionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var version in models.SelectMany(m => m.Versions))
        {
            if (!imagesByVersion.TryGetValue(version.Id, out var versionImages)) continue;
            foreach (var i in versionImages)
            {
                version.Images.Add(new ModelImage
                {
                    Id = i.Id,
                    ModelVersionId = i.ModelVersionId,
                    CivitaiId = i.CivitaiId,
                    Url = i.Url,
                    MediaType = i.MediaType,
                    IsNsfw = i.IsNsfw,
                    NsfwLevel = i.NsfwLevel,
                    Width = i.Width,
                    Height = i.Height,
                    BlurHash = i.BlurHash,
                    SortOrder = i.SortOrder,
                    CreatedAt = i.CreatedAt,
                    PostId = i.PostId,
                    Username = i.Username,
                    ThumbnailMimeType = i.ThumbnailMimeType,
                    ThumbnailWidth = i.ThumbnailWidth,
                    ThumbnailHeight = i.ThumbnailHeight,
                    LocalCachePath = i.LocalCachePath,
                    IsLocalCacheValid = i.IsLocalCacheValid,
                    CachedAt = i.CachedAt,
                    CachedFileSize = i.CachedFileSize,
                    Prompt = i.Prompt,
                    NegativePrompt = i.NegativePrompt,
                    Seed = i.Seed,
                    Steps = i.Steps,
                    Sampler = i.Sampler,
                    CfgScale = i.CfgScale,
                    GenerationModel = i.GenerationModel,
                    DenoisingStrength = i.DenoisingStrength,
                    LikeCount = i.LikeCount,
                    HeartCount = i.HeartCount,
                    CommentCount = i.CommentCount,
                    // Signal to the tile that a thumbnail exists in DB but wasn't loaded
                    ThumbnailData = i.HasThumbnailInDb ? ModelImage.ThumbnailNotLoadedSentinel : null,
                });
            }
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<(byte[]? Data, string? MimeType)> GetImageThumbnailDataAsync(int imageId, CancellationToken cancellationToken = default)
    {
        var result = await Context.ModelImages
            .Where(i => i.Id == imageId)
            .Select(i => new { i.ThumbnailData, i.ThumbnailMimeType })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return (result?.ThumbnailData, result?.ThumbnailMimeType);
    }

    /// <inheritdoc />
    public async Task<Model?> GetByIdWithIncludesAsync(int id, CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
                .ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Model?> FindByModelPageIdOrIdAsync(int? modelPageId, int? fallbackModelId, CancellationToken cancellationToken = default)
    {
        var query = Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags).ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions).ThenInclude(v => v.Files)
            .Include(m => m.Versions).ThenInclude(v => v.Images)
            .Include(m => m.Versions).ThenInclude(v => v.TriggerWords)
            .AsSplitQuery();

        Model? model = null;
        if (modelPageId.HasValue)
            model = await query.FirstOrDefaultAsync(m => m.CivitaiModelPageId == modelPageId.Value, cancellationToken).ConfigureAwait(false);

        if (model is null && fallbackModelId.HasValue)
            model = await query.FirstOrDefaultAsync(m => m.Id == fallbackModelId.Value, cancellationToken).ConfigureAwait(false);

        return model;
    }

    /// <inheritdoc />
    public async Task<Model?> FindByLocalFilePathAsync(string localFilePath, CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags).ThenInclude(mt => mt.Tag)
            .Include(m => m.Versions).ThenInclude(v => v.Files)
            .Include(m => m.Versions).ThenInclude(v => v.Images)
            .Include(m => m.Versions).ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .FirstOrDefaultAsync(
                m => m.Versions.Any(v => v.Files.Any(f =>
                    f.LocalPath != null && f.LocalPath.ToLower() == localFilePath.ToLower())),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsCivitaiIdTakenAsync(int civitaiId, int excludeModelId, CancellationToken cancellationToken = default)
    {
        return await Context.Models
            .AnyAsync(m => m.CivitaiId == civitaiId && m.Id != excludeModelId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsVersionCivitaiIdTakenAsync(int civitaiVersionId, int excludeVersionId, CancellationToken cancellationToken = default)
    {
        return await Context.ModelVersions
            .AnyAsync(v => v.CivitaiId == civitaiVersionId && v.Id != excludeVersionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HashSet<int>> GetInstalledCivitaiVersionIdsAsync(
        IReadOnlyList<string>? allowedRootPaths = null,
        CancellationToken cancellationToken = default)
    {
        // No root filter → fast SQL distinct path-only.
        if (allowedRootPaths is null || allowedRootPaths.Count == 0)
        {
            var ids = await Context.ModelVersions
                .Where(v => v.CivitaiId != null
                            && v.Files.Any(f => f.LocalPath != null && f.LocalPath != ""
                                && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null)))
                .Select(v => v.CivitaiId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return new HashSet<int>(ids);
        }

        // Root filter: pull (civitaiId, file paths) pairs and check StartsWith in C#.
        // EF Core can't translate a runtime OR-of-StartsWith list cleanly, and the
        // result-set is small (one row per installed version).
        // Issue #380: also exclude files explicitly verified as missing
        // (IsLocalFileValid=false with a non-null verification timestamp).
        var rows = await Context.ModelVersions
            .Where(v => v.CivitaiId != null
                        && v.Files.Any(f => f.LocalPath != null && f.LocalPath != ""
                            && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null)))
            .Select(v => new
            {
                CivitaiId = v.CivitaiId!.Value,
                Paths = v.Files
                    .Where(f => f.LocalPath != null
                        && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null))
                    .Select(f => f.LocalPath!)
                    .ToList()
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var normalizedRoots = allowedRootPaths
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        var result = new HashSet<int>();
        foreach (var row in rows)
        {
            foreach (var path in row.Paths)
            {
                if (PathIsUnderAnyRoot(path, normalizedRoots))
                {
                    result.Add(row.CivitaiId);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="filePath"/> is exactly one of, or sits
    /// inside any of, the given normalized roots. Case-insensitive; ensures that
    /// <c>C:\Foo\Bar</c> doesn't match the root <c>C:\Foo</c> only because the
    /// strings share a prefix (we require a separator boundary).
    /// </summary>
    private static bool PathIsUnderAnyRoot(string filePath, IReadOnlyList<string> normalizedRoots)
    {
        foreach (var root in normalizedRoots)
        {
            if (filePath.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (filePath.Length > root.Length
                && filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (filePath[root.Length] == Path.DirectorySeparatorChar
                    || filePath[root.Length] == Path.AltDirectorySeparatorChar))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<Creator?> FindCreatorByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        return await Context.Creators
            .FirstOrDefaultAsync(c => c.Username.ToLower() == username.ToLower(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, Tag>> GetAllTagsLookupAsync(CancellationToken cancellationToken = default)
    {
        return await Context.Tags
            .ToDictionaryAsync(t => t.NormalizedName, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpdateUpdateCheckMetadataAsync(
        int modelId,
        int totalVersionCount,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // Look up the model's CivitaiModelPageId so all grouped rows stay in sync.
        // Falls back to a single-row update when the page id is unknown.
        var pageId = await Context.Models
            .Where(m => m.Id == modelId)
            .Select(m => m.CivitaiModelPageId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var query = pageId.HasValue
            ? Context.Models.Where(m => m.CivitaiModelPageId == pageId.Value)
            : Context.Models.Where(m => m.Id == modelId);

        return await query
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.TotalVersionCount, totalVersionCount)
                .SetProperty(m => m.LastCheckedForUpdatesUtc, checkedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
