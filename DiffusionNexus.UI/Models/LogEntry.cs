using System;

namespace DiffusionNexus.UI.Models
{
    public enum LogSeverity
    {
        Info,
        Warning,
        Error,
        Debug,
        Success
    }

    public class LogEntry
    {
        public LogEntry(DateTime timestamp, LogSeverity severity, string message)
        {
            Timestamp = timestamp;
            Severity = severity;
            Message = message;
        }

        public DateTime Timestamp { get; }
        public LogSeverity Severity { get; }
        public string Message { get; }

        public string ToStringLine() => $"[{Timestamp:HH:mm:ss}] [{Severity.ToString().ToUpper()}] {Message}";
    }
}
