using System;

namespace DiffusionNexus.UI.Models
{
    public enum LogSeverity
    {
        Info,
        Warning,
        Error,
        Debug
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
        public LogSeverity Severity { get; set; }

        public string ToLine()
        {
            return $"[{Timestamp:HH:mm:ss}] [{Severity.ToString().ToUpper()}] {Message}";
        }
    }
}
