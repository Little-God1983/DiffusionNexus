using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Updated ViewModel demonstrating seamless database integration
/// This can replace the existing LoraSortMainSettingsViewModel
/// </summary>
public partial class EnhancedLoraSortMainSettingsViewModel : ViewModelBase
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
    private bool deleteEmptySourceFolders;
    [ObservableProperty]
    private bool useCustomMappings;
    [ObservableProperty]
    private bool useDatabaseCache = true;  // NEW: Option to use database
    [ObservableProperty]
    private double progress;
    [ObservableProperty]
    private string? statusText;
    [ObservableProperty]
    private bool isBusy;
    [ObservableProperty]
    private bool isIndeterminate = true;
    [ObservableProperty]
    private string? databaseStats;  // NEW: Show database info

    private readonly ISettingsService _settingsService;
    public IDialogService DialogService { get; set; } = null!;
    private Window? _window;
    private MainWindowViewModel? _mainWindowVm;
    private bool _originalLogExpanded;

    public IAsyncRelayCommand SelectBasePathCommand { get; }
    public IAsyncRelayCommand SelectTargetPathCommand { get; }
    public IRelayCommand GoCommand { get; }
    public IAsyncRelayCommand ImportToDatabaseCommand { get; }  // NEW: Import to DB
    public IAsyncRelayCommand RefreshDatabaseStatsCommand { get; }  // NEW: Show stats

    private CancellationTokenSource _cts;
    private bool _isProcessing = false;

    public EnhancedLoraSortMainSettingsViewModel() : this(new SettingsService())
    {
    }

    public EnhancedLoraSortMainSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SelectBasePathCommand = new AsyncRelayCommand(OnSelectBasePathAsync);
        SelectTargetPathCommand = new AsyncRelayCommand(OnSelectTargetPathAsync);
        GoCommand = new AsyncRelayCommand(OnGo, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ImportToDatabaseCommand = new AsyncRelayCommand(ImportToDatabaseAsync);
        RefreshDatabaseStatsCommand = new AsyncRelayCommand(RefreshDatabaseStatsAsync);
        
        _ = LoadDefaultsAsync();
        _ = RefreshDatabaseStatsAsync();  // Show initial stats
    }

    private async Task LoadDefaultsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        BasePath = settings.LoraSortSourcePath;
        TargetPath = settings.LoraSortTargetPath;
        DeleteEmptySourceFolders = settings.DeleteEmptySourceFolders;
        UseDatabaseCache = settings.UseDatabaseCache ?? true;  // NEW: Load preference
    }

    // NEW: Import existing files to database
    private async Task ImportToDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(BasePath))
        {
            await ShowDialog("Please select a base path first", "No Path Selected");
            return;
        }

        try
        {
            IsBusy = true;
            IsIndeterminate = true;
            StatusText = "Importing to database...";

            var settings = await _settingsService.LoadAsync();
            var importService = ServiceFactory.CreateImportService(settings.CivitaiApiKey ?? "");

            var progress = new Progress<ProgressReport>(report =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (report.Percentage.HasValue)
                    {
                        IsIndeterminate = false;
                        Progress = report.Percentage.Value;
                    }
                    StatusText = report.StatusMessage;
                    Log(report.StatusMessage ?? "", report.LogLevel);
                });
            });

            await importService.ImportDirectoryAsync(BasePath, progress);
            await RefreshDatabaseStatsAsync();
            
            Log("Import complete!", LogSeverity.Success);
            await ShowDialog("Import to database complete!", "Success");
        }
        catch (Exception ex)
        {
            Log($"Import failed: {ex.Message}", LogSeverity.Error);
            await ShowDialog($"Import failed: {ex.Message}", "Error");
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = true;
            Progress = 0;
            StatusText = null;
        }
    }

    // NEW: Show database statistics
    private async Task RefreshDatabaseStatsAsync()
    {
        try
        {
            var context = ServiceFactory.GetOrCreateDbContext();
            var modelCount = await context.Models.CountAsync();
            var fileCount = await context.ModelFiles.Where(f => f.LocalFilePath != null).CountAsync();
            
            var dbPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiffusionNexus", "diffusion_nexus.db");
            
            var dbSize = System.IO.File.Exists(dbPath) 
                ? new System.IO.FileInfo(dbPath).Length / 1024.0 / 1024.0 
                : 0;
            
            DatabaseStats = $"DB: {modelCount} models, {fileCount} local files, {dbSize:F1} MB";
        }
        catch
        {
            DatabaseStats = "Database not initialized";
        }
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

    private async Task StartProcessingAsync()
    {
        try
        {
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

            // UPDATED: Use ServiceFactory to create controller with database support
            var settings = await _settingsService.LoadAsync();
            
            if (IsCopyMode)
            {
                var tempController = ServiceFactory.CreateFileController(settings.CivitaiApiKey ?? "", useDatabase: false);
                if (!tempController.EnoughFreeSpaceOnDisk(BasePath!, TargetPath!))
                {
                    Log("Insufficient disk space.", LogSeverity.Warning);
                    await ShowDialog("You don't have enough disk space to copy the files.", "Insufficient Disk Space");
                    return;
                }
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

            SetProcessingUIState();

            var options = new SelectedOptions
            {
                BasePath = BasePath!,
                TargetPath = TargetPath!,
                IsMoveOperation = !IsCopyMode,
                OverrideFiles = OverrideFiles,
                CreateBaseFolders = CreateBaseFolders,
                DeleteEmptySourceFolders = DeleteEmptySourceFolders,
                UseCustomMappings = UseCustomMappings,
                ApiKey = settings.CivitaiApiKey ?? string.Empty
            };

            // UPDATED: Create controller with database support
            var controllerService = ServiceFactory.CreateFileController(
                options.ApiKey, 
                useDatabase: UseDatabaseCache);

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
                            Log("Processing…", LogSeverity.Info);
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
            
            if (DeleteEmptySourceFolders)
            {
                Log("Cleaning up empty folders…", LogSeverity.Info);
                await controllerService.DeleteEmptyDirectoriesAsync(BasePath!);
            }
            
            Log("Finalising…", LogSeverity.Info);
            
            // Refresh stats after processing
            await RefreshDatabaseStatsAsync();
        }
        catch (OperationCanceledException)
        {
            Log("Operation cancelled by user.", LogSeverity.Warning);
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

    // Helper methods (same as original)
    public void SetWindow(Window window) => _window = window;
    public void SetMainWindowViewModel(MainWindowViewModel vm) => _mainWindowVm = vm;
    internal bool ValidatePaths() => !string.IsNullOrWhiteSpace(BasePath) && !string.IsNullOrWhiteSpace(TargetPath);
    internal bool IsPathTheSame() => string.Compare(
        System.IO.Path.GetFullPath(BasePath).TrimEnd('\\'),
        System.IO.Path.GetFullPath(TargetPath).TrimEnd('\\'),
        StringComparison.InvariantCultureIgnoreCase) == 0;

    private void SetProcessingUIState()
    {
        _isProcessing = true;
        IsBusy = true;
        _originalLogExpanded = _mainWindowVm?.IsLogExpanded ?? false;
        if (_mainWindowVm != null)
            _mainWindowVm.IsLogExpanded = true;
        _cts = new CancellationTokenSource();
    }

    private void ResetUI()
    {
        _isProcessing = false;
        IsBusy = false;
        Progress = 0;
        StatusText = null;
        _cts?.Dispose();
        _cts = null!;
    }

    private void RestUIAndCloseLog()
    {
        ResetUI();
        if (_mainWindowVm != null)
            _mainWindowVm.IsLogExpanded = _originalLogExpanded;
    }

    private async Task ShowDialog(string message, string caption)
    {
        if (_window == null) return;
        // Dialog implementation same as original
    }

    private async Task ShowMessageAndResetUI(string message, string caption)
    {
        Log(message, LogSeverity.Warning);
        await ShowDialog(message, caption);
        RestUIAndCloseLog();
    }

    // Placeholder for actual Select* implementations
    private Task OnSelectBasePathAsync() => Task.CompletedTask;
    private Task OnSelectTargetPathCommand() => Task.CompletedTask;
}
