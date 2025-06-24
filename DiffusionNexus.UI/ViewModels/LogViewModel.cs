using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LogViewModel : ObservableObject
    {
        private readonly ILogEventService _service;

        public LogViewModel() : this(LogEventService.Instance) { }

        public LogViewModel(ILogEventService service)
        {
            _service = service;
            _service.PropertyChanged += ServiceOnPropertyChanged;
            _service.Publish(LogLevel.Info, "Log service initialized.");
            _service.Publish(LogLevel.Success, "Log service ready.");
        }

        public ObservableCollection<LogEntry> Entries => _service.Entries;

        public LogEntry? LatestEntry => _service.LatestEntry;

        [ObservableProperty]
        private bool _isOverlayVisible;

        [RelayCommand]
        private void ToggleOverlay() => IsOverlayVisible = !IsOverlayVisible;

        private void ServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ILogEventService.LatestEntry))
            {
                OnPropertyChanged(nameof(LatestEntry));
            }
        }
    }
}
