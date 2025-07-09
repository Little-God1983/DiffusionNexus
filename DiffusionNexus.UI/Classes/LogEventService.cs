using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Service.Classes;

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
            
            // Ensure collection modifications are always performed on the UI thread
            if (Dispatcher.UIThread.CheckAccess())
            {
                // Already on UI thread, safe to execute directly
                _entries.Add(entry);
                LatestEntry = entry;
            }
            else
            {
                // Not on UI thread, dispatch to UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    _entries.Add(entry);
                    LatestEntry = entry;
                });
            }
        }

        public void Clear()
        {
            // Ensure collection modifications are always performed on the UI thread
            if (Dispatcher.UIThread.CheckAccess())
            {
                // Already on UI thread, safe to execute directly
                _entries.Clear();
                LatestEntry = null;
            }
            else
            {
                // Not on UI thread, dispatch to UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    _entries.Clear();
                    LatestEntry = null;
                });
            }
        }
    }
}
