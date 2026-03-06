namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Severity level for unified log entries.
/// </summary>
public enum LogLevel
{
    /// <summary>Very detailed diagnostic information.</summary>
    Trace,

    /// <summary>Debug information for developers.</summary>
    Debug,

    /// <summary>General informational messages.</summary>
    Info,

    /// <summary>Something unexpected occurred but operation continued.</summary>
    Warning,

    /// <summary>An error prevented an operation from completing.</summary>
    Error,

    /// <summary>A critical failure that may require application restart.</summary>
    Fatal
}
