using System.Collections.ObjectModel;
using System.ComponentModel;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Classes
{
    public interface ILogEventService : INotifyPropertyChanged
    {
        ObservableCollection<LogEntry> Entries { get; }
        LogEntry? LatestEntry { get; }
        void Publish(LogLevel level, string message);
    }
}
