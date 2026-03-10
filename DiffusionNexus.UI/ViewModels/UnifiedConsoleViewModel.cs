using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Unified Console that replaces both the Activity Log panel
/// and the Installer Manager's Process Console. Provides filtered log entries,
/// tracked task progress, and instance management accessible from any module.
/// </summary>
public partial class UnifiedConsoleViewModel : ViewModelBase, IDisposable
{
    private readonly IUnifiedLogger _logger;
    private readonly ITaskTracker _taskTracker;
    private PackageProcessManager? _processManager;
    private IServiceProvider? _serviceProvider;
    private readonly List<LogEntry> _allEntries = [];
    private readonly object _entriesLock = new();
    private IDisposable? _logSubscription;
    private bool _disposed;

    #region Observable Properties

    [ObservableProperty]
    private ObservableCollection<LogEntry> _filteredEntries = [];

    [ObservableProperty]
    private ObservableCollection<TrackedTaskInfo> _activeTasks = [];

    [ObservableProperty]
    private ObservableCollection<TrackedTaskInfo> _taskHistory = [];

    // Level filters
    [ObservableProperty] private bool _showTrace;
    [ObservableProperty] private bool _showDebug;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showError = true;

    // Category filter
    [ObservableProperty]
    private LogCategory? _categoryFilter;

    // Task filter – when set, show only entries linked to this task
    [ObservableProperty]
    private string? _taskIdFilter;

    // Search
    [ObservableProperty]
    private string? _searchText;

    // Stats
    [ObservableProperty] private int _totalEntryCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private bool _hasActiveTasks;

    // Panel state
    [ObservableProperty] private bool _isPanelOpen;

    /// <summary>
    /// Raised when the console panel should be opened (e.g., when launching an instance).
    /// The StatusBarViewModel subscribes to this to sync its own IsLogPanelOpen.
    /// </summary>
    public event EventHandler? PanelOpenRequested;

    // Instance tabs
    [ObservableProperty]
    private ObservableCollection<InstanceTabItem> _instanceTabs = [];

    [ObservableProperty]
    private InstanceTabItem? _selectedInstance;

    [ObservableProperty]
    private bool _hasInstances;

    #endregion

    /// <summary>
    /// Available categories for the filter dropdown.
    /// </summary>
    public IReadOnlyList<LogCategory?> AvailableCategories { get; } =
        [null, .. Enum.GetValues<LogCategory>()];

    public UnifiedConsoleViewModel(IUnifiedLogger logger, ITaskTracker taskTracker)
    {
        _logger = logger;
        _taskTracker = taskTracker;

        // Subscribe to live log stream
        _logSubscription = _logger.LogStream.Subscribe(new LogObserver(this));

        // Subscribe to task changes
        _taskTracker.TaskChanged += OnTaskChanged;

        // Load existing entries
        LoadExistingEntries();
    }

    /// <summary>
    /// Initializes instance management with process manager and service provider.
    /// Called after construction when DI services are available.
    /// </summary>
    public void InitializeInstanceManagement(PackageProcessManager processManager, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (_processManager is not null) return;

        _processManager = processManager;
        _serviceProvider = serviceProvider;

        // Wire process manager events
        _processManager.RunningStateChanged += OnInstanceRunningStateChanged;
        _processManager.WebUrlDetected += OnInstanceWebUrlDetected;

        // Load installed packages
        _ = LoadInstancesAsync();
    }

    #region Initialization

    private void LoadExistingEntries()
    {
        var existing = _logger.GetEntries();
        lock (_entriesLock)
        {
            _allEntries.AddRange(existing);
        }
        UpdateCounts();
        ApplyFilters();
    }

    #endregion

    #region Log Stream Handling

    private void OnLogEntryReceived(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_entriesLock)
            {
                _allEntries.Add(entry);
            }
            UpdateCounts();

