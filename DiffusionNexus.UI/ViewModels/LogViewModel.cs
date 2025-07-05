using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LogViewModel : ViewModelBase
    {
        private readonly ILogEventService _service;

        public LogViewModel() : this(LogEventService.Instance) { }

        public LogViewModel(ILogEventService service)
        {
            _service = service;
            _service.Publish(LogSeverity.Info, "Log service initialized.");
            _service.PropertyChanged += ServiceOnPropertyChanged;
           
            Entries.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(VisibleEntries));
                OnPropertyChanged(nameof(HasVisibleEntries));
            };
            _service.Publish(LogSeverity.Success, "Log service ready.");
        }

        public ObservableCollection<LogEntry> Entries => _service.Entries;

        public LogEntry? LatestEntry => _service.LatestEntry;

        public IEnumerable<object?> SeverityOptions { get; } = new object?[]
        {
            null,
            LogSeverity.Info,
            LogSeverity.Success,
            new LogSeverityFilter("Info + Success", LogSeverity.Info, LogSeverity.Success),
            LogSeverity.Warning,
            LogSeverity.Error,
            new LogSeverityFilter("Warning + Errors", LogSeverity.Warning, LogSeverity.Error),
        };

        private object? _selectedFilter;
        public object? SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                if (SetProperty(ref _selectedFilter, value))
                {
                    OnPropertyChanged(nameof(VisibleEntries));
                    OnPropertyChanged(nameof(HasVisibleEntries));
                }
            }
        }

        public IEnumerable<LogEntry> VisibleEntries => SelectedFilter switch
        {
            null => Entries,
            LogSeverity sev => Entries.Where(e => e.Severity == sev),
            LogSeverityFilter filter => Entries.Where(e => filter.Severities.Contains(e.Severity)),
            _ => Entries
        };

        public bool HasVisibleEntries => VisibleEntries.Any();

        [RelayCommand]
        private async Task ExportLogAsync()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null && VisibleEntries.Any())
            {
                var path = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = "log.txt"
                });
                if (path != null)
                {
                    File.WriteAllLines(path.Path.LocalPath, VisibleEntries.Select(e => e.ToStringLine()));
                    _service.Publish(LogSeverity.Success, "log saved");
                }
                else
                {
                    _service.Publish(LogSeverity.Warning, "log export aborted by user");
                }
            }
        }

        [RelayCommand]
        private async Task CopyLogAsync()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null && VisibleEntries.Any())
            {
                var text = string.Join(Environment.NewLine, VisibleEntries.Select(e => e.ToStringLine()));
                await desktop.MainWindow.Clipboard!.SetTextAsync(text);
                _service.Publish(LogSeverity.Success, "copied to clipboard");
            }
        }

        [RelayCommand]
        private void ClearLog()
        {
            _service.Clear();
        }
        
        [ObservableProperty]
        private string _buttonText = OverlayButtonText;

        public const string OverlayButtonText = "↑ Expand log";
        public const string HideOverlayButtonText = "↓ Hide log";

        private bool _isOverlayVisible = true;
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

        private void ServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ILogEventService.LatestEntry))
            {
                OnPropertyChanged(nameof(LatestEntry));
            }
        }
    }
}
