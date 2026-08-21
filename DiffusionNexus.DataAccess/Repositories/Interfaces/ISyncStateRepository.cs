using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services.Sync;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Reads and writes the per-model library-sync attempt state, and selects the work
/// items ("candidates") each sync step should consider within a <see cref="SyncScope"/>.
/// </summary>
/// <remarks>
/// Every candidate selection takes the enabled LoRA source roots as well as the scope, because
/// "the library" is exactly what those roots contain. A model is in the library when it has a
/// file with a <c>LocalPath</c> under one of the roots that the user can still see — the same
/// rule as <c>ModelFileSyncService.LoadCachedFilesAsync</c>: valid, or never verified at all
/// (a legacy row predating verification). Rows left behind by a source the user has since
/// disabled, moved or removed are still in the database but are no longer library members, and
/// syncing them spends the user's Civitai budget on models they cannot see. The predicate
/// applies to <see cref="SyncScopeKind.Library"/> and
/// <see cref="SyncScopeKind.SourceFolder"/> (which narrows further to that one folder); an
/// explicit <see cref="SyncScopeKind.Models"/> scope is the user pointing at specific models, so
/// it deliberately ignores the roots. An empty root list therefore selects nothing at all for
/// the first two kinds.
/// </remarks>
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

    /// <summary>
    /// LoRA-family models with a valid local file and no Civitai id, within scope and in the
    /// library. No retry filtering — the caller applies SyncRetryPolicy.
    /// </summary>
    /// <param name="scope">What the run targets.</param>
    /// <param name="enabledSourceRoots">
    /// The enabled LoRA source folders — see the remarks on <see cref="ISyncStateRepository"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<IdentifyCandidate>> SelectIdentifyCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default);

    /// <summary>
    /// Models with a Civitai id, not user-edited, zero tags, within scope and in the library.
    /// </summary>
    /// <param name="scope">What the run targets.</param>
    /// <param name="enabledSourceRoots">
    /// The enabled LoRA source folders — see the remarks on <see cref="ISyncStateRepository"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TagCandidate>> SelectTagCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default);

    /// <summary>
    /// Versions with a Civitai id and zero images whose model has a Civitai id, within scope and
    /// in the library.
    /// </summary>
    /// <param name="scope">What the run targets.</param>
    /// <param name="enabledSourceRoots">
    /// The enabled LoRA source folders — see the remarks on <see cref="ISyncStateRepository"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ImageCandidate>> SelectImageCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default);
}
