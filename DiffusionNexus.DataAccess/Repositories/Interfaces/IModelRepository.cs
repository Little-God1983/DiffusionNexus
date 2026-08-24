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
    /// Loads a single <see cref="ModelImage"/> by id, <b>tracked</b> — the thumbnail step writes
    /// the row it gets back (<c>ThumbnailData</c>, <c>ThumbnailAttemptedAt</c>,
    /// <c>ThumbnailFailure</c>) and saves through the same unit of work.
    /// </summary>
    /// <remarks>
    /// Returns null when the row is gone, which is an ordinary outcome rather than an error: the
    /// step selects its work in one scope and executes each item in another, and a model can be
    /// deleted in between.
    /// </remarks>
    Task<ModelImage?> GetImageByIdAsync(int imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a single <see cref="ModelVersion"/> by id, <b>tracked</b> — the identify step's
    /// header/heuristic rungs write the base model straight onto the row they get back and save
    /// through the same unit of work.
    /// </summary>
    /// <remarks>
    /// Returns null when the row is gone, which is an ordinary outcome rather than an error: the
    /// step selects its work in one scope and executes each item in another, and a version can be
    /// deleted in between. Backed by <c>FindAsync</c>, which answers from the change tracker's
    /// identity map before it ever queries — on the path where a sidecar was found and parsed, the
    /// sidecar applier already pulled the whole model graph (<see cref="GetByIdWithIncludesAsync"/>)
    /// onto this same unit of work moments earlier, so this call costs nothing further there. No
    /// Includes here even so — do NOT widen <see cref="GetByIdWithIncludesAsync"/> for this instead:
    /// on the no-sidecar path nothing has necessarily been loaded yet, and that method always runs a
    /// query (five split queries) and drags in every version's image BLOBs, just to fetch one row by id.
    /// </remarks>
    Task<ModelVersion?> GetVersionByIdAsync(int versionId, CancellationToken cancellationToken = default);

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
    /// Finds a model by SHA256 hash on any of its files, with full includes. Case-insensitive.
    /// Used by the download path as a fallback when no model matches by
    /// <c>CivitaiModelPageId</c> — catches local-discovery rows that have the
    /// file on disk but no Civitai linkage yet, so we don't create a duplicate
    /// Model for the same file. Returns null if no match.
    /// </summary>
    Task<Model?> FindByFileHashAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether any model (other than <paramref name="excludeModelId"/>) already owns the given CivitaiId.
    /// </summary>
    Task<bool> IsCivitaiIdTakenAsync(int civitaiId, int excludeModelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether any model version (other than <paramref name="excludeVersionId"/>) already owns the given Civitai version ID.
    /// </summary>
    Task<bool> IsVersionCivitaiIdTakenAsync(int civitaiVersionId, int excludeVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the set of Civitai version IDs that are already installed locally
    /// (have a non-null <see cref="ModelVersion.CivitaiId"/> and at least one file with a local path).
    /// When <paramref name="allowedRootPaths"/> is non-null and non-empty, only files
    /// whose <c>LocalPath</c> sits under one of those roots are considered installed.
    /// Used by the Civitai browser's "Hide installed" filter to honor the user's
    /// enabled-source toggles in Settings.
    /// </summary>
    Task<HashSet<int>> GetInstalledCivitaiVersionIdsAsync(
        IReadOnlyList<string>? allowedRootPaths = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the set of SHA256 hashes (lowercase) of locally-valid model files —
    /// the same eligibility rule as <see cref="GetInstalledCivitaiVersionIdsAsync"/>
    /// (non-empty LocalPath and either IsLocalFileValid or never verified).
    /// Used by the Civitai browser as a fallback signal: when a local row's
    /// <see cref="ModelVersion.CivitaiId"/> is missing (e.g. orphan duplicate rows
    /// from past indexing bugs), a hash match against the API response still
    /// surfaces the model as installed.
    /// </summary>
    Task<HashSet<string>> GetInstalledFileHashesAsync(
        IReadOnlyList<string>? allowedRootPaths = null,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Updates only <see cref="Model.TotalVersionCount"/> and
    /// <see cref="Model.LastCheckedForUpdatesUtc"/> for the given model without
    /// loading the full entity graph. Targets every <see cref="Model"/> that
    /// shares the same <see cref="Model.CivitaiModelPageId"/> when the value is
    /// known so all grouped rows stay consistent.
    /// </summary>
    /// <param name="modelId">Primary <see cref="Model.Id"/> identifying the row.</param>
    /// <param name="totalVersionCount">Total versions returned by Civitai.</param>
    /// <param name="checkedAtUtc">Timestamp of this update check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows updated.</returns>
    Task<int> UpdateUpdateCheckMetadataAsync(
        int modelId,
        int totalVersionCount,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken = default);
}
