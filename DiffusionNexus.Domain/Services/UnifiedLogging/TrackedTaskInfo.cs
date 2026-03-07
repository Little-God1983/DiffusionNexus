namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Metadata and live state for a tracked long-running task.
/// Mutable progress/status fields are updated by the owning <see cref="ITrackedTaskHandle"/>.
/// </summary>
public sealed class TrackedTaskInfo
{
    /// <summary>Unique identifier for this task.</summary>
    public required string TaskId { get; init; }

    /// <summary>Human-readable name (e.g., "Downloading Forge 1.20.1").</summary>
    public required string Name { get; init; }

    /// <summary>Functional category for log filtering.</summary>
    public required LogCategory Category { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public TrackedTaskStatus Status { get; set; } = TrackedTaskStatus.Queued;

    /// <summary>Progress fraction 0.0–1.0, or -1 for indeterminate.</summary>
    public double Progress { get; set; } = -1;

    /// <summary>Short status text (e.g., "Extracting files… (3/17)").</summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>When the task was created.</summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the task reached a terminal state, if applicable.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Cancellation source for cooperative cancellation.</summary>
    public CancellationTokenSource? Cts { get; init; }

    /// <summary>
    /// Returns true when the task is in a terminal state (completed, failed, or cancelled).
    /// </summary>
    public bool IsTerminal => Status is TrackedTaskStatus.Completed
                                     or TrackedTaskStatus.Failed
                                     or TrackedTaskStatus.Cancelled;
}
