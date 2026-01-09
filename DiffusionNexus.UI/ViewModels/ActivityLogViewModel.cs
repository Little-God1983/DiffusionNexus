using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the activity log panel, providing filtered log entries and progress operations.
/// </summary>
public partial class ActivityLogViewModel : ViewModelBase, IDisposable
{
    private readonly IActivityLogService _logService;
    private readonly List<ActivityLogEntry> _allEntries = [];
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<ActivityLogEntry> _filteredEntries = [];

    [ObservableProperty]
    private ObservableCollection<ProgressOperation> _activeOperations = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredEntries))]
    private bool _showDebug;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredEntries))]
    private bool _showInfo = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredEntries))]
    private bool _showSuccess = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredEntries))]
    private bool _showWarnings = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredEntries))]
    private bool _showErrors = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredEntries))]
    private string? _sourceFilter;

    [ObservableProperty]
    private bool _isPanelOpen;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ActivitySeverity _statusSeverity = ActivitySeverity.Info;

    [ObservableProperty]
    private bool _hasActiveOperations;

    [ObservableProperty]
    private ProgressOperation? _primaryOperation;

    [ObservableProperty]
    private int _totalEntryCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    /// <summary>
    /// List of unique sources for filtering dropdown.
    /// </summary>
    public ObservableCollection<string> AvailableSources { get; } = [];

    public ActivityLogViewModel(IActivityLogService logService)
    {
        _logService = logService;

        // Subscribe to service events
        _logService.EntryAdded += OnEntryAdded;
        _logService.LogCleared += OnLogCleared;
        _logService.OperationStarted += OnOperationStarted;
        _logService.OperationCompleted += OnOperationCompleted;
        _logService.StatusChanged += OnStatusChanged;

        // Load existing entries
        LoadExistingEntries();
        UpdateStatusFromService();
    }

    private void LoadExistingEntries()
    {
        _allEntries.Clear();
        _allEntries.AddRange(_logService.GetEntries());
        UpdateCounts();
        ApplyFilters();
        UpdateAvailableSources();
    }

    private void OnEntryAdded(object? sender, ActivityLogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _allEntries.Add(entry);
            UpdateCounts();
            
            if (ShouldIncludeEntry(entry))
            {
                FilteredEntries.Add(entry);
            }
            
            UpdateAvailableSources(entry.Source);
        });
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _allEntries.Clear();
            FilteredEntries.Clear();
            AvailableSources.Clear();
            UpdateCounts();
        });
    }

    private void OnOperationStarted(object? sender, ProgressOperation operation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ActiveOperations.Add(operation);
            HasActiveOperations = ActiveOperations.Count > 0;
            PrimaryOperation = ActiveOperations.LastOrDefault();
        });
    }

    private void OnOperationCompleted(object? sender, ProgressOperation operation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ActiveOperations.Remove(operation);
            HasActiveOperations = ActiveOperations.Count > 0;
            PrimaryOperation = ActiveOperations.LastOrDefault();
        });
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateStatusFromService);
    }

    private void UpdateStatusFromService()
    {
        StatusMessage = _logService.CurrentStatus;
        StatusSeverity = _logService.CurrentStatusSeverity;
    }

    private void UpdateCounts()
    {
        TotalEntryCount = _allEntries.Count;
        WarningCount = _allEntries.Count(e => e.Severity == ActivitySeverity.Warning);
        ErrorCount = _allEntries.Count(e => e.Severity == ActivitySeverity.Error);
    }

    private void UpdateAvailableSources(string? newSource = null)
    {
        if (newSource is not null && !AvailableSources.Contains(newSource))
        {
            AvailableSources.Add(newSource);
        }
        else if (newSource is null)
        {
            AvailableSources.Clear();
            foreach (var source in _allEntries.Select(e => e.Source).Distinct().OrderBy(s => s))
            {
                AvailableSources.Add(source);
            }
        }
    }

    private bool ShouldIncludeEntry(ActivityLogEntry entry)
    {
        // Check severity filter
        var includesBySeverity = entry.Severity switch
        {
            ActivitySeverity.Debug => ShowDebug,
            ActivitySeverity.Info => ShowInfo,
            ActivitySeverity.Success => ShowSuccess,
            ActivitySeverity.Warning => ShowWarnings,
            ActivitySeverity.Error => ShowErrors,
            _ => true
        };

        if (!includesBySeverity) return false;

        // Check source filter
        if (!string.IsNullOrEmpty(SourceFilter) && 
            !entry.Source.Equals(SourceFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void ApplyFilters()
    {
        FilteredEntries.Clear();
        foreach (var entry in _allEntries.Where(ShouldIncludeEntry))
        {
            FilteredEntries.Add(entry);
        }
    }

    partial void OnShowDebugChanged(bool value) => ApplyFilters();
    partial void OnShowInfoChanged(bool value) => ApplyFilters();
    partial void OnShowSuccessChanged(bool value) => ApplyFilters();
    partial void OnShowWarningsChanged(bool value) => ApplyFilters();
    partial void OnShowErrorsChanged(bool value) => ApplyFilters();
    partial void OnSourceFilterChanged(string? value) => ApplyFilters();

    [RelayCommand]
    private void ClearLog()
    {
        _logService.ClearLog();
    }

    [RelayCommand]
    private void TogglePanel()
    {
        IsPanelOpen = !IsPanelOpen;
    }

    [RelayCommand]
    private void ShowErrorsOnly()
    {
        ShowDebug = false;
        ShowInfo = false;
        ShowSuccess = false;
        ShowWarnings = true;
        ShowErrors = true;
    }

    [RelayCommand]
    private void ShowAll()
    {
        ShowDebug = true;
        ShowInfo = true;
        ShowSuccess = true;
        ShowWarnings = true;
        ShowErrors = true;
    }

    [RelayCommand]
    private void CancelOperation(ProgressOperation? operation)
    {
        operation?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logService.EntryAdded -= OnEntryAdded;
        _logService.LogCleared -= OnLogCleared;
        _logService.OperationStarted -= OnOperationStarted;
        _logService.OperationCompleted -= OnOperationCompleted;
        _logService.StatusChanged -= OnStatusChanged;

        GC.SuppressFinalize(this);
    }
}
