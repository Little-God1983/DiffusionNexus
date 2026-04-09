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
}
