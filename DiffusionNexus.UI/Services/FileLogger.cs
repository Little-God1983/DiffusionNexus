using System.Runtime.CompilerServices;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Simple file logger that writes to a log file next to the application executable.
/// Thread-safe implementation for use across the application.
/// </summary>
public static class FileLogger
{
    private static readonly object _lock = new();
    private static readonly string _logFilePath = string.Empty;
    private static bool _isEnabled = true;

    static FileLogger()
    {
        try
        {
            // Get the directory where the exe is located
            var exePath = AppContext.BaseDirectory;
            _logFilePath = Path.Combine(exePath, "DiffusionNexus.log");
            
            // Clear old log and write startup header
            File.WriteAllText(_logFilePath, $"=== Application Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            WriteLogInternal($"Log file: {_logFilePath}");
        }
        catch
        {
            _isEnabled = false;
        }
    }

    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    public static string LogFilePath => _logFilePath;

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Log(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        var fileName = Path.GetFileName(filePath);
        WriteLogInternal($"[INFO] [{fileName}:{lineNumber}] {memberName}: {message}");
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public static void LogWarning(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        var fileName = Path.GetFileName(filePath);
        WriteLogInternal($"[WARN] [{fileName}:{lineNumber}] {memberName}: {message}");
    }

    /// <summary>
    /// Logs an error message.
    /// </summary>
    public static void LogError(
        string message,
        Exception? ex = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        var fileName = Path.GetFileName(filePath);
        var exMessage = ex is not null ? $" Exception: {ex.GetType().Name}: {ex.Message}" : "";
        WriteLogInternal($"[ERROR] [{fileName}:{lineNumber}] {memberName}: {message}{exMessage}");
        
        if (ex?.StackTrace is not null)
        {
            WriteLogInternal($"[STACK] {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Logs method entry for tracing.
    /// </summary>
    public static void LogEntry(
        string? parameters = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        var fileName = Path.GetFileName(filePath);
        var paramText = string.IsNullOrEmpty(parameters) ? "" : $" ({parameters})";
        WriteLogInternal($"[TRACE] [{fileName}] --> {memberName}{paramText}");
    }

    /// <summary>
    /// Logs method exit for tracing.
    /// </summary>
    public static void LogExit(
        string? result = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        var fileName = Path.GetFileName(filePath);
        var resultText = string.IsNullOrEmpty(result) ? "" : $" => {result}";
        WriteLogInternal($"[TRACE] [{fileName}] <-- {memberName}{resultText}");
    }

    private static void WriteLogInternal(string message)
    {
        if (!_isEnabled) return;

        try
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var threadId = Environment.CurrentManagedThreadId;
                var logLine = $"{timestamp} [T{threadId:D3}] {message}";
                
                // Use FileStream with explicit flush to ensure immediate write
                using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(logLine);
                writer.Flush();
                stream.Flush();
            }
        }
        catch
        {
            // Silently fail if we can't write to log
        }
    }

    /// <summary>
    /// Clears the log file.
    /// </summary>
    public static void ClearLog()
    {
        if (!_isEnabled) return;

        try
        {
            lock (_lock)
            {
                File.WriteAllText(_logFilePath, $"=== Log cleared at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
        }
        catch
        {
            // Silently fail
        }
    }
}
