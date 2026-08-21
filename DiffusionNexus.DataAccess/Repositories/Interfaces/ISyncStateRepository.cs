using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services.Sync;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Reads and writes the per-model library-sync attempt state, and selects the work
/// items ("candidates") each sync step should consider within a <see cref="SyncScope"/>.
/// </summary>
public interface ISyncStateRepository : IRepository<ModelSyncState>
{
    /// <summary>Ids of models that have no <see cref="ModelSyncState"/> row yet (legacy rows awaiting derivation).</summary>
    Task<IReadOnlyList<int>> GetModelIdsWithoutStateAsync(CancellationToken ct = default);

    /// <summary>The tracked state row for a model, or null when it has none.</summary>
    Task<ModelSyncState?> GetByModelIdAsync(int modelId, CancellationToken ct = default);

    /// <summary>
    /// The tracked state row for a model, adding a new row with defaults when it is missing.
    /// The caller still has to call <c>SaveChangesAsync</c>.
    /// </summary>
    Task<ModelSyncState> GetOrCreateAsync(int modelId, CancellationToken ct = default);

    /// <summary>LoRA-family models with a valid local file and no Civitai id, within scope. No retry filtering — the caller applies SyncRetryPolicy.</summary>
    Task<IReadOnlyList<IdentifyCandidate>> SelectIdentifyCandidatesAsync(SyncScope scope, CancellationToken ct = default);

    /// <summary>Models with a Civitai id, not user-edited, zero tags, within scope.</summary>
    Task<IReadOnlyList<TagCandidate>> SelectTagCandidatesAsync(SyncScope scope, CancellationToken ct = default);

    /// <summary>Versions with a Civitai id and zero images whose model has a Civitai id, within scope.</summary>
    Task<IReadOnlyList<ImageCandidate>> SelectImageCandidatesAsync(SyncScope scope, CancellationToken ct = default);
}
