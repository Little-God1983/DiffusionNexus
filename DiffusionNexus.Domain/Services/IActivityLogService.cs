namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Severity level for activity log entries.
/// </summary>
public enum ActivitySeverity
{
    /// <summary>Debug information for developers.</summary>
    Debug,
    
    /// <summary>General information about application activity.</summary>
    Info,
    
    /// <summary>Successful completion of an operation.</summary>
    Success,
    
    /// <summary>Warning that something unexpected occurred but operation continued.</summary>
    Warning,
    
    /// <summary>Error that prevented an operation from completing.</summary>
    Error
}

/// <summary>
/// Represents a single entry in the activity log.
/// </summary>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Severity">Severity level of the event.</param>
/// <param name="Source">The component or module that generated the event (e.g., "Backup", "Import", "LoraViewer").</param>
/// <param name="Message">Human-readable description of the event.</param>
/// <param name="Details">Optional additional details or exception info.</param>
public sealed record ActivityLogEntry(
    DateTimeOffset Timestamp,
    ActivitySeverity Severity,
    string Source,
    string Message,
    string? Details = null)
{
    /// <summary>
    /// Creates an info-level log entry.
    /// </summary>
    public static ActivityLogEntry Info(string source, string message, string? details = null)
        => new(DateTimeOffset.Now, ActivitySeverity.Info, source, message, details);

    /// <summary>
    /// Creates a success-level log entry.
    /// </summary>
    public static ActivityLogEntry Success(string source, string message, string? details = null)
        => new(DateTimeOffset.Now, ActivitySeverity.Success, source, message, details);

    /// <summary>
    /// Creates a warning-level log entry.
    /// </summary>
    public static ActivityLogEntry Warning(string source, string message, string? details = null)
        => new(DateTimeOffset.Now, ActivitySeverity.Warning, source, message, details);

    /// <summary>
    /// Creates an error-level log entry.
    /// </summary>
    public static ActivityLogEntry Error(string source, string message, string? details = null)
        => new(DateTimeOffset.Now, ActivitySeverity.Error, source, message, details);

    /// <summary>
    /// Creates an error-level log entry from an exception.
    /// </summary>
    public static ActivityLogEntry Error(string source, string message, Exception exception)
        => new(DateTimeOffset.Now, ActivitySeverity.Error, source, message, exception.Message);

    /// <summary>
    /// Creates a debug-level log entry.
    /// </summary>
    public static ActivityLogEntry Debug(string source, string message, string? details = null)
        => new(DateTimeOffset.Now, ActivitySeverity.Debug, source, message, details);

    /// <summary>
    /// Formats the entry for display.
    /// </summary>
    public string ToDisplayString() => $"[{Timestamp:HH:mm:ss}] [{Severity}] {Source}: {Message}";
}

/// <summary>
/// Represents an ongoing operation with progress tracking.
/// </summary>
public sealed class ProgressOperation : IDisposable
{
    private readonly Action<ProgressOperation>? _onComplete;
    private bool _disposed;

    /// <summary>
    /// Unique identifier for this operation.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Display name of the operation (e.g., "Backing up datasets", "Importing 24 images").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Source component (e.g., "Backup", "Import").
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// Current progress percentage (0-100), or null for indeterminate progress.
    /// </summary>
    public int? ProgressPercent { get; private set; }

    /// <summary>
    /// Optional status message (e.g., "Processing file 5 of 24").
    /// </summary>
    public string? StatusMessage { get; private set; }

    /// <summary>
    /// When the operation started.
    /// </summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>
    /// Whether this operation can be cancelled.
    /// </summary>
    public bool IsCancellable { get; }

    /// <summary>
    /// Cancellation token source for this operation.
    /// </summary>
    public CancellationTokenSource? CancellationTokenSource { get; }

    /// <summary>
    /// Raised when progress is updated.
    /// </summary>
    public event EventHandler? ProgressChanged;

