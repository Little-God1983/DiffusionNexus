using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Services;
using DiffusionNexus.UI.Classes;
using System;
using System.Diagnostics;
using System.IO;
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
        public IDialogService DialogService { get; set; } = null!;
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
            return !string.IsNullOrEmpty(BasePath) && !string.IsNullOrEmpty(TargetPath);
        }

        private void ShowMessageAndResetUI(string message, string caption)
        {
            // TODO: Write in UI log
            ResetUI();
        }

        private void SetProcessingUIState()
        {
            _isProcessing = true;
            //TODO: Show Progress bar Overlay
            //TODO: Expand log in MainView
            //TODO: Show Cancel button
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

                //TODO: If user wants to copy files, we need to check if there is enough disk space available.
                //if ((bool)radioCopy.IsChecked && !controllerService.EnoughFreeSpaceOnDisk(txtBasePath.Text, txtTargetPath.Text))
                //{
                //Todo: Write in UI log
                //"You don't have enough disk space to copy the files.", "Insuficcent Diskspace"
                return;
                //}

                bool moveOperation = false;
                //If user selected "Move" operation instead of "Copy", we need to handle that.
                //if (!(bool)radioCopy.IsChecked)
                //{
                //TODO: Show confirmation dialog "Moving instead of copying means that the original file order cannot be restored. Continue anyways?", "Are you sure?"
                //if user selects "No" then return and reset UI;
                //if ()
                //{
                //    ResetUI();
                //    return;
                //}
                moveOperation = true;
                //}

                //TODO: Prepare progress indicator and start Pogressing
                //await controllerService.ComputeFolder(progressIndicator, _cts.Token, new SelectedOptions()
                //{
                    //BasePath = BasePath,
                    //TargetPath = TargetPath,
                    //IsMoveOperation = moveOperation,
                    //TODO: GET these values from UI
                    //OverrideFiles = (bool)chbOverride.IsChecked,
                    //CreateBaseFolders = (bool)chbBaseFolders.IsChecked,
                    //UseCustomMappings = (bool)chbCustom.IsChecked,
                    //ApiKey = SettingsManager.LoadApiKey()
                //});
            }

            catch (OperationCanceledException)
            {
                //TODO: Write in UI log
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unexpected error: {ex.Message}");
                //Log.Error($"Unexpected error: {ex.Message}");
            }
            finally
            {
                ResetUI();
            }
        }
        private void ResetUI()
        {
            _isProcessing = false;
        }

        private bool IsPathTheSame()
        {
            return string.Compare(
                Path.GetFullPath(BasePath).TrimEnd('\\'),
                Path.GetFullPath(TargetPath).TrimEnd('\\'),
                StringComparison.InvariantCultureIgnoreCase) == 0;
        }

    }
}
