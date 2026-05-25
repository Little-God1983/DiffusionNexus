namespace DiffusionNexus.Domain.Services;

/// <summary>
/// State of a single tracked download on the coordinator's queue.
/// </summary>
public enum DownloadTaskStatus
{
    /// <summary>Waiting for a concurrency slot to open up.</summary>
    Queued,

    /// <summary>Actively running.</summary>
    Active,

    /// <summary>Finished successfully — usually removed shortly after.</summary>
    Completed,

    /// <summary>Finished with an error or exception.</summary>
    Failed,

    /// <summary>The user (or shutdown) cancelled the download before it finished.</summary>
    Cancelled
}

/// <summary>
/// Snapshot of a tracked download. Immutable for UI binding; the coordinator
/// emits fresh snapshots on every state change.
/// </summary>
public sealed record DownloadTask(
    Guid Id,
    string Name,
    DownloadTaskStatus Status,
    int Percent,
    string? StatusMessage);

/// <summary>
/// Progress payload pushed from an in-flight download into the coordinator.
/// </summary>
public sealed record DownloadTaskProgress(int Percent, string? StatusMessage);

/// <summary>
/// Singleton service that coordinates concurrent file downloads across the
/// app. Limits the number of simultaneously-active downloads (default 3),
/// queues the rest, and aggregates progress so the status bar can show a
/// single summary (one name when there's one download, a combined "N
/// downloads in progress" with an averaged percentage when there are more).
/// </summary>
public interface IDownloadCoordinator
{
    /// <summary>
    /// Maximum number of downloads allowed to run at the same time. Excess
    /// enqueues wait for an active slot to free up. Mutable so future
    /// settings UI can expose it.
    /// </summary>
    int MaxConcurrent { get; set; }

    /// <summary>Snapshot of every queued + active download, in enqueue order.</summary>
    IReadOnlyList<DownloadTask> All { get; }

    /// <summary>Snapshot of only the currently-running downloads.</summary>
    IReadOnlyList<DownloadTask> Active { get; }

    /// <summary>Snapshot of downloads waiting on a slot.</summary>
    IReadOnlyList<DownloadTask> Queued { get; }

    /// <summary>Average percent across all currently-active downloads (0–100). 0 when none.</summary>
    int AverageActivePercent { get; }

    /// <summary>
    /// Adds a download to the queue. Returns when the download has finished
    /// (success or failure). The action receives a progress sink it must use
    /// to report percent updates; those updates flow into the coordinator
    /// (and from there into the status bar and unified console).
    /// </summary>
    /// <param name="name">
    /// User-visible name shown in the status bar when this is the only
    /// active download, and in the per-row flyout list at all times.
    /// </param>
    /// <param name="downloadAction">
    /// The actual download work. Receives an <see cref="IProgress{T}"/> for
    /// progress reporting and a <see cref="CancellationToken"/> linked to
    /// shutdown. Should return true on success, false on a recoverable
    /// failure. Exceptions are caught and surfaced as a failed task.
    /// </param>
    /// <param name="cancellationToken">External cancellation.</param>
    Task<bool> EnqueueAsync(
        string name,
        Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>> downloadAction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fired on every state change — task added, started, progressed,
    /// completed, or removed. UI subscribes to refresh both the status-bar
    /// summary and the expandable flyout list.
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Cancels the download identified by <paramref name="taskId"/>. Works
    /// on queued tasks (they never run) and active tasks (their
    /// <see cref="CancellationToken"/> is signalled, the download action
    /// aborts at its next checkpoint, and any partial file is removed by
    /// the action's own cleanup path). Unknown IDs are a no-op.
    /// </summary>
    void Cancel(Guid taskId);
}
