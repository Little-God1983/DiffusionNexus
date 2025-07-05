using System.Collections.ObjectModel;
using System.ComponentModel;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.Classes
{
    public interface ILogEventService : INotifyPropertyChanged
    {
        ObservableCollection<LogEntry> Entries { get; }
        LogEntry? LatestEntry { get; }
        void Publish(LogSeverity severity, string message);
        void Clear();
    }
}
