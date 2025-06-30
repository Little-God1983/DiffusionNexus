using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LogViewModel : ViewModelBase
    {
        private readonly ILogEventService _service;
        private readonly ObservableCollection<LogEntry> _visibleEntries = new();
        public ObservableCollection<LogEntry> VisibleEntries => _visibleEntries;

        private Window? _window;

        public LogViewModel() : this(LogEventService.Instance) { }

        public LogViewModel(ILogEventService service)
        {
            _service = service;
            _service.PropertyChanged += ServiceOnPropertyChanged;
            _service.Entries.CollectionChanged += EntriesOnCollectionChanged;
            foreach (var e in _service.Entries)
                _visibleEntries.Add(e);
            _service.Publish(LogSeverity.Info, "Log service initialized.");
            _service.Publish(LogSeverity.Info, "Log service ready.");

            ExportLogCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
            CopyLogCommand = new AsyncRelayCommand(CopyAsync, () => CanCopy);
        }

        public ObservableCollection<LogEntry> Entries => _service.Entries;

        public LogEntry? LatestEntry => _service.LatestEntry;

        [ObservableProperty]
        private LogSeverity? _selectedSeverity;

        public Array SeverityOptions { get; } = Enum.GetValues(typeof(LogSeverity));

        [ObservableProperty]
        private string _buttonText = OverlayButtonText;

        public const string OverlayButtonText = "↑ Expand log";
        public const string HideOverlayButtonText = "↓ Hide log";

        private bool _isOverlayVisible;
        public bool IsOverlayVisible
        {
            get => _isOverlayVisible;
            set
            {
                if (_isOverlayVisible != value)
                {
                    _isOverlayVisible = value;
                    ButtonText = value == true ? HideOverlayButtonText : OverlayButtonText;
                    System.Diagnostics.Debug.WriteLine($"IsOverlayVisible changed to: {value}");
                    OnPropertyChanged();  // Explicit notification
                }
            }
        }

        partial void OnSelectedSeverityChanged(LogSeverity? value)
        {
            ApplyFilter();
        }

        private void EntriesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (LogEntry entry in e.NewItems)
                {
                    if (IsVisible(entry))
                        _visibleEntries.Add(entry);
                }
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(CanCopy));
                ExportLogCommand.NotifyCanExecuteChanged();
                CopyLogCommand.NotifyCanExecuteChanged();
            }
        }

        private bool IsVisible(LogEntry entry) => !_selectedSeverity.HasValue || entry.Severity == _selectedSeverity.Value;

        private void ApplyFilter()
        {
            _visibleEntries.Clear();
            foreach (var entry in _service.Entries)
                if (IsVisible(entry))
                    _visibleEntries.Add(entry);
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CanCopy));
            ExportLogCommand.NotifyCanExecuteChanged();
            CopyLogCommand.NotifyCanExecuteChanged();
        }

        public void SetWindow(Window window) => _window = window;

        public bool CanExport => _visibleEntries.Count > 0;
        public bool CanCopy => _visibleEntries.Count > 0;

        public IAsyncRelayCommand ExportLogCommand { get; }
        public IAsyncRelayCommand CopyLogCommand { get; }

        private async Task ExportAsync()
        {
            if (_window == null) return;
            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "log.txt" });
            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllLines(path, _visibleEntries.Select(e => e.ToLine()));
            }
        }

        private async Task CopyAsync()
        {
            if (_window?.Clipboard != null)
            {
                var text = string.Join(Environment.NewLine, _visibleEntries.Select(e => e.ToLine()));
                await _window.Clipboard.SetTextAsync(text);
            }
        }

        private void ServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ILogEventService.LatestEntry))
            {
                OnPropertyChanged(nameof(LatestEntry));
            }
        }
    }
}
