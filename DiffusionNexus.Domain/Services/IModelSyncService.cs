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
    /// One-shot reclassification of rows that predate support-asset detection (#527), in two arms
    /// that partition the legacy library by the evidence each file can offer: a pickle
    /// (<c>.ckpt</c>/<c>.pt</c>/<c>.pth</c>) is named from its FILE NAME, a safetensors container
    /// (<c>.safetensors</c>/<c>.sft</c>) from its WEIGHTS. Returns how many rows changed in total.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="excludeModelIds">
    /// Model ids to skip even though they match the candidate query. Exists for
    /// <see cref="DiscoverNewFilesAsync"/>'s own use (#527 round 2): a model it just created in
    /// the SAME call had its kind set from its weights via <c>AssetKindResolver</c> — the more
    /// accurate rung — moments earlier, and looks identical to a genuinely old, never-classified
    /// row to this query (neither has a <c>ModelSyncState</c> row yet). Re-examining a
    /// just-created row here by name alone can only make an already-correct weight-based verdict
    /// worse, never better; the container arm would merely reproduce the identical verdict at the
    /// cost of a second header read. Null (the default) excludes nothing — every other caller,
    /// including this method's own direct tests, has no such batch to protect.
    /// </param>
    /// <remarks>
    /// A pickle has no readable header at all, so its file name is the only evidence this pass — or
    /// anything else — will ever have for it; that is where a name guess is warranted, and its
    /// result is final. A safetensors container is never guessed at from its name, on weaker
    /// evidence, when its weights can be read: that asymmetry is the design's, not this method's.
    /// <para>
    /// <b>The container arm was originally omitted, and the argument for omitting it was wrong.</b>
    /// It ran: a header read here would cost minutes on a large library over a NAS, and does not
    /// need to happen in this pass anyway, because every row the pickle candidate query selects is
    /// <c>NotIdentified</c>/<c>None</c> — already due for <c>IdentifyModelStep</c>, which reads the
    /// header for free. Both halves failed. The scope claim was circular: it described the rows THAT
    /// query selects, while the containers at issue are the ones it does not, and a <c>Matched</c>
    /// row is terminal for the retry policy, so <c>IdentifyModelStep</c> never selects it again —
    /// its correction is unreachable for exactly the rows that need it, permanently. Three real text
    /// encoders sat in that state behind a Civitai match, unambiguous from their weights and read by
    /// nothing. The cost claim was simply unmeasured: a sweep of one real 1553-container library
    /// takes 4.5 s, about 3 ms a file, because a safetensors header is a small JSON block at the
    /// front of the file and the tensor payload is never touched.
    /// </para>
    /// <para>
    /// Both arms are idempotent and self-terminating, by different means. A pickle reclassified to
    /// VAE no longer satisfies its candidate query's <c>Type == LORA</c>. A container is stamped
    /// <c>ModelSyncState.HeaderCheckedAt</c> whatever its weights said — including "nothing" —
    /// which is what makes the read once per file EVER rather than once per pass, and what stops a
    /// container whose keys match no rung from being re-read forever.
    /// </para>
    /// </remarks>
    Task<int> ReclassifySupportAssetsAsync(
        CancellationToken cancellationToken = default,
        IReadOnlySet<int>? excludeModelIds = null);

    /// <summary>
    /// How many support assets — VAEs, ControlNets, upscalers, text encoders — the LoRA grid is
    /// currently leaving out (#527).
    /// </summary>
    /// <remarks>
    /// <see cref="LoadCachedFilesAsync"/> has always dropped everything outside the LoRA family,
    /// which is correct and predates this feature. What is new is that these files now have a
    /// TYPE saying what they are, so on a legacy library they leave a grid the user has been
    /// looking at for months. A file that disappears with no explanation reads as data loss;
    /// this count is what lets the Viewer say where it went. Counted separately from the load
    /// rather than returned by it, so no existing caller's shape changes for a number only one
    /// of them wants.
    /// <para>
    /// Honours the enabled LoRA sources for the same reason the load does: a VAE under a source
    /// the user has disabled is not being hidden by this feature, and counting it would send
    /// them looking for a file that was never going to show.
    /// </para>
    /// </remarks>
    Task<int> CountExcludedSupportAssetsAsync(CancellationToken cancellationToken = default);
}
