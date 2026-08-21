using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.UnifiedLogging;
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
    public async Task<int> EnsureInitializedAsync(CancellationToken ct = default)
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

            var now = DateTimeOffset.UtcNow;
            var derivedInBatch = 0;

            foreach (var id in batch)
            {
                var model = await uow.Models.GetByIdWithIncludesAsync(id, ct);
                if (model is null)
                {
                    // Deleted between the id query and now — nothing to derive from.
                    _logger?.Debug(LogCategory.FileSystem, LogSource,
                        $"Skipped sync-state derivation for model {id}: the model no longer exists");
                    continue;
                }

                await uow.SyncStates.AddAsync(SyncStateDeriver.Derive(model, now), ct);
                derivedInBatch++;
            }

            if (derivedInBatch == 0) continue;

            await uow.SaveChangesAsync(ct);
            created += derivedInBatch;
        }

        _logger?.Info(LogCategory.FileSystem, LogSource, $"Sync state initialized for {created} legacy models");

        return created;
    }
}
