using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Bridge adapter that implements the legacy <see cref="IActivityLogService"/> interface
/// by delegating to <see cref="IUnifiedLogger"/> and <see cref="ITaskTracker"/>.
/// Allows existing consumers to work without immediate migration.
/// </summary>
[Obsolete("Use IUnifiedLogger and ITaskTracker directly. This bridge exists for backward compatibility.")]
public sealed class ActivityLogServiceBridge : IActivityLogService
{
    private const string DefaultStatus = "Ready";

    private readonly IUnifiedLogger _logger;
    private readonly ITaskTracker _taskTracker;
    private readonly object _statusLock = new();
    private readonly object _backupLock = new();

    private string _currentStatus = DefaultStatus;
    private ActivitySeverity _currentStatusSeverity = ActivitySeverity.Info;
    private bool _isBackupInProgress;
    private int? _backupProgressPercent;
    private string? _backupOperationName;

    public ActivityLogServiceBridge(IUnifiedLogger logger, ITaskTracker taskTracker)
    {
        _logger = logger;
        _taskTracker = taskTracker;
    }

    /// <inheritdoc />
    public int MaxEntries { get; set; } = 1000;

    #region Logging – delegates to IUnifiedLogger

    /// <inheritdoc />
    public void Log(ActivityLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var level = MapSeverity(entry.Severity);
        _logger.Log(level, LogCategory.General, entry.Source, entry.Message, detail: entry.Details);

        SetStatus(entry.Message, entry.Severity);
        EntryAdded?.Invoke(this, entry);
    }

    /// <inheritdoc />
    public void LogInfo(string source, string message, string? details = null)
        => Log(ActivityLogEntry.Info(source, message, details));

    /// <inheritdoc />
    public void LogSuccess(string source, string message, string? details = null)
        => Log(ActivityLogEntry.Success(source, message, details));

    /// <inheritdoc />
    public void LogWarning(string source, string message, string? details = null)
        => Log(ActivityLogEntry.Warning(source, message, details));

    /// <inheritdoc />
    public void LogError(string source, string message, string? details = null)
        => Log(ActivityLogEntry.Error(source, message, details));

    /// <inheritdoc />
    public void LogError(string source, string message, Exception exception)
        => Log(ActivityLogEntry.Error(source, message, exception));

