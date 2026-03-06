namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Reusable progress-tracking system for any long-running task.
/// All tracked tasks are visible in the Unified Console.
/// Registered as a singleton in DI.
/// </summary>
public interface ITaskTracker
{
    /// <summary>
    /// Starts tracking a new task and returns a handle for progress reporting.
    /// The handle should be used inside a <c>using</c> block.
    /// </summary>
    /// <param name="name">Human-readable task name (e.g., "Downloading Forge 1.20.1").</param>
    /// <param name="category">Functional category for filtering.</param>
    /// <param name="cts">Optional cancellation source; one is created if null.</param>
    /// <returns>A handle to report progress, log, and finalize the task.</returns>
    ITrackedTaskHandle BeginTask(string name, LogCategory category, CancellationTokenSource? cts = null);

    /// <summary>
    /// Observable stream of the current list of active (non-terminal) tasks.
    /// Emits a new snapshot whenever a task is added, updated, or removed.
    /// </summary>
    IObservable<IReadOnlyList<TrackedTaskInfo>> ActiveTasks { get; }

    /// <summary>
    /// Snapshot of all tasks including completed, failed, and cancelled (task history).
    /// </summary>
    IReadOnlyList<TrackedTaskInfo> AllTasks { get; }

    /// <summary>
    /// Requests cancellation of a task by its unique identifier.
    /// </summary>
    void CancelTask(string taskId);

    /// <summary>
    /// Raised whenever any task's progress or status changes.
    /// </summary>
    event EventHandler<TrackedTaskInfo>? TaskChanged;
}
