using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
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
        // TODO: implement file export via IDialogService.ShowSaveFileDialogAsync
        await Task.CompletedTask;
    }

    #endregion

    #region Instance Management

    private async Task LoadInstancesAsync()
    {
        if (_serviceProvider is null) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInstallerPackageRepository>();
            var packages = await repo.GetAllAsync();

            Dispatcher.UIThread.Post(() =>
            {
                InstanceTabs.Clear();
                foreach (var package in packages)
                {
                    var tab = new InstanceTabItem(package.Id, package.Name, package.Type)
                    {
                        IsDefault = package.IsDefault
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
            var repo = scope.ServiceProvider.GetRequiredService<IInstallerPackageRepository>();
            var package = await repo.GetByIdAsync(tab.PackageId);
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
