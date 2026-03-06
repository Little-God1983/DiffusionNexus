namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Represents a single entry in the unified log.
/// Immutable record that captures full context for every log event.
/// </summary>
/// <param name="Timestamp">When the event occurred (UTC).</param>
/// <param name="Level">Severity level.</param>
/// <param name="Category">Functional area that produced the entry.</param>
/// <param name="Source">Class or method name that produced the log.</param>
/// <param name="Message">Human-readable description of the event.</param>
/// <param name="Detail">Optional extended info, stack traces, etc.</param>
/// <param name="TaskId">Links to a tracked task if applicable.</param>
/// <param name="Exception">The exception associated with this entry, if any.</param>
public sealed record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Source,
    string Message,
    string? Detail = null,
    string? TaskId = null,
    Exception? Exception = null)
{
    /// <summary>
    /// Formats the entry for single-line display.
    /// </summary>
    public string ToDisplayString()
        => $"[{Timestamp:HH:mm:ss}] {LevelAbbreviation} [{Category}] {Source}: {Message}";

    /// <summary>
    /// Gets a short abbreviation for the log level suitable for display.
    /// </summary>
    public string LevelAbbreviation => Level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Fatal => "FTL",
        _ => "???"
    };
}
