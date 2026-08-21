using DiffusionNexus.Domain.Services.Sync;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// One unit of work inside a sync run. <paramref name="Payload"/> carries the step's own
/// candidate record (e.g. <see cref="IdentifyCandidate"/>); <paramref name="Name"/> is the
/// model name shown in progress.
/// </summary>
public sealed record SyncItem(int ModelId, string Name, object Payload);

/// <summary>
/// What executing one <see cref="SyncItem"/> achieved. <paramref name="Skipped"/> means the
/// item needed no work at all (it is neither a success nor a failure for the report's counts).
/// </summary>
public sealed record SyncItemResult(bool Succeeded, bool Skipped, string? FailureReason)
{
    public static SyncItemResult Success { get; } = new(true, false, null);
    public static SyncItemResult Skip { get; } = new(false, true, null);
    public static SyncItemResult Failure(string reason) => new(false, false, reason);
}

/// <summary>
/// A single step of the library sync (#521 WP2). Selection is a pure DB+policy query with no
/// network access, so <c>PlanAsync</c> can call it on every step and still finish in under a
/// second; execution handles exactly one item and is responsible for recording its own outcome
/// in <c>ModelSyncState</c> — the service only counts results.
/// </summary>
/// <remarks>
/// Implementations must hold no <c>DbContext</c>: the owning service is a singleton, so every
/// step creates a fresh <c>IServiceScope</c>/<c>IUnitOfWork</c> per <see cref="SelectAsync"/>
/// call and per <see cref="ExecuteOneAsync"/> item.
/// </remarks>
public interface ISyncStep
{
    SyncStepKind Kind { get; }

    /// <summary>One-line description of the step for the plan dialog.</summary>
    string Description { get; }

    /// <summary>Wall-clock budget per item, used to estimate a run's duration in the plan.</summary>
    TimeSpan EstimatedPerItem { get; }

    /// <summary>The items this step would process. DB queries and retry policy only — never the network.</summary>
    Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Processes one item and records its outcome. Returns a failure result rather than throwing
    /// for expected faults (network, disk); only cancellation propagates.
    /// </summary>
    Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct);
}
