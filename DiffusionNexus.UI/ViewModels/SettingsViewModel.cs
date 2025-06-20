using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ISettingsService _settingsService;

        [ObservableProperty]
        private string? _civitaiApiKey;

        [ObservableProperty]
        private string? _loraHelperFolderPath;

        public IRelayCommand SaveCommand { get; }
        public IRelayCommand DeleteApiKeyCommand { get; }

        public SettingsViewModel() : this(new SettingsService())
        {
        }

        public SettingsViewModel(ISettingsService service)
        {
            _settingsService = service;
            SaveCommand = new AsyncRelayCommand(SaveAsync);
            DeleteApiKeyCommand = new RelayCommand(DeleteApiKey);
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();
            CivitaiApiKey = SecureStorageHelper.DecryptString(settings.EncryptedCivitaiApiKey ?? string.Empty);
            LoraHelperFolderPath = settings.LoraHelperFolderPath;
        }

        private async Task SaveAsync()
        {
            var model = new SettingsModel
            {
                EncryptedCivitaiApiKey = string.IsNullOrWhiteSpace(CivitaiApiKey) ? null : SecureStorageHelper.EncryptString(CivitaiApiKey),
                LoraHelperFolderPath = LoraHelperFolderPath
            };
            await _settingsService.SaveAsync(model);
        }

        private void DeleteApiKey()
        {
            CivitaiApiKey = string.Empty;
        }
    }
}
