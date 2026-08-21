using DiffusionNexus.DataAccess.Exceptions;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// One-time backfill: gives every model that predates the sync-state table a
/// <see cref="Domain.Entities.ModelSyncState"/> row derived by <see cref="SyncStateDeriver"/>.
/// Idempotent — once every model has a row it is a single cheap id query and returns 0.
/// </summary>
public sealed class SyncStateInitializer
{
    private const int BatchSize = 200;
    private const string LogSource = "LibrarySync";

    /// <summary>
    /// Process-wide single-flight gate. The backfill runs inside <c>PlanAsync</c>, and on the first
    /// launch after the upgrade it takes seconds over a real library — long enough for the user to
    /// press the per-tile button while the bulk sync is already planning. Both calls would then read
    /// the same "no state row yet" id list and Add the same primary keys, and the loser's save is
    /// rejected outright: the second plan dies instead of syncing anything. Static because the two
    /// callers hold two instances of nothing in particular — the gate belongs to the table.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IUnifiedLogger? _logger;

    public SyncStateInitializer(IServiceScopeFactory scopes, IUnifiedLogger? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger;
    }

    /// <summary>
    /// Creates the missing state rows and returns how many were created.
    /// Uses a fresh scope per batch so no context grows across the whole library.
    /// </summary>
    /// <remarks>
    /// Serialized process-wide (see <see cref="Gate"/>), so a second caller waits and then finds
    /// nothing left to do rather than racing for the same keys. The waiting is cheap: once every
    /// model has a row this is one id query that returns empty.
    /// </remarks>
    public async Task<int> EnsureInitializedAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await InitializeAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<int> InitializeAsync(CancellationToken ct)
    {
        IReadOnlyList<int> legacyIds;
        using (var scope = _scopes.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            legacyIds = await uow.SyncStates.GetModelIdsWithoutStateAsync(ct);
        }

        if (legacyIds.Count == 0) return 0;

        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"Initializing sync state for {legacyIds.Count} legacy models (derived from existing data, no network)");

        var created = 0;

        for (var offset = 0; offset < legacyIds.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = legacyIds.Skip(offset).Take(BatchSize).ToList();

            using var scope = _scopes.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            try
            {
                created += await DeriveBatchAsync(uow, batch, ct);
            }
            catch (Exception ex) when (IsRejectedSave(ex))
            {
                // The gate only covers this process. Another one — an installer, a second window, a
                // developer's dotnet-ef — can still have created some of these rows since the id
                // query, and losing that race must not cost the user the whole backfill. Deriving is
                // idempotent, so re-asking which ids are still missing and redoing just those is a
                // complete recovery. Once only: if it happens twice something else is wrong, and
                // silently looping over a database that keeps refusing us is not a fix.
                _logger?.Warn(LogCategory.FileSystem, LogSource,
                    $"Sync-state batch was rejected ({ex.Message}); re-reading which rows are still missing and retrying once");

                uow.ClearChangeTracker();

                var stillMissing = (await uow.SyncStates.GetModelIdsWithoutStateAsync(ct)).ToHashSet();
                var retry = batch.Where(stillMissing.Contains).ToList();

                if (retry.Count > 0) created += await DeriveBatchAsync(uow, retry, ct);
            }
        }

        _logger?.Info(LogCategory.FileSystem, LogSource, $"Sync state initialized for {created} legacy models");

        return created;
    }

    /// <summary>Derives and saves one batch, returning how many rows it added.</summary>
    /// <remarks>
    /// One projected query for the whole batch (R8). It used to be one <c>GetByIdWithIncludesAsync</c>
    /// per model — five split queries each, dragging every image's <c>ThumbnailData</c> BLOB along —
    /// to answer four booleans about it. Over a real library that is hundreds of megabytes of JPEG
    /// read on the first launch after the upgrade, for nothing.
    /// </remarks>
    private async Task<int> DeriveBatchAsync(IUnitOfWork uow, IReadOnlyList<int> batch, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var inputs = await uow.SyncStates.GetDerivationInputsAsync(batch, ct);

        // Absent from the projection = deleted between the id query and now, so there is nothing to
        // derive from. Logged as a set rather than per model: a bulk delete would otherwise write
        // one debug line per row.
        if (inputs.Count != batch.Count)
        {
            var missing = batch.Except(inputs.Select(i => i.ModelId)).ToList();
            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"Skipped sync-state derivation for {missing.Count} model(s) that no longer exist",
                string.Join(", ", missing));
        }

        foreach (var input in inputs)
        {
            await uow.SyncStates.AddAsync(SyncStateDeriver.Derive(input, now), ct);
        }

        if (inputs.Count == 0) return 0;

        await uow.SaveChangesAsync(ct);
        return inputs.Count;
    }

    /// <summary>
    /// A save the database refused. The unit of work translates EF's <see cref="DbUpdateException"/>
    /// into <see cref="DatabaseOperationException"/>, which is what actually arrives here; the raw
    /// type is listed for any path that does not go through the unit of work.
    /// </summary>
    private static bool IsRejectedSave(Exception ex) => ex is DbUpdateException or DatabaseOperationException;
}
