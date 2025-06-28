using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using DiffusionNexus.UI.Classes;
using Avalonia.Platform.Storage;
using System.Linq;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class LoraSortMainSettingsViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string? basePath;
        [ObservableProperty]
        private string? targetPath;
        [ObservableProperty]
        private bool isCopyMode = true;
        [ObservableProperty]
        private bool overrideFiles;
        [ObservableProperty]
        private bool createBaseFolders = true;
        [ObservableProperty]
        private bool useCustomMappings;
        [ObservableProperty]
        private double progress;
        [ObservableProperty]
        private string? statusText;
        [ObservableProperty]
        private string actionButtonText = "Go";

        private readonly ISettingsService _settingsService;
        private Window? _window;

        public IAsyncRelayCommand SelectBasePathCommand { get; }
        public IAsyncRelayCommand SelectTargetPathCommand { get; }
        public IRelayCommand GoCommand { get; }

        public LoraSortMainSettingsViewModel() : this(new SettingsService())
        {
        }

        public LoraSortMainSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            SelectBasePathCommand = new AsyncRelayCommand(OnSelectBasePathAsync);
            SelectTargetPathCommand = new AsyncRelayCommand(OnSelectTargetPathAsync);
            GoCommand = new RelayCommand(OnGo);
            _ = LoadDefaultsAsync();
        }

        public void SetWindow(Window window)
        {
            _window = window;
        }

        private async Task LoadDefaultsAsync()
        {
            var settings = await _settingsService.LoadAsync();
            BasePath = settings.LoraSortSourcePath;
            TargetPath = settings.LoraSortTargetPath;
        }

        private async Task OnSelectBasePathAsync()
        {
            if (_window is null) return;
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                BasePath = path;
        }

        private async Task OnSelectTargetPathAsync()
        {
            if (_window is null) return;
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                TargetPath = path;
        }

        private void OnGo()
        {
            // TODO: Implement main action logic
            StatusText = "Go clicked (not implemented)";
            Progress = 0;
        }
    }
}
