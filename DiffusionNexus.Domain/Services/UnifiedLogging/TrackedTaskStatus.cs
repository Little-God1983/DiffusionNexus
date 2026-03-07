namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Lifecycle status of a tracked long-running task.
/// </summary>
public enum TrackedTaskStatus
{
    /// <summary>Task is queued but not yet started.</summary>
    Queued,

    /// <summary>Task is actively running.</summary>
    Running,

    /// <summary>Task is temporarily paused.</summary>
    Paused,

    /// <summary>Task completed successfully.</summary>
    Completed,

    /// <summary>Task failed with an error.</summary>
    Failed,

    /// <summary>Task was cancelled by the user or system.</summary>
    Cancelled
}
