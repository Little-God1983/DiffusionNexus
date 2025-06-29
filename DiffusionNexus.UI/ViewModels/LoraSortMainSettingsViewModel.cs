using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Services;
using DiffusionNexus.UI.Classes;
using System;
using System.Linq;
using System.Threading;
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

        private CancellationTokenSource _cts;
        private bool _isProcessing = false;

        public LoraSortMainSettingsViewModel() : this(new SettingsService())
        {
        }

        public LoraSortMainSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            SelectBasePathCommand = new AsyncRelayCommand(OnSelectBasePathAsync);
            SelectTargetPathCommand = new AsyncRelayCommand(OnSelectTargetPathAsync);
            GoCommand = new AsyncRelayCommand(OnGo);
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

        private async Task OnGo()
        {
            if (!_isProcessing)
            {
                await StartProcessingAsync();
            }
            else
            {
                _cts?.Cancel();
            }

            // TODO: Implement main action logic
            StatusText = "Go clicked (not implemented)";
            Progress = 0;
        }

        private bool ValidatePaths()
        {
            return !string.IsNullOrEmpty(txtBasePath.Text) && !string.IsNullOrEmpty(txtTargetPath.Text);
        }

        private void ShowMessageAndResetUI(string message, string caption)
        {
            MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            ResetUI();
        }

        private void SetProcessingUIState()
        {
            _isProcessing = true;
            btnGoCancel.Content = "Cancel";
            if (DataContext is MainViewModel vm)
            {
                vm.ClearLogs();
            }
            btnTargetPath.IsEnabled = false;
            btnBasePath.IsEnabled = false;
            _cts = new CancellationTokenSource();
        }

        private async Task StartProcessingAsync()
        {
            try
            {
                SetProcessingUIState();

                if (!ValidatePaths())
                {
                    ShowMessageAndResetUI("No path selected", "No Path");
                    return;
                }

                if (IsPathTheSame())
                {
                    ShowMessageAndResetUI("Select a different target than the source path.", "Source cannot be target path");
                    return;
                }

                var controllerService = new FileControllerService();
                if ((bool)radioCopy.IsChecked && !controllerService.EnoughFreeSpaceOnDisk(txtBasePath.Text, txtTargetPath.Text))
                {
                    ShowMessageAndResetUI("You don't have enough disk space to copy the files.", "Insuficcent Diskspace");
                    return;
                }

                bool moveOperation = false;
                if (!(bool)radioCopy.IsChecked)
                {
                    if (!ShowConfirmationDialog("Moving instead of copying means that the original file order cannot be restored. Continue anyways?", "Are you sure?"))
                    {
                        ResetUI();
                        return;
                    }
                    moveOperation = true;
                }

                var progressIndicator = CreateProgressIndicator();
                await controllerService.ComputeFolder(progressIndicator, _cts.Token, new SelectedOptions()
                {
                    BasePath = txtBasePath.Text,
                    TargetPath = txtTargetPath.Text,
                    IsMoveOperation = moveOperation,
                    OverrideFiles = (bool)chbOverride.IsChecked,
                    CreateBaseFolders = (bool)chbBaseFolders.IsChecked,
                    UseCustomMappings = (bool)chbCustom.IsChecked,
                    ApiKey = SettingsManager.LoadApiKey()
                });
            }
            catch (OperationCanceledException)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.AppendLog("Operation was canceled by user.", isError: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected error: {ex.Message}");
            }
            finally
            {
                ResetUI();
            }
        }
    }
}