            if (ShouldInclude(entry))
            {
                FilteredEntries.Add(entry);
            }
        });
    }

    #endregion

    #region Task Tracking

    private void OnTaskChanged(object? sender, TrackedTaskInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (info.IsTerminal)
            {
                // Move from active to history
                var existing = FindActiveTask(info.TaskId);
                if (existing is not null) ActiveTasks.Remove(existing);

                // Add to history (avoid duplicates)
                if (!TaskHistory.Any(t => t.TaskId == info.TaskId))
                    TaskHistory.Insert(0, info);
            }
            else
            {
                // Update or add to active tasks
                var existing = FindActiveTask(info.TaskId);
                if (existing is null)
                {
                    ActiveTasks.Add(info);
                }
                else
                {
                    // Force UI refresh by triggering property change on the collection
                    var idx = ActiveTasks.IndexOf(existing);
                    ActiveTasks[idx] = info;
                }
            }

            HasActiveTasks = ActiveTasks.Count > 0;
        });
    }

    private TrackedTaskInfo? FindActiveTask(string taskId)
        => ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);

    #endregion

    #region Filtering

    private bool ShouldInclude(LogEntry entry)
    {
        // Level filter
        var levelOk = entry.Level switch
        {
            LogLevel.Trace => ShowTrace,
            LogLevel.Debug => ShowDebug,
            LogLevel.Info => ShowInfo,
            LogLevel.Warning => ShowWarning,
            LogLevel.Error or LogLevel.Fatal => ShowError,
            _ => true
        };
        if (!levelOk) return false;

        // Category filter
        if (CategoryFilter.HasValue && entry.Category != CategoryFilter.Value)
            return false;

        // Task filter
        if (!string.IsNullOrEmpty(TaskIdFilter) && entry.TaskId != TaskIdFilter)
            return false;

        // Text search
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText;
            if (!entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !(entry.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyFilters()
    {
        FilteredEntries.Clear();
        LogEntry[] snapshot;
        lock (_entriesLock)
        {
            snapshot = [.. _allEntries];
        }
        foreach (var entry in snapshot.Where(ShouldInclude))
        {
            FilteredEntries.Add(entry);
        }
    }

    private void UpdateCounts()
    {
        lock (_entriesLock)
        {
            TotalEntryCount = _allEntries.Count;
            WarningCount = _allEntries.Count(e => e.Level == LogLevel.Warning);
            ErrorCount = _allEntries.Count(e => e.Level is LogLevel.Error or LogLevel.Fatal);
        }
    }

    // Re-apply filters when any filter property changes
    partial void OnShowTraceChanged(bool value) => ApplyFilters();
    partial void OnShowDebugChanged(bool value) => ApplyFilters();
    partial void OnShowInfoChanged(bool value) => ApplyFilters();
    partial void OnShowWarningChanged(bool value) => ApplyFilters();
    partial void OnShowErrorChanged(bool value) => ApplyFilters();
    partial void OnCategoryFilterChanged(LogCategory? value) => ApplyFilters();
    partial void OnTaskIdFilterChanged(string? value) => ApplyFilters();
    partial void OnSearchTextChanged(string? value) => ApplyFilters();

    #endregion

    #region Commands

    [RelayCommand]
    private void ClearLog()
    {
        _logger.Clear();
        lock (_entriesLock) _allEntries.Clear();
        FilteredEntries.Clear();
        UpdateCounts();
    }

    [RelayCommand]
    private void ShowAll()
    {
        ShowTrace = true;
        ShowDebug = true;
        ShowInfo = true;
        ShowWarning = true;
        ShowError = true;
        CategoryFilter = null;
        TaskIdFilter = null;
        SearchText = null;

        // Clear instance selection
        foreach (var t in InstanceTabs) t.IsSelected = false;
        SelectedInstance = null;
    }

    [RelayCommand]
    private void ShowErrorsOnly()
    {
        ShowTrace = false;
        ShowDebug = false;
        ShowInfo = false;
        ShowWarning = true;
        ShowError = true;
    }

    [RelayCommand]
    private void CancelTask(TrackedTaskInfo? task)
    {
        if (task is not null)
            _taskTracker.CancelTask(task.TaskId);
    }

    [RelayCommand]
    private void FilterByTask(TrackedTaskInfo? task)
    {
        TaskIdFilter = task?.TaskId;
    }

    [RelayCommand]
    private void ClearTaskFilter()
    {
        TaskIdFilter = null;
    }

    [RelayCommand]
    private void TogglePanel()
    {
        IsPanelOpen = !IsPanelOpen;
    }

    [RelayCommand]
    private async Task ExportLogAsync()
    {
        var dialogService = _serviceProvider?.GetService<IDialogService>();
        if (dialogService is null) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var path = await dialogService.ShowSaveFileDialogAsync(
            "Export Log",
            $"DiffusionNexus_Log_{timestamp}.txt",
            "Text files (*.txt)|*.txt|All files (*.*)|*.*");

        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var content = FormatLogEntries(FilteredEntries);
            await File.WriteAllTextAsync(path, content);
            _logger.Info(LogCategory.General, "UnifiedConsole", $"Log exported to {path}");
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.General, "UnifiedConsole", $"Failed to export log: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Copies the currently filtered log entries to the system clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyLogToClipboardAsync()
    {
        var content = FormatLogEntries(FilteredEntries);
        if (string.IsNullOrEmpty(content)) return;

        if (_clipboardFunc is not null)
        {
            try
            {
                await _clipboardFunc(content);
                _logger.Info(LogCategory.General, "UnifiedConsole",
                    $"Copied {FilteredEntries.Count} log entries to clipboard");
            }
            catch (Exception ex)
            {
                _logger.Error(LogCategory.General, "UnifiedConsole",
                    $"Failed to copy to clipboard: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Delegate set by the View to perform clipboard operations via TopLevel.
    /// </summary>
    private Func<string, Task>? _clipboardFunc;

    /// <summary>
    /// Registers the clipboard write function from the View (which has access to TopLevel).
    /// </summary>
    public void RegisterClipboardHandler(Func<string, Task> clipboardFunc)
    {
        _clipboardFunc = clipboardFunc;
    }

    /// <summary>
    /// Formats log entries into a human-readable text block for export or clipboard.
    /// </summary>
    private static string FormatLogEntries(IEnumerable<LogEntry> entries)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        var sb = new StringBuilder();
        sb.AppendLine($"DiffusionNexus Log Export — v{version} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('─', 80));

        foreach (var entry in entries)
        {
            sb.Append($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] ");
            sb.Append($"{entry.LevelAbbreviation} ");
            sb.Append($"[{entry.Category}] ");
            sb.Append($"{entry.Source}: ");
            sb.AppendLine(entry.Message);

            if (!string.IsNullOrEmpty(entry.Detail))
            {
                foreach (var line in entry.Detail.Split('\n'))
                {
                    sb.Append("    ");
                    sb.AppendLine(line.TrimEnd());
                }
            }

            if (entry.Exception is not null)
            {
                sb.Append("    Exception: ");
                sb.AppendLine(entry.Exception.ToString());
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Instance Management

    private async Task LoadInstancesAsync()
    {
        if (_serviceProvider is null) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<DataAccess.UnitOfWork.IUnitOfWork>();
            var packages = await unitOfWork.InstallerPackages.GetAllAsync();

            Dispatcher.UIThread.Post(() =>
            {
                InstanceTabs.Clear();
                foreach (var package in packages)
                {
                    var tab = new InstanceTabItem(package.Id, package.Name, package.Type)
                    {
                        IsDefault = package.IsDefault,
                        InstallationPath = package.InstallationPath,
                        IsUpdateAvailable = package.IsUpdateAvailable,
                        IsMissing = !Directory.Exists(package.InstallationPath)
                    };

                    // Restore running state if the process is still alive
                    if (_processManager?.IsRunning(package.Id) == true)
                    {
                        tab.IsRunning = true;
                        tab.DetectedWebUrl = _processManager.GetDetectedUrl(package.Id);
                    }

                    InstanceTabs.Add(tab);
                }
                HasInstances = InstanceTabs.Count > 0;

                // Check for updates in the background after loading
                _ = CheckAllForUpdatesAsync();
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "UnifiedConsole: Failed to load instances");
        }
    }

    /// <summary>
    /// Reloads the instance list. Call after adding/removing installations.
    /// </summary>
    public Task RefreshInstancesAsync() => LoadInstancesAsync();

    [RelayCommand]
    private void LaunchInstance(InstanceTabItem? tab)
    {
        if (tab is null || _processManager is null || _serviceProvider is null) return;
        if (_processManager.IsRunning(tab.PackageId)) return;

        // Panel open + filter happens in OnInstanceRunningStateChanged
        // when PackageProcessManager fires RunningStateChanged.
        _ = LaunchInstanceCoreAsync(tab);
    }

    private async Task LaunchInstanceCoreAsync(InstanceTabItem tab)
    {
        try
        {
            using var scope = _serviceProvider!.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<DataAccess.UnitOfWork.IUnitOfWork>();
            var package = await unitOfWork.InstallerPackages.GetByIdAsync(tab.PackageId);
            if (package is null) return;

            if (string.IsNullOrWhiteSpace(package.ExecutablePath)) return;

            var fullPath = Path.Combine(package.InstallationPath, package.ExecutablePath);
            if (!File.Exists(fullPath)) return;

            _processManager!.Launch(package.Id, fullPath, package.InstallationPath, package.Arguments);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "UnifiedConsole: Failed to launch instance {Id}", tab.PackageId);
        }
    }

    [RelayCommand]
    private async Task StopInstanceAsync(InstanceTabItem? tab)
    {
        if (tab is null || _processManager is null) return;
        await _processManager.StopAsync(tab.PackageId);
    }

    [RelayCommand]
    private async Task RestartInstanceAsync(InstanceTabItem? tab)
    {
        if (tab is null || _processManager is null) return;
        await _processManager.RestartAsync(tab.PackageId);
    }

    [RelayCommand]
    private void OpenInstanceWebUi(InstanceTabItem? tab)
    {
        if (tab is null || string.IsNullOrWhiteSpace(tab.DetectedWebUrl)) return;

        try
        {
            // TODO: Linux Implementation - use xdg-open on Linux
            Process.Start(new ProcessStartInfo(tab.DetectedWebUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open Web UI at {Url}", tab.DetectedWebUrl);
        }
    }

    [RelayCommand]
    private async Task UpdateInstanceAsync(InstanceTabItem? tab)
    {
        if (tab is null || _serviceProvider is null) return;
        if (tab.IsRunning)
        {
            _logger.Log(LogLevel.Warning, LogCategory.InstanceManagement, tab.Name,
                "Cannot update while instance is running. Stop it first.");
            return;
        }

        var updateService = ResolveUpdateService(tab.Type);
        if (updateService is null)
        {
            _logger.Log(LogLevel.Warning, LogCategory.InstanceManagement, tab.Name,
                "No update service available for this installer type.");
            return;
        }

        tab.IsUpdating = true;
        var progress = new Progress<string>(msg =>
            _logger.Log(LogLevel.Info, LogCategory.InstanceManagement, tab.Name, msg));

        try
        {
            var result = await updateService.UpdateAsync(tab.InstallationPath, progress);

            if (result.Success)
            {
                tab.IsUpdateAvailable = false;
                tab.UpdateSummary = "Up to date";

                // Persist the new version hash to the database
                if (result.NewHash is not null)
                    await UpdatePackageVersionAsync(tab.PackageId, result.NewHash);

                _logger.Log(LogLevel.Info, LogCategory.InstanceManagement, tab.Name,
                    $"Update complete: {result.Message}");
            }
            else
            {
                _logger.Log(LogLevel.Error, LogCategory.InstanceManagement, tab.Name,
                    $"Update failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Update failed for {Name}", tab.Name);
            _logger.Log(LogLevel.Error, LogCategory.InstanceManagement, tab.Name,
                $"Update error: {ex.Message}");
        }
        finally
        {
            tab.IsUpdating = false;
        }
    }

    /// <summary>
    /// Checks all instances for available updates in parallel.
    /// </summary>
    private async Task CheckAllForUpdatesAsync()
    {
        var tasks = InstanceTabs.Select(tab => CheckForUpdatesAsync(tab));
        await Task.WhenAll(tasks);
    }

    private async Task CheckForUpdatesAsync(InstanceTabItem tab)
    {
        var updateService = ResolveUpdateService(tab.Type);
        if (updateService is null) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await updateService.CheckForUpdatesAsync(tab.InstallationPath, ct: cts.Token);
            Dispatcher.UIThread.Post(() =>
            {
                tab.IsUpdateAvailable = result.IsUpdateAvailable;
                tab.UpdateSummary = result.Summary;
            });

            if (result.IsUpdateAvailable)
            {
                Serilog.Log.Information("Update available for {Name}: {Summary}",
                    tab.Name, result.Summary);
            }
        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Warning("Update check timed out for {Name} at {Path}",
                tab.Name, tab.InstallationPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Update check failed for {Name} at {Path}",
                tab.Name, tab.InstallationPath);
        }
    }

    private IInstallerUpdateService? ResolveUpdateService(Domain.Enums.InstallerType type)
    {
        if (_serviceProvider is null) return null;

        var services = _serviceProvider.GetServices<IInstallerUpdateService>();
        return services.FirstOrDefault(s => s.SupportedTypes.Contains(type));
    }

    private async Task UpdatePackageVersionAsync(int packageId, string newHash)
    {
        if (_serviceProvider is null) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<DataAccess.UnitOfWork.IUnitOfWork>();

            var package = await unitOfWork.InstallerPackages.GetByIdAsync(packageId);
            if (package is not null)
            {
                package.Version = newHash;
                package.IsUpdateAvailable = false;
                await unitOfWork.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to persist new version for package {Id}", packageId);
        }
    }

    [RelayCommand]
    private void FilterByInstance(InstanceTabItem? tab)
    {
        if (tab is null || (SelectedInstance is not null && SelectedInstance.PackageId == tab.PackageId))
        {
            // Deselect: clear instance filter and show all
            foreach (var t in InstanceTabs) t.IsSelected = false;
            SelectedInstance = null;
            SearchText = null;
            CategoryFilter = null;
        }
        else
        {
            // Select this tab and filter the log to its output
            foreach (var t in InstanceTabs) t.IsSelected = false;
            tab.IsSelected = true;
            SelectedInstance = tab;
            CategoryFilter = LogCategory.InstanceManagement;
            // Search by package name – matches "Instance: {name}" source from TrackedTaskHandle
            SearchText = tab.Name;
        }
    }

    private void OnInstanceRunningStateChanged(int packageId, bool running)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var tab = InstanceTabs.FirstOrDefault(t => t.PackageId == packageId);
            if (tab is null) return;

            tab.IsRunning = running;

            if (running)
            {
                // Auto-open the console panel and filter to this instance,
                // regardless of whether the launch came from the Installer Manager
                // cards or from the Unified Console tabs.
                PanelOpenRequested?.Invoke(this, EventArgs.Empty);
                FilterByInstance(tab);
            }
            else
            {
                tab.DetectedWebUrl = null;
            }
        });
    }

    private void OnInstanceWebUrlDetected(int packageId, string url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var tab = InstanceTabs.FirstOrDefault(t => t.PackageId == packageId);
            if (tab is not null)
                tab.DetectedWebUrl = url;
        });
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logSubscription?.Dispose();
        _taskTracker.TaskChanged -= OnTaskChanged;

        if (_processManager is not null)
        {
            _processManager.RunningStateChanged -= OnInstanceRunningStateChanged;
            _processManager.WebUrlDetected -= OnInstanceWebUrlDetected;
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    /// <summary>
    /// Bridge from IObservable to the ViewModel's handler.
    /// </summary>
    private sealed class LogObserver(UnifiedConsoleViewModel vm) : IObserver<LogEntry>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(LogEntry value) => vm.OnLogEntryReceived(value);
    }
}
