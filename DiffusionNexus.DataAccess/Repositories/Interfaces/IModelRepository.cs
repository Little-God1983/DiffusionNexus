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

    /// <summary>
    /// Local rows still carrying discovery's old blanket <c>LORA</c> stamp that Civitai has never
    /// identified — the cohort a library's VAEs, text encoders, ControlNets and upscalers sit in
    /// (#527). Deliberately excludes <c>Matched</c> rows: those carry an authoritative Civitai
    /// type, and a name guess may fill a blank but never overwrite an answer.
    /// <para>
    /// Also restricted to rows carrying a PICKLE — the only shape the name-only pass may act on
    /// (design §3, "Why the extension condition is load-bearing"). That restriction is in the query
    /// rather than left entirely to the caller because this runs on every discovery pass; see the
    /// implementation's remarks.
    /// </para>
    /// <para>
    /// Excludes <c>IsUserEdited</c> rows, matching <c>IdentifyModelStep</c>'s refusal to re-stamp
    /// <c>Type</c> on one: a type the user set by hand is an answer, not a blank.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Model>> GetSupportAssetBackfillCandidatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows still carrying discovery's old blanket <c>LORA</c> stamp whose safetensors WEIGHTS have
    /// never been read (#527) — the legacy cohort nothing else will ever revisit.
    /// </summary>
    /// <remarks>
    /// Deliberately filtered by neither <c>Source</c> nor sync outcome, and that is the whole point.
    /// <c>IdentifyModelStep</c> corrects a row's kind whenever it reads that file's weights, but it
    /// only ever reaches rows a bulk run selects, and <c>Matched</c> is terminal for the retry
    /// policy — so a support asset Civitai happened to match is re-read by nothing, ever, and keeps
    /// its wrong type permanently. Three real text encoders sat in exactly that state behind a
    /// Civitai match (see the smoke notes on PR #549).
    /// <para>
    /// Bounded by <c>ModelSyncState.HeaderCheckedAt</c> rather than by an outcome, which makes this
    /// one header read per file EVER rather than per run, and empties the pass as it goes. Rows with
    /// no state row at all are excluded rather than given one here: <c>SyncStateInitializer</c> is
    /// what creates those, deriving each from the model's own history, and a bare row Added by this
    /// query's caller would be <c>None</c>/unstamped — i.e. immediately due for a metadata check,
    /// which is the first-run herd <c>SyncStateDeriver</c> exists to prevent. The initializer runs
    /// at the head of every sync plan, so such a row is simply picked up by the next pass.
    /// </para>
    /// <para>
    /// Restricted to safetensors containers, the mirror image of
    /// <see cref="GetSupportAssetBackfillCandidatesAsync"/>'s pickle restriction: this pass reads
    /// weights and a pickle has none, that one guesses from a name and a container must never be
    /// guessed at. Between them every legacy row is reached exactly once, by the only rung that has
    /// any evidence for it.
    /// </para>
    /// <para>
    /// Excludes <c>IsUserEdited</c> rows for the reason every rung does: a type the user set by hand
    /// is an answer, not a blank.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Model>> GetHeaderReclassifyCandidatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The <c>LocalPath</c>, validity flag and verification timestamp of every file belonging to a
    /// support-asset model (VAE, ControlNet, Upscaler, TextEncoder) — nothing else. Backs
    /// <c>ModelFileSyncService.CountExcludedSupportAssetsAsync</c> (#527): that count only ever
    /// needs these three columns, so it must not run through
    /// <see cref="GetModelsWithLocalFilesLightAsync"/>, whose multi-include <c>AsSplitQuery</c>
    /// over Creator/Tags/Versions/TriggerWords is the heaviest read in the Viewer's load path —
    /// paying it twice on every refresh, for a number that never looks at any of that graph, is
    /// exactly the query-count-vs-payload mistake the light variant exists to avoid.
    /// </summary>
    Task<IReadOnlyList<(string LocalPath, bool IsLocalFileValid, DateTimeOffset? LocalFileVerifiedAt)>>
        GetSupportAssetFilePathsAsync(CancellationToken cancellationToken = default);
}
