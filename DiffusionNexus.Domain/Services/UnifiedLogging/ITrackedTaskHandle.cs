namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Handle returned by <see cref="ITaskTracker.BeginTask"/> for reporting
/// progress and logging against a specific tracked task.
/// Dispose to finalize (auto-completes if not already in a terminal state).
/// </summary>
public interface ITrackedTaskHandle : IDisposable
{
    /// <summary>Unique task identifier used to link log entries.</summary>
    string TaskId { get; }

    /// <summary>Cooperative cancellation token for this task.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Reports determinate progress.
    /// </summary>
    /// <param name="progress">Fraction 0.0–1.0.</param>
    /// <param name="statusText">Optional human-readable status.</param>
    void ReportProgress(double progress, string? statusText = null);

    /// <summary>
    /// Reports indeterminate progress (spinner).
    /// </summary>
    /// <param name="statusText">Optional human-readable status.</param>
    void ReportIndeterminate(string? statusText = null);

    /// <summary>
    /// Marks the task as successfully completed.
    /// </summary>
    /// <param name="message">Optional completion message logged as Info.</param>
    void Complete(string? message = null);

    /// <summary>
    /// Marks the task as failed.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    /// <param name="message">Optional message describing what was being attempted.</param>
    void Fail(Exception ex, string? message = null);

    /// <summary>
    /// Logs a message linked to this task.
    /// </summary>
    void Log(LogLevel level, string message);
}
