using System.Collections.Concurrent;
using DiffusionNexus.Domain.Services;
using SerilogLogger = Serilog.Log;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Thread-safe implementation of <see cref="IActivityLogService"/>.
/// Provides centralized logging and progress tracking for all application modules.
/// </summary>
public sealed class ActivityLogService : IActivityLogService
{
    private const string DefaultStatus = "Ready";
    private const int DefaultMaxEntries = 1000;
    
    private readonly ConcurrentQueue<ActivityLogEntry> _entries = new();
    private readonly ConcurrentDictionary<Guid, ProgressOperation> _activeOperations = new();
    private readonly object _statusLock = new();
    
    private int _entryCount;
    private string _currentStatus = DefaultStatus;
    private ActivitySeverity _currentStatusSeverity = ActivitySeverity.Info;

    /// <inheritdoc />
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    /// <inheritdoc />
    public string CurrentStatus
    {
        get
        {
            lock (_statusLock)
            {
                return _currentStatus;
            }
        }
    }

    /// <inheritdoc />
    public ActivitySeverity CurrentStatusSeverity
    {
        get
        {
            lock (_statusLock)
            {
                return _currentStatusSeverity;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ActivityLogEntry>? EntryAdded;

    /// <inheritdoc />
    public event EventHandler? LogCleared;

    /// <inheritdoc />
    public event EventHandler<ProgressOperation>? OperationStarted;

    /// <inheritdoc />
    public event EventHandler<ProgressOperation>? OperationCompleted;

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    #region Logging

    /// <inheritdoc />
    public void Log(ActivityLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Enqueue(entry);
        Interlocked.Increment(ref _entryCount);

        // Prune old entries if over limit
        PruneIfNeeded();

        // Also log to Serilog for file-based logging
        LogToSerilog(entry);

        // Update status bar with this message
        SetStatus(entry.Message, entry.Severity);

        // Notify subscribers
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
        => _entries.ToArray();

    /// <inheritdoc />
    public IReadOnlyList<ActivityLogEntry> GetEntries(ActivitySeverity minimumSeverity)
        => _entries.Where(e => e.Severity >= minimumSeverity).ToArray();

    /// <inheritdoc />
    public IReadOnlyList<ActivityLogEntry> GetEntries(string source)
        => _entries.Where(e => e.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <inheritdoc />
    public void ClearLog()
    {
        while (_entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _entryCount);
        }
        
        LogCleared?.Invoke(this, EventArgs.Empty);
    }

    private void PruneIfNeeded()
    {
        while (_entryCount > MaxEntries && _entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _entryCount);
        }
    }

    private static void LogToSerilog(ActivityLogEntry entry)
    {
        var messageTemplate = "[{Source}] {Message}";
        
        switch (entry.Severity)
        {
            case ActivitySeverity.Debug:
                SerilogLogger.Debug(messageTemplate, entry.Source, entry.Message);
                break;
            case ActivitySeverity.Info:
            case ActivitySeverity.Success:
                SerilogLogger.Information(messageTemplate, entry.Source, entry.Message);
                break;
            case ActivitySeverity.Warning:
                SerilogLogger.Warning(messageTemplate, entry.Source, entry.Message);
                break;
            case ActivitySeverity.Error:
                SerilogLogger.Error(messageTemplate + " Details: {Details}", entry.Source, entry.Message, entry.Details);
                break;
        }
    }

    #endregion

    #region Progress Tracking

    /// <inheritdoc />
    public ProgressOperation StartOperation(string name, string source, bool isCancellable = false)
    {
        var operation = new ProgressOperation(name, source, isCancellable, OnOperationCompleted);
        _activeOperations[operation.Id] = operation;
        
        LogInfo(source, $"Started: {name}");
        OperationStarted?.Invoke(this, operation);
        
        return operation;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProgressOperation> GetActiveOperations()
        => _activeOperations.Values.ToArray();

    private void OnOperationCompleted(ProgressOperation operation)
    {
        if (_activeOperations.TryRemove(operation.Id, out _))
        {
            var elapsed = DateTimeOffset.Now - operation.StartedAt;
            LogSuccess(operation.Source, $"Completed: {operation.Name}", $"Duration: {elapsed.TotalSeconds:F1}s");
            OperationCompleted?.Invoke(this, operation);
        }
    }

    #endregion

    #region Status Bar

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

    #endregion
}
