namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Centralized logging service that all application logging flows through.
/// Replaces scattered Console.WriteLine, Debug.WriteLine, and ad-hoc logging.
/// Registered as a singleton in DI.
/// </summary>
public interface IUnifiedLogger
{
    #region Basic Logging

    /// <summary>
    /// Logs an entry with full control over all parameters.
    /// </summary>
    void Log(LogLevel level, LogCategory category, string source, string message,
             string? detail = null, Exception? ex = null, string? taskId = null);

    /// <summary>
    /// Logs a trace-level message.
    /// </summary>
    void Trace(LogCategory category, string source, string message, string? detail = null);

    /// <summary>
    /// Logs a debug-level message.
    /// </summary>
    void Debug(LogCategory category, string source, string message, string? detail = null);

    /// <summary>
    /// Logs an info-level message.
    /// </summary>
    void Info(LogCategory category, string source, string message, string? detail = null);

    /// <summary>
    /// Logs a warning-level message.
    /// </summary>
    void Warn(LogCategory category, string source, string message, string? detail = null);

    /// <summary>
    /// Logs an error-level message with optional exception.
    /// </summary>
    void Error(LogCategory category, string source, string message, Exception? ex = null);

    /// <summary>
    /// Logs a fatal-level message that typically requires application restart.
    /// </summary>
    void Fatal(LogCategory category, string source, string message, Exception ex);

    #endregion

    #region Observable Stream

    /// <summary>
    /// Observable stream of log entries for real-time UI binding.
    /// New subscribers receive only future entries (hot observable).
    /// </summary>
    IObservable<LogEntry> LogStream { get; }

    #endregion

    #region Query

    /// <summary>
    /// Retrieves existing log entries with optional category and minimum level filters.
    /// </summary>
    IReadOnlyList<LogEntry> GetEntries(LogCategory? category = null, LogLevel? minLevel = null);

    #endregion

    #region Lifecycle

    /// <summary>
    /// Clears all retained log entries.
    /// </summary>
    void Clear();

    #endregion
}
