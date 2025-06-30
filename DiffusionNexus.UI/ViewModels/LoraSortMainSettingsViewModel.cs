using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using DiffusionNexus.UI.Classes;
using Avalonia.Platform.Storage;
using System.Linq;
using System.Threading.Tasks;
using DiffusionNexus.LoraSort.Service.Services;
using DiffusionNexus.LoraSort.Service.Classes;
using System.Threading;
using DiffusionNexus.UI.Models;
using System;

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
        private readonly ILogEventService _logService;
        private Window? _window;

        public IAsyncRelayCommand SelectBasePathCommand { get; }
        public IAsyncRelayCommand SelectTargetPathCommand { get; }
        public IRelayCommand GoCommand { get; }

        public LoraSortMainSettingsViewModel() : this(new SettingsService(), LogEventService.Instance)
        {
        }

        public LoraSortMainSettingsViewModel(ISettingsService settingsService, ILogEventService logService)
        {
            _settingsService = settingsService;
            _logService = logService;
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

        partial void OnStatusTextChanged(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                _logService.Publish(LogSeverity.Info, value);
        }

        partial void OnProgressChanged(double value)
        {
            _logService.Publish(LogSeverity.Info, $"Progress: {value}%");
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

        private async void OnGo()
        {
            if (string.IsNullOrEmpty(BasePath) || string.IsNullOrEmpty(TargetPath))
            {
                StatusText = "Base or Target path missing";
                return;
            }

            var options = new SelectedOptions
            {
                BasePath = BasePath!,
                TargetPath = TargetPath!,
                IsMoveOperation = !IsCopyMode,
                OverrideFiles = OverrideFiles,
                CreateBaseFolders = CreateBaseFolders,
                UseCustomMappings = UseCustomMappings
            };

            var controller = new FileControllerService();
            var progress = new Progress<ProgressReport>(HandleProgress);
            ActionButtonText = "Running";
            await controller.ComputeFolder(progress, CancellationToken.None, options);
            ActionButtonText = "Go";
        }

        private void HandleProgress(ProgressReport report)
        {
            if (report.Percentage.HasValue)
                Progress = report.Percentage.Value;
            if (!string.IsNullOrEmpty(report.StatusMessage))
                StatusText = report.StatusMessage;
            if (!string.IsNullOrEmpty(report.StatusMessage))
                _logService.Publish(LogSeverity.Info, report.StatusMessage);
        }
    }
}
