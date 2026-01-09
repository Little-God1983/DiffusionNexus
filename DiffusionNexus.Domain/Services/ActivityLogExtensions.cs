namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Extension methods for convenient logging through IActivityLogService.
/// </summary>
public static class ActivityLogExtensions
{
    /// <summary>
    /// Logs an info message and returns the entry for chaining.
    /// </summary>
    public static ActivityLogEntry Info(this IActivityLogService logService, string source, string message, string? details = null)
    {
        var entry = ActivityLogEntry.Info(source, message, details);
        logService.Log(entry);
        return entry;
    }

    /// <summary>
    /// Logs a success message and returns the entry for chaining.
    /// </summary>
    public static ActivityLogEntry Success(this IActivityLogService logService, string source, string message, string? details = null)
    {
        var entry = ActivityLogEntry.Success(source, message, details);
        logService.Log(entry);
        return entry;
    }

    /// <summary>
    /// Logs a warning message and returns the entry for chaining.
    /// </summary>
    public static ActivityLogEntry Warning(this IActivityLogService logService, string source, string message, string? details = null)
    {
        var entry = ActivityLogEntry.Warning(source, message, details);
        logService.Log(entry);
        return entry;
    }

    /// <summary>
    /// Logs an error message and returns the entry for chaining.
    /// </summary>
    public static ActivityLogEntry Error(this IActivityLogService logService, string source, string message, string? details = null)
    {
        var entry = ActivityLogEntry.Error(source, message, details);
        logService.Log(entry);
        return entry;
    }

    /// <summary>
    /// Logs an error from an exception and returns the entry for chaining.
    /// </summary>
    public static ActivityLogEntry Error(this IActivityLogService logService, string source, string message, Exception exception)
    {
        var entry = ActivityLogEntry.Error(source, message, exception);
        logService.Log(entry);
        return entry;
    }

    /// <summary>
    /// Starts a tracked operation and logs its start.
    /// Use with 'using' statement for automatic completion logging.
    /// </summary>
    /// <example>
    /// using var op = logService.BeginOperation("Importing files", "Import", isCancellable: true);
    /// op.ReportProgress(50, "Halfway done");
    /// </example>
    public static ProgressOperation BeginOperation(
        this IActivityLogService logService, 
        string operationName, 
        string source,
        bool isCancellable = false)
    {
        return logService.StartOperation(operationName, source, isCancellable);
    }

    /// <summary>
    /// Creates a logger facade for a specific source (component).
    /// Reduces boilerplate when logging from a single class.
    /// </summary>
    /// <example>
    /// private readonly IActivityLogger _log;
    /// public MyService(IActivityLogService logService) 
    /// {
    ///     _log = logService.ForSource("MyService");
    /// }
    /// _log.Info("Something happened");
    /// </example>
    public static IActivityLogger ForSource(this IActivityLogService logService, string source)
    {
        return new SourcedActivityLogger(logService, source);
    }
}

/// <summary>
/// A logger that automatically includes the source in all log calls.
/// </summary>
public interface IActivityLogger
{
    /// <summary>
    /// Logs an info message.
    /// </summary>
    void Info(string message, string? details = null);

    /// <summary>
    /// Logs a success message.
    /// </summary>
    void Success(string message, string? details = null);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    void Warning(string message, string? details = null);

    /// <summary>
    /// Logs an error message.
    /// </summary>
    void Error(string message, string? details = null);

    /// <summary>
    /// Logs an error from an exception.
    /// </summary>
    void Error(string message, Exception exception);

    /// <summary>
    /// Logs a debug message.
    /// </summary>
    void Debug(string message, string? details = null);

    /// <summary>
    /// Sets the status bar message.
    /// </summary>
    void SetStatus(string message, ActivitySeverity severity = ActivitySeverity.Info);

    /// <summary>
    /// Starts a tracked operation.
    /// </summary>
    ProgressOperation BeginOperation(string operationName, bool isCancellable = false);
}

/// <summary>
/// Implementation of IActivityLogger that wraps IActivityLogService with a fixed source.
/// </summary>
internal sealed class SourcedActivityLogger : IActivityLogger
{
    private readonly IActivityLogService _logService;
    private readonly string _source;

    public SourcedActivityLogger(IActivityLogService logService, string source)
    {
        _logService = logService;
        _source = source;
    }

    public void Info(string message, string? details = null)
        => _logService.LogInfo(_source, message, details);

    public void Success(string message, string? details = null)
        => _logService.LogSuccess(_source, message, details);

    public void Warning(string message, string? details = null)
        => _logService.LogWarning(_source, message, details);

    public void Error(string message, string? details = null)
        => _logService.LogError(_source, message, details);

    public void Error(string message, Exception exception)
        => _logService.LogError(_source, message, exception);

    public void Debug(string message, string? details = null)
        => _logService.LogDebug(_source, message, details);

    public void SetStatus(string message, ActivitySeverity severity = ActivitySeverity.Info)
        => _logService.SetStatus(message, severity);

    public ProgressOperation BeginOperation(string operationName, bool isCancellable = false)
        => _logService.StartOperation(operationName, _source, isCancellable);
}
