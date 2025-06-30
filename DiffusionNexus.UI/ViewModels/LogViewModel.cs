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
using DiffusionNexus.LoraSort.Service.Classes;
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

        public IEnumerable<LogSeverity?> SeverityOptions { get; } = new LogSeverity?[]
        {
            null,
            LogSeverity.Info,
            LogSeverity.Success,
            LogSeverity.Warning,
            LogSeverity.Error,
            LogSeverity.Debug
        };

        private LogSeverity? _selectedSeverity;
        public LogSeverity? SelectedSeverity
        {
            get => _selectedSeverity;
            set
            {
                if (SetProperty(ref _selectedSeverity, value))
                {
                    OnPropertyChanged(nameof(VisibleEntries));
                    OnPropertyChanged(nameof(HasVisibleEntries));
                }
            }
        }

        public IEnumerable<LogEntry> VisibleEntries =>
            SelectedSeverity.HasValue
                ? Entries.Where(e => e.Severity == SelectedSeverity.Value)
                : Entries;

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
            }
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
