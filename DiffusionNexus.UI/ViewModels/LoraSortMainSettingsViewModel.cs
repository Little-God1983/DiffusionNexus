using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Media;
using System;
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
        private bool isBusy;
        [ObservableProperty]
        private bool isIndeterminate = true;

        private readonly ISettingsService _settingsService;
        public IDialogService DialogService { get; set; } = null!;
        private Window? _window;
        private MainWindowViewModel? _mainWindowVm;
        private bool _originalLogExpanded;

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

        public void SetMainWindowViewModel(MainWindowViewModel vm)
        {
            _mainWindowVm = vm;
        }

        private async Task ShowDialog(string message, string caption)
        {
            if (_window == null)
                return;
            var dialog = new Window
            {
                Width = 300,
                Height = 150,
                Title = caption,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            ok.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(10),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    ok
                }
            };
            await dialog.ShowDialog(_window);
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
        }

        internal bool ValidatePaths()
        {
            return !string.IsNullOrWhiteSpace(BasePath) && !string.IsNullOrWhiteSpace(TargetPath);
        }

        private async Task ShowMessageAndResetUI(string message, string caption)
        {
            Log(message, LogSeverity.Warning);
            await ShowDialog(message, caption);
            RestUIAndCloseLog();
        }

        private void SetProcessingUIState()
        {
            _isProcessing = true;
            IsBusy = true;
            _originalLogExpanded = _mainWindowVm?.IsLogExpanded ?? false;
            if (_mainWindowVm != null)
                _mainWindowVm.IsLogExpanded = true;
            _cts = new CancellationTokenSource();
        }

        private async Task StartProcessingAsync()
        {
            try
            {
                SetProcessingUIState();

                if (!ValidatePaths())
                {
                    await ShowMessageAndResetUI("No path selected", "No Path");
                    return;
                }

                if (IsPathTheSame())
                {
                    await ShowMessageAndResetUI("Select a different target than the source path.", "Source cannot be target path");
                    return;
                }

                var controllerService = new FileControllerService();

                if (IsCopyMode && !controllerService.EnoughFreeSpaceOnDisk(BasePath!, TargetPath!))
                {
                    Log("Insufficient disk space.", LogSeverity.Warning);
                    await ShowDialog("You don't have enough disk space to copy the files.", "Insufficient Disk Space");
                    return;
                }

                if (!IsCopyMode)
                {
                    var move = await DialogService.ShowYesNoAsync("Moving instead of copying means that the original file order cannot be restored. Continue anyways?", "Are you sure?");
                    if (!move)
                    {
                        RestUIAndCloseLog();
                        return;
                    }
                }

                var settings = await _settingsService.LoadAsync();
                var options = new SelectedOptions
                {
                    BasePath = BasePath!,
                    TargetPath = TargetPath!,
                    IsMoveOperation = !IsCopyMode,
                    OverrideFiles = OverrideFiles,
                    CreateBaseFolders = CreateBaseFolders,
                    UseCustomMappings = UseCustomMappings,
                    ApiKey = settings.CivitaiApiKey ?? string.Empty
                };

                Log("Scanning…", LogSeverity.Info);
                IsIndeterminate = true;
                var first = true;
                var progress = new Progress<ProgressReport>(report =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (report.Percentage.HasValue)
                        {
                            if (first)
                            {
                                IsIndeterminate = false;
                                Log("Copying…", LogSeverity.Info);
                                first = false;
                            }
                            Progress = report.Percentage.Value;
                        }
                        if (!string.IsNullOrWhiteSpace(report.StatusMessage))
                        {
                            Log(report.StatusMessage, report.LogLevel);
                        }
                    });
                });

                await controllerService.ComputeFolder(progress, _cts.Token, options);
                Log("Finalising…", LogSeverity.Info);
            }

            catch (OperationCanceledException)
            {
                Log("Operation cancelled by user.",LogSeverity.Warning);
            }
            catch (Exception ex)
            {
                Log($"Unexpected error: {ex.Message}", LogSeverity.Error);
                await ShowDialog("Unexpected error – see log for details.", "Error");
            }
            finally
            {
                ResetUI();
                Log("Done Processing", LogSeverity.Success);
            }
        }
        private void ResetUI()
        {
            _isProcessing = false;
            IsBusy = false;
            Progress = 0;
            StatusText = null;
            _cts?.Dispose();
            _cts = null!;
            if (_mainWindowVm != null)
                _mainWindowVm.IsLogExpanded = _originalLogExpanded;
        }
        private void RestUIAndCloseLog()
        {             ResetUI();
            if (_mainWindowVm != null)
                _mainWindowVm.IsLogExpanded = _originalLogExpanded;
        }


        internal bool IsPathTheSame()
        {
            return string.Compare(
                Path.GetFullPath(BasePath).TrimEnd('\\'),
                Path.GetFullPath(TargetPath).TrimEnd('\\'),
                StringComparison.InvariantCultureIgnoreCase) == 0;
        }

    }
}
