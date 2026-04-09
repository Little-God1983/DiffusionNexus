using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="Model"/> entities with domain-specific query methods.
/// </summary>
public interface IModelRepository : IRepository<Model>
{
    /// <summary>
    /// Loads all models with their full navigation graph (Versions, Files, Images, TriggerWords, Creator)
    /// that have at least one file with a non-empty local path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Models with local files, fully populated.</returns>
    Task<IReadOnlyList<Model>> GetModelsWithLocalFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all models with their full navigation graph using a split query for performance.
    /// WARNING: Includes ThumbnailData BLOBs. For large model counts, prefer the Light variants.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All models with related entities.</returns>
    Task<IReadOnlyList<Model>> GetAllWithIncludesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads models with local files, excluding <c>ThumbnailData</c> BLOBs from images.
    /// Image metadata (URLs, dimensions, sort order, etc.) is still loaded — only the
    /// heavy BLOB column is omitted to keep memory usage safe at scale (11K+ models).
    /// </summary>
    Task<IReadOnlyList<Model>> GetModelsWithLocalFilesLightAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a single image's ThumbnailData from the database.
    /// Used for on-demand lazy loading when a tile scrolls into view.
    /// </summary>
    Task<(byte[]? Data, string? MimeType)> GetImageThumbnailDataAsync(int imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a single model by ID with its full navigation graph.
    /// Much more memory-efficient than <see cref="GetAllWithIncludesAsync"/> when only one model is needed.
    /// </summary>
    Task<Model?> GetByIdWithIncludesAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the first model with the given <c>CivitaiModelPageId</c>, with full includes.
    /// Returns null if none found. Falls back to <paramref name="fallbackModelId"/> if provided.
    /// </summary>
    Task<Model?> FindByModelPageIdOrIdAsync(int? modelPageId, int? fallbackModelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a model by matching a local file path on any of its version files, with full includes.
    /// Returns null if no match.
    /// </summary>
    Task<Model?> FindByLocalFilePathAsync(string localFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether any model (other than <paramref name="excludeModelId"/>) already owns the given CivitaiId.
    /// </summary>
    Task<bool> IsCivitaiIdTakenAsync(int civitaiId, int excludeModelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether any model version (other than <paramref name="excludeVersionId"/>) already owns the given Civitai version ID.
    /// </summary>
    Task<bool> IsVersionCivitaiIdTakenAsync(int civitaiVersionId, int excludeVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an existing <see cref="Creator"/> by username (case-insensitive) so it can be reused
    /// instead of creating a duplicate row.
    /// </summary>
    Task<Creator?> FindCreatorByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all distinct <see cref="Tag"/> entities currently in the database, keyed by NormalizedName.
    /// Used to reuse existing Tag rows when syncing tags from Civitai.
    /// </summary>
    Task<Dictionary<string, Tag>> GetAllTagsLookupAsync(CancellationToken cancellationToken = default);
}
