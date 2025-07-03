

namespace DiffusionNexus.Service.Classes
{
    public class ProgressReport
    {
        public int? Percentage { get; set; }
        public string StatusMessage { get; set; } = String.Empty;
        public bool? IsSuccessful { get; set; }
        public LogSeverity LogLevel { get; set; } = LogSeverity.Info;
    }
}
