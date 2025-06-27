using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Linq;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly ISettingsService _settingsService;

        [ObservableProperty]
        private string? _civitaiApiKey;

        [ObservableProperty]
        private string? _loraHelperFolderPath;

        [ObservableProperty]
        private string? _loraSortSourcePath;

        [ObservableProperty]
        private string? _loraSortTargetPath;

        public IRelayCommand SaveCommand { get; }
        public IRelayCommand DeleteApiKeyCommand { get; }
        public IAsyncRelayCommand<Window?> BrowseLoraSortSourceCommand { get; }
        public IAsyncRelayCommand<Window?> BrowseLoraSortTargetCommand { get; }
        public IAsyncRelayCommand<Window?> BrowseLoraHelperFolderCommand { get; }

        public SettingsViewModel() : this(new SettingsService())
        {
        }

        public SettingsViewModel(ISettingsService service)
        {
            _settingsService = service;
            SaveCommand = new AsyncRelayCommand(SaveAsync);
            DeleteApiKeyCommand = new RelayCommand(DeleteApiKey);
            BrowseLoraSortSourceCommand = new AsyncRelayCommand<Window?>(BrowseLoraSortSourceAsync);
            BrowseLoraSortTargetCommand = new AsyncRelayCommand<Window?>(BrowseLoraSortTargetAsync);
            BrowseLoraHelperFolderCommand = new AsyncRelayCommand<Window?>(BrowseLoraHelperFolderAsync);
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();
            CivitaiApiKey = SecureStorageHelper.DecryptString(settings.EncryptedCivitaiApiKey ?? string.Empty);
            LoraHelperFolderPath = settings.LoraHelperFolderPath;
            LoraSortSourcePath = settings.LoraSortSourcePath;
            LoraSortTargetPath = settings.LoraSortTargetPath;
        }

        private async Task SaveAsync()
        {
            var model = new SettingsModel
            {
                EncryptedCivitaiApiKey = string.IsNullOrWhiteSpace(CivitaiApiKey) ? null : SecureStorageHelper.EncryptString(CivitaiApiKey),
                LoraHelperFolderPath = LoraHelperFolderPath,
                LoraSortSourcePath = LoraSortSourcePath,
                LoraSortTargetPath = LoraSortTargetPath
            };
            await _settingsService.SaveAsync(model);
        }

        private void DeleteApiKey()
        {
            CivitaiApiKey = string.Empty;
        }

        private async Task BrowseLoraSortSourceAsync(Window? window)
        {
            if (window is null) return;
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                LoraSortSourcePath = path;
        }

        private async Task BrowseLoraSortTargetAsync(Window? window)
        {
            if (window is null) return;
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                LoraSortTargetPath = path;
        }

        private async Task BrowseLoraHelperFolderAsync(Window? window)
        {
            if (window is null) return;
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                LoraHelperFolderPath = path;
        }
    }
}