    /// <inheritdoc />
    public void LogDebug(string source, string message, string? details = null)
    {
#if DEBUG
        Log(ActivityLogEntry.Debug(source, message, details));
#endif
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityLogEntry> GetEntries()
    {
        // Convert from unified LogEntry back to ActivityLogEntry for compatibility
        return _logger.GetEntries()
            .Select(e => new ActivityLogEntry(
                new DateTimeOffset(e.Timestamp, TimeSpan.Zero),
                MapLevel(e.Level),
                e.Source,
                e.Message,
                e.Detail))
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityLogEntry> GetEntries(ActivitySeverity minimumSeverity)
    {
        var minLevel = MapSeverity(minimumSeverity);
        return _logger.GetEntries(minLevel: minLevel)
            .Select(e => new ActivityLogEntry(
                new DateTimeOffset(e.Timestamp, TimeSpan.Zero),
                MapLevel(e.Level),
                e.Source,
                e.Message,
                e.Detail))
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityLogEntry> GetEntries(string source)
    {
        return _logger.GetEntries()
            .Where(e => e.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .Select(e => new ActivityLogEntry(
                new DateTimeOffset(e.Timestamp, TimeSpan.Zero),
                MapLevel(e.Level),
                e.Source,
                e.Message,
                e.Detail))
            .ToArray();
    }

    /// <inheritdoc />
    public void ClearLog()
    {
        _logger.Clear();
        LogCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler<ActivityLogEntry>? EntryAdded;
    /// <inheritdoc />
    public event EventHandler? LogCleared;

    #endregion

    #region Progress Tracking – delegates to ITaskTracker

    /// <inheritdoc />
    public ProgressOperation StartOperation(string name, string source, bool isCancellable = false)
    {
        var operation = new ProgressOperation(name, source, isCancellable, OnOperationCompleted);

        // Also create a tracked task so it appears in the unified console
        _taskTracker.BeginTask(name, LogCategory.General);

        OperationStarted?.Invoke(this, operation);
        return operation;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProgressOperation> GetActiveOperations() => [];

    /// <inheritdoc />
    public event EventHandler<ProgressOperation>? OperationStarted;
    /// <inheritdoc />
    public event EventHandler<ProgressOperation>? OperationCompleted;

    private void OnOperationCompleted(ProgressOperation operation)
    {
        OperationCompleted?.Invoke(this, operation);
    }

    #endregion

    #region Status Bar

    /// <inheritdoc />
    public string CurrentStatus
    {
        get { lock (_statusLock) return _currentStatus; }
    }

    /// <inheritdoc />
    public ActivitySeverity CurrentStatusSeverity
    {
        get { lock (_statusLock) return _currentStatusSeverity; }
    }

    /// <inheritdoc />
    public void SetStatus(string message, ActivitySeverity severity = ActivitySeverity.Info)
    {
        lock (_statusLock)
        {
            _currentStatus = message;
            _currentStatusSeverity = severity;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void ClearStatus()
    {
        lock (_statusLock)
        {
            _currentStatus = DefaultStatus;
            _currentStatusSeverity = ActivitySeverity.Info;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    #endregion

    #region Backup Progress

    /// <inheritdoc />
    public bool IsBackupInProgress
    {
        get { lock (_backupLock) return _isBackupInProgress; }
    }

    /// <inheritdoc />
    public int? BackupProgressPercent
    {
        get { lock (_backupLock) return _backupProgressPercent; }
    }

    /// <inheritdoc />
    public string? BackupOperationName
    {
        get { lock (_backupLock) return _backupOperationName; }
    }

    /// <inheritdoc />
    public void StartBackupProgress(string operationName)
    {
        lock (_backupLock)
        {
            _isBackupInProgress = true;
            _backupProgressPercent = 0;
            _backupOperationName = operationName;
        }
        _logger.Info(LogCategory.Backup, "Backup", $"Starting: {operationName}");
        BackupProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void ReportBackupProgress(int percent, string? statusMessage = null)
    {
        lock (_backupLock)
        {
            _backupProgressPercent = Math.Clamp(percent, 0, 100);
        }
        if (!string.IsNullOrEmpty(statusMessage))
            SetStatus(statusMessage);
        BackupProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void CompleteBackupProgress(bool success, string message)
    {
        lock (_backupLock)
        {
            _isBackupInProgress = false;
            _backupProgressPercent = null;
            _backupOperationName = null;
        }
        if (success)
            _logger.Info(LogCategory.Backup, "Backup", message);
        else
            _logger.Error(LogCategory.Backup, "Backup", message);
        BackupProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? BackupProgressChanged;

    #endregion

    #region Mapping Helpers

    private static LogLevel MapSeverity(ActivitySeverity severity) => severity switch
    {
        ActivitySeverity.Debug => LogLevel.Debug,
        ActivitySeverity.Info => LogLevel.Info,
        ActivitySeverity.Success => LogLevel.Info,
        ActivitySeverity.Warning => LogLevel.Warning,
        ActivitySeverity.Error => LogLevel.Error,
        _ => LogLevel.Info
    };

    private static ActivitySeverity MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => ActivitySeverity.Debug,
        LogLevel.Debug => ActivitySeverity.Debug,
        LogLevel.Info => ActivitySeverity.Info,
        LogLevel.Warning => ActivitySeverity.Warning,
        LogLevel.Error => ActivitySeverity.Error,
        LogLevel.Fatal => ActivitySeverity.Error,
        _ => ActivitySeverity.Info
    };

    #endregion
}
