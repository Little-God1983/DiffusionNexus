namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of a file sync operation.
/// </summary>
public record FileSyncResult
{
    /// <summary>Files that were already in the database and verified on disk.</summary>
    public int VerifiedCount { get; init; }

    /// <summary>New files discovered on disk and added to database.</summary>
    public int NewFilesCount { get; init; }

    /// <summary>Files in database that are no longer found on disk.</summary>
    public int MissingCount { get; init; }

    /// <summary>Files that were moved and their paths were updated.</summary>
    public int MovedCount { get; init; }

    /// <summary>Total files processed.</summary>
    public int TotalProcessed => VerifiedCount + NewFilesCount + MissingCount + MovedCount;
}

/// <summary>
/// The outcome of one discovery scan (#537): what it ADDED and what it merely CHANGED. A
/// hash-matched moved file is re-pointed at its new path in the same SaveChanges that inserts the
/// new models — a grid-visible change that is not a new file, so it travels as its own count
/// rather than widening "N new files discovered".
/// </summary>
public record DiscoveryResult
{
    /// <summary>Genuinely new models, created for files the database did not know.</summary>
    public IReadOnlyList<Entities.Model> NewModels { get; init; } = [];

    /// <summary>
    /// Existing <c>ModelFile</c> rows whose path was re-pointed at a moved file (matched by size
    /// and hash). Repoint candidates are by definition invalid-path rows — the ones the grid
    /// hides — so a caller deciding whether the grid needs re-projecting must count these as yes.
    /// </summary>
    public int RepointedCount { get; init; }

    /// <summary>
    /// Pre-existing rows corrected out of discovery's old blanket <c>LORA</c> stamp into their real
    /// kind (#527) — a VAE, text encoder, ControlNet or upscaler a library predating that
    /// detection still mislabelled. Changed, not added, the same shape of thing
    /// <see cref="RepointedCount"/> already is. Runs on every call to
    /// <see cref="IModelSyncService.DiscoverNewFilesAsync"/>, not only when new files are found —
    /// a returning library with nothing new on disk is exactly the case the legacy backfill has to
    /// reach, since every caller of discovery (the bulk sync button and the passive background
    /// reconcile alike) gets this for free rather than only the one that also calls
    /// <see cref="IModelSyncService.ReclassifySupportAssetsAsync"/> directly.
    /// </summary>
    public int ReclassifiedCount { get; init; }
}

/// <summary>
/// Progress information for sync operations.
/// </summary>
public record SyncProgress
{
    /// <summary>Current phase of the sync operation.</summary>
    public required string Phase { get; init; }

    /// <summary>Current item being processed.</summary>
    public string? CurrentItem { get; init; }

    /// <summary>Number of items processed so far.</summary>
    public int ProcessedCount { get; init; }

    /// <summary>Total number of items to process (if known).</summary>
    public int? TotalCount { get; init; }

    /// <summary>Progress percentage (0-100) if calculable.</summary>
    public int? ProgressPercent => TotalCount > 0 ? (ProcessedCount * 100 / TotalCount) : null;
}

/// <summary>
/// One physical LoRA file on disk together with the database entities that own it
/// and the enabled LoRA-source root the file lives under. The Installed tab groups
/// these by <c>(Model, SourceRoot)</c> so each location becomes a separate tile —
/// issue #380.
/// </summary>
public sealed record InstalledModelFile(
    Entities.Model Model,
    Entities.ModelVersion Version,
    Entities.ModelFile File,
    string SourceRoot);

/// <summary>
/// Service for synchronizing local model files with the database.
/// Handles discovery of new files, verification of existing files,
/// and detection/resolution of moved files.
/// </summary>
public interface IModelSyncService
{
    /// <summary>
    /// Loads all models from the database that have valid local files.
    /// This is the fast path for displaying cached data immediately.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Models with local files from the database.</returns>
    Task<IReadOnlyList<Entities.Model>> LoadCachedModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one entry per physical ModelFile under an enabled LoRA source. Used by
    /// the Installed tab to render one tile per copy on disk — two folders containing
    /// the same LoRA produce two entries (issue #380).
    /// </summary>
    Task<IReadOnlyList<InstalledModelFile>> LoadCachedFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans configured source folders for model files whose paths the database does not know.
    /// Creates minimal Model/ModelVersion/ModelFile entries for genuinely new files; a file whose
    /// size and hash match an existing invalid-path row is a MOVE, and that row is re-pointed at
    /// the new path instead.
    /// </summary>
    /// <param name="progress">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the scan added and what it changed (#537) — both are grid-visible.</returns>
    Task<DiscoveryResult> DiscoverNewFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that files in the database still exist at their recorded paths.
    /// For missing files, attempts to find them by hash/size match.
    /// Updates IsLocalFileValid and LocalFileVerifiedAt accordingly.
    /// </summary>
    /// <param name="progress">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of sync results.</returns>
    Task<FileSyncResult> VerifyAndSyncFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a full sync: load cached, discover new, verify existing.
    /// Returns all models (both cached and newly discovered).
    /// </summary>
    /// <param name="progress">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All models with local files.</returns>
    Task<IReadOnlyList<Entities.Model>> FullSyncAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes and stores the SHA256 hash for a file.
    /// Uses first 10MB of file for performance on large files.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The computed hash.</returns>
    Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// One-shot reclassification of rows that predate support-asset detection (#527), scoped to the
    /// files a header cannot help with — a pickle (<c>.ckpt</c>/<c>.pt</c>/<c>.pth</c>), named from
    /// its FILE NAME only. A safetensors container (<c>.safetensors</c>/<c>.sft</c>) is left exactly
    /// as it is: it is decided by <c>IdentifyModelStep</c> from its weights instead. Returns how
    /// many rows changed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="excludeModelIds">
    /// Model ids to skip even though they match the candidate query. Exists for
    /// <see cref="DiscoverNewFilesAsync"/>'s own use (#527 round 2): a model it just created in
    /// the SAME call had its kind set from its weights via <c>AssetKindResolver</c> — the more
    /// accurate rung — moments earlier, and looks identical to a genuinely old, never-classified
    /// row to this query (neither has a <c>ModelSyncState</c> row yet). Re-examining a
    /// just-created row here by name alone can only make an already-correct weight-based verdict
    /// worse, never better. Null (the default) excludes nothing — every other caller, including
    /// this method's own direct tests, has no such batch to protect.
    /// </param>
    /// <remarks>
    /// A pickle has no readable header at all, so its file name is the only evidence this pass — or
    /// anything else — will ever have for it; that is where a name guess is warranted, and its
    /// result is final. A safetensors container is a different case entirely, not a cheaper version
    /// of the same one: reading a header per row here would cost minutes on a large library over a
    /// NAS, but that read does not need to happen in THIS pass, because it already happens for free
    /// the next time <c>IdentifyModelStep</c> looks at the row — every row this candidate query
    /// selects is <c>NotIdentified</c>/<c>None</c>, i.e. already due for that step. Guessing a
    /// safetensors row's kind from its name here, on weaker evidence, only to have the real answer
    /// overwrite it moments or days later, is strictly worse than never guessing at all — so this
    /// pass skips every safetensors row outright, regardless of what its name says. Idempotent and
    /// self-terminating for the pickles it does classify — a row reclassified to VAE no longer
    /// satisfies the candidate query's <c>Type == LORA</c>.
    /// </remarks>
    Task<int> ReclassifySupportAssetsAsync(
        CancellationToken cancellationToken = default,
        IReadOnlySet<int>? excludeModelIds = null);
}
