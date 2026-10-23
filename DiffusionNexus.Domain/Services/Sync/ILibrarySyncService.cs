namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>Plans and executes a metadata sync run against the local library.</summary>
public interface ILibrarySyncService
{
    Task<SyncPlan> PlanAsync(SyncScope scope, SyncOptions options, CancellationToken ct = default);
    Task<SyncReport> ExecuteAsync(SyncPlan plan, IProgress<LibrarySyncProgress>? progress = null, CancellationToken ct = default);

    /// <summary>True while an ExecuteAsync is running anywhere in the process (single-flight).</summary>
    bool IsRunning { get; }
}
