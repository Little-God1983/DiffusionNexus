using System.Collections.ObjectModel;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Classes
{
    public interface ILogEventService
    {
        ObservableCollection<LogEntry> Entries { get; }
        LogEntry? LatestEntry { get; }
        void Publish(LogLevel level, string message);
    }
}
