using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Linq;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly ISettingsService _settingsService;
        private Window? _window;

        [ObservableProperty]
        private SettingsModel _settings = new SettingsModel();

        public IRelayCommand SaveCommand { get; }
        public IRelayCommand DeleteApiKeyCommand { get; }
        public IAsyncRelayCommand BrowseLoraSortSourceCommand { get; }
        public IAsyncRelayCommand BrowseLoraSortTargetCommand { get; }
        public IAsyncRelayCommand<LoraHelperSourceModel?> BrowseLoraHelperSourceCommand { get; }
        public IRelayCommand AddLoraHelperSourceCommand { get; }
        public IRelayCommand<LoraHelperSourceModel?> RemoveLoraHelperSourceCommand { get; }

        private readonly ILogEventService _logEventService;

        public SettingsViewModel() : this(new SettingsService(), LogEventService.Instance)
        {
        }

        public SettingsViewModel(ISettingsService service, ILogEventService logEventService)
        {
            _logEventService = logEventService;
            _settingsService = service;
            SaveCommand = new AsyncRelayCommand(SaveAsync);
            DeleteApiKeyCommand = new RelayCommand(DeleteApiKey);
            BrowseLoraSortSourceCommand = new AsyncRelayCommand(BrowseLoraSortSourceAsync);
            BrowseLoraSortTargetCommand = new AsyncRelayCommand(BrowseLoraSortTargetAsync);
            BrowseLoraHelperSourceCommand = new AsyncRelayCommand<LoraHelperSourceModel?>(BrowseLoraHelperSourceAsync);
            AddLoraHelperSourceCommand = new RelayCommand(AddLoraHelperSource);
            RemoveLoraHelperSourceCommand = new RelayCommand<LoraHelperSourceModel?>(RemoveLoraHelperSource, source => source != null);
            _ = LoadAsync();
        }

        public void SetWindow(Window window)
        {
            _window = window;
        }

        private async Task LoadAsync()
        {
            Settings = await _settingsService.LoadAsync();
        }

        private async Task SaveAsync()
        {
            await _settingsService.SaveAsync(Settings);
            _logEventService.Publish(LogSeverity.Success, "Settings Saved");
        }

        private void DeleteApiKey()
        {
            Settings.CivitaiApiKey = string.Empty;
        }

        private async Task BrowseLoraSortSourceAsync()
        {
            if (_window is null) return;
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                Settings.LoraSortSourcePath = path;
        }

        private async Task BrowseLoraSortTargetAsync()
        {
            if (_window is null) return;
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                Settings.LoraSortTargetPath = path;
        }

        private void AddLoraHelperSource()
        {
            Settings.LoraHelperSources.Add(new LoraHelperSourceModel());
        }

        private void RemoveLoraHelperSource(LoraHelperSourceModel? source)
        {
            if (source is null)
            {
                return;
            }

            Settings.LoraHelperSources.Remove(source);
        }

        private async Task BrowseLoraHelperSourceAsync(LoraHelperSourceModel? source)
        {
            if (_window is null || source is null) return;
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                source.FolderPath = path;
            }
        }
    }
}
