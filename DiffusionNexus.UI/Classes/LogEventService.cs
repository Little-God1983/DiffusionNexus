using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.LoraSort.Service.Classes;

namespace DiffusionNexus.UI.Classes
{
    public class LogEventService : ObservableObject, ILogEventService
    {
        private readonly ObservableCollection<LogEntry> _entries = new();
        private LogEntry? _latestEntry;

        public static LogEventService Instance { get; } = new LogEventService();

        private LogEventService() { }

        public ObservableCollection<LogEntry> Entries => _entries;

        public LogEntry? LatestEntry
        {
            get => _latestEntry;
            private set => SetProperty(ref _latestEntry, value);
        }

        public void Publish(LogSeverity severity, string message)
        {
            var entry = new LogEntry(DateTime.Now, severity, message);
            _entries.Add(entry);
            LatestEntry = entry;
        }
    }
}
