using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// Step 0 — scans the configured source folders for model files the database does not know yet,
/// by delegating to the existing <see cref="IModelSyncService.DiscoverNewFilesAsync"/> (#521 WP2).
/// </summary>
/// <remarks>
/// The disk scan itself decides how many files there are, so this step cannot be planned as
/// "n items": <see cref="SelectAsync"/> returns a single pseudo-item and the run's report reads
/// <see cref="DiscoveredCount"/> afterwards. The sync service's single-flight guard is what makes
/// that mutable count safe — only one run exists at a time.
/// </remarks>
public sealed class DiscoverFilesStep : ISyncStep
{
    private const string LogSource = "LibrarySync";

    private readonly IServiceScopeFactory _scopes;
    private readonly IUnifiedLogger? _logger;

    public DiscoverFilesStep(IServiceScopeFactory scopes, IUnifiedLogger? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger;
    }

    /// <summary>How many new models the last <see cref="ExecuteOneAsync"/> created.</summary>
    public int DiscoveredCount { get; private set; }

    /// <summary>
    /// How many existing rows the last scan re-pointed at moved files (#537). Those rows were
    /// stamped invalid — hidden from the grid — so the report has to carry this or a repoint-only
    /// scan reads as "nothing happened" while twelve models just became visible-in-the-database.
    /// </summary>
    public int RepointedCount { get; private set; }

    /// <summary>
    /// How many pre-existing rows the last scan reclassified as support assets (#527). Reported
    /// for the same reason RepointedCount is: on a library that predates the feature this is the
    /// only visible sign that 35 rows just stopped claiming to be LoRAs.
    /// </summary>
    public int ReclassifiedCount { get; private set; }

    /// <inheritdoc />
    public SyncStepKind Kind => SyncStepKind.DiscoverFiles;

    /// <inheritdoc />
    public string Description => "Scan source folders for new files";

    /// <inheritdoc />
    public TimeSpan EstimatedPerItem => TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SyncItem>>([new SyncItem(0, Description, scope)]);

    /// <inheritdoc />
    public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
    {
        DiscoveredCount = 0;
        RepointedCount = 0;
        ReclassifiedCount = 0;

        try
        {
            // IModelSyncService is transient and owns a DbContext — never cache it on this step.
            using var scope = _scopes.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IModelSyncService>();

            // ReclassifySupportAssetsAsync now runs INSIDE DiscoverNewFilesAsync itself (#527
            // round 2) — the passive background reconcile path calls that method directly and
            // never went through this step, so a separate call here used to mean a user who only
            // ever refreshed a legacy library never got the backfill. Read the count off the
            // result rather than calling the pass again: reclassification is self-terminating (a
            // reclassified row no longer matches its own candidate query), so a second direct call
            // here would not double the count — it would silently overwrite it with 0 and undo
            // this line's whole purpose.
            var discovered = await sync.DiscoverNewFilesAsync(progress: null, ct).ConfigureAwait(false);
            DiscoveredCount = discovered.NewModels.Count;
            RepointedCount = discovered.RepointedCount;
            ReclassifiedCount = discovered.ReclassifiedCount;

            _logger?.Info(LogCategory.FileSystem, LogSource,
                $"Discovered {DiscoveredCount} new model file(s), re-pointed {RepointedCount} moved, " +
                $"reclassified {ReclassifiedCount} as support assets");
            return SyncItemResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException or TaskCanceledException)
        {
            // Only the faults a disk scan can legitimately hit: an unreadable/absent source folder,
            // a permission wall, or a write that the database rejected. A NullReferenceException in
            // here is a bug and must surface as one, not as a tidy "step failed" row — the sync
            // service counts it and carries on (R5).
            //
            // TaskCanceledException is in the list because the filter above it already claimed the
            // only cancellation that matters: one raised while OUR token is cancelled. What reaches
            // here is a cancellation of somebody else's token — a timeout inside the scan — which is
            // a fault of this scan, not the user pressing Cancel, and reporting it as a cancelled
            // run would be a lie about what happened.
            _logger?.Error(LogCategory.FileSystem, LogSource, "File discovery failed", ex);
            return SyncItemResult.Failure(ex.Message);
        }
    }
}