    /// <summary>
    /// Creates a new progress operation.
    /// </summary>
    /// <param name="name">Display name of the operation.</param>
    /// <param name="source">Source component.</param>
    /// <param name="isCancellable">Whether the operation can be cancelled.</param>
    /// <param name="onComplete">Callback when the operation is disposed.</param>
    public ProgressOperation(string name, string source, bool isCancellable = false, Action<ProgressOperation>? onComplete = null)
    {
        Name = name;
        Source = source;
        IsCancellable = isCancellable;
        _onComplete = onComplete;

        if (isCancellable)
        {
            CancellationTokenSource = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Updates the progress of this operation.
    /// </summary>
    /// <param name="percent">Progress percentage (0-100), or null for indeterminate.</param>
    /// <param name="statusMessage">Optional status message.</param>
    public void ReportProgress(int? percent, string? statusMessage = null)
    {
        ProgressPercent = percent;
        StatusMessage = statusMessage;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Requests cancellation of this operation.
    /// </summary>
    public void Cancel()
    {
        if (IsCancellable)
        {
            CancellationTokenSource?.Cancel();
        }
    }

    /// <summary>
    /// Completes this operation and removes it from the active list.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        CancellationTokenSource?.Dispose();
        _onComplete?.Invoke(this);
    }
}

/// <summary>
/// Centralized service for application-wide activity logging and progress tracking.
/// Accessible from all modules (UI, Services, Infrastructure) for consistent feedback.
/// </summary>
public interface IActivityLogService
{
    #region Logging

    /// <summary>
    /// Logs an activity entry.
    /// </summary>
    /// <param name="entry">The log entry to add.</param>
    void Log(ActivityLogEntry entry);

    /// <summary>
    /// Logs an info-level message.
    /// </summary>
    void LogInfo(string source, string message, string? details = null);

    /// <summary>
    /// Logs a success-level message.
    /// </summary>
    void LogSuccess(string source, string message, string? details = null);

    /// <summary>
    /// Logs a warning-level message.
    /// </summary>
    void LogWarning(string source, string message, string? details = null);

    /// <summary>
    /// Logs an error-level message.
    /// </summary>
    void LogError(string source, string message, string? details = null);

    /// <summary>
    /// Logs an error from an exception.
    /// </summary>
    void LogError(string source, string message, Exception exception);

    /// <summary>
    /// Logs a debug-level message (only in debug builds).
    /// </summary>
    void LogDebug(string source, string message, string? details = null);

    /// <summary>
    /// Gets all log entries.
    /// </summary>
    IReadOnlyList<ActivityLogEntry> GetEntries();

    /// <summary>
    /// Gets log entries filtered by minimum severity.
    /// </summary>
    /// <param name="minimumSeverity">Minimum severity to include.</param>
    IReadOnlyList<ActivityLogEntry> GetEntries(ActivitySeverity minimumSeverity);

    /// <summary>
    /// Gets log entries filtered by source.
    /// </summary>
    /// <param name="source">Source to filter by.</param>
    IReadOnlyList<ActivityLogEntry> GetEntries(string source);

    /// <summary>
    /// Clears all log entries.
    /// </summary>
    void ClearLog();

    /// <summary>
    /// Event raised when a new entry is added.
    /// </summary>
    event EventHandler<ActivityLogEntry>? EntryAdded;

    /// <summary>
    /// Event raised when the log is cleared.
    /// </summary>
    event EventHandler? LogCleared;

    #endregion

    #region Progress Tracking

    /// <summary>
    /// Starts tracking a new operation.
    /// Dispose the returned object when the operation completes.
    /// </summary>
    /// <param name="name">Display name of the operation.</param>
    /// <param name="source">Source component.</param>
    /// <param name="isCancellable">Whether the operation can be cancelled.</param>
    /// <returns>A progress operation that should be disposed when complete.</returns>
    ProgressOperation StartOperation(string name, string source, bool isCancellable = false);

    /// <summary>
    /// Gets all currently active operations.
    /// </summary>
    IReadOnlyList<ProgressOperation> GetActiveOperations();

    /// <summary>
    /// Event raised when an operation starts.
    /// </summary>
    event EventHandler<ProgressOperation>? OperationStarted;

    /// <summary>
    /// Event raised when an operation completes.
    /// </summary>
    event EventHandler<ProgressOperation>? OperationCompleted;

    #endregion

    #region Status Bar

    /// <summary>
    /// Sets the status bar message without logging.
    /// Use for transient status updates.
    /// </summary>
    /// <param name="message">Status message to display.</param>
    /// <param name="severity">Optional severity for styling.</param>
    void SetStatus(string message, ActivitySeverity severity = ActivitySeverity.Info);

    /// <summary>
    /// Clears the status bar message (reverts to default "Ready" state).
    /// </summary>
    void ClearStatus();

    /// <summary>
    /// Gets the current status bar message.
    /// </summary>
    string CurrentStatus { get; }

    /// <summary>
    /// Gets the current status severity.
    /// </summary>
    ActivitySeverity CurrentStatusSeverity { get; }

    /// <summary>
    /// Event raised when the status message changes.
    /// </summary>
    event EventHandler? StatusChanged;

    #endregion

    #region Configuration

    /// <summary>
    /// Maximum number of log entries to retain. Older entries are pruned.
    /// Default is 1000.
    /// </summary>
    int MaxEntries { get; set; }

    #endregion

    #region Backup Progress

    /// <summary>
    /// Starts tracking a backup operation with progress bar display.
    /// </summary>
    /// <param name="operationName">Display name of the backup operation.</param>
    void StartBackupProgress(string operationName);

    /// <summary>
    /// Updates the current backup progress percentage.
    /// </summary>
    /// <param name="percent">Progress percentage (0-100).</param>
    /// <param name="statusMessage">Optional status message to display.</param>
    void ReportBackupProgress(int percent, string? statusMessage = null);

    /// <summary>
    /// Completes the current backup operation.
    /// </summary>
    /// <param name="success">Whether the backup completed successfully.</param>
    /// <param name="message">Completion message.</param>
    void CompleteBackupProgress(bool success, string message);

    /// <summary>
    /// Gets whether a backup operation is currently in progress.
    /// </summary>
    bool IsBackupInProgress { get; }

    /// <summary>
    /// Gets the current backup progress percentage (0-100), or null if no backup is running.
    /// </summary>
    int? BackupProgressPercent { get; }

    /// <summary>
    /// Gets the current backup operation name, or null if no backup is running.
    /// </summary>
    string? BackupOperationName { get; }

    /// <summary>
    /// Event raised when backup progress state changes (started, progress updated, or completed).
    /// </summary>
    event EventHandler? BackupProgressChanged;

    #endregion
}
