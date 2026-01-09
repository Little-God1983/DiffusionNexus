using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the status bar at the bottom of the main window.
/// Shows current status, active operation progress, and toggle for log panel.
/// </summary>
public partial class StatusBarViewModel : ViewModelBase, IDisposable
{
    private readonly IActivityLogService _logService;
    private readonly ActivityLogViewModel _logViewModel;
    private bool _disposed;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBackground))]
    [NotifyPropertyChangedFor(nameof(StatusForeground))]
    private ActivitySeverity _statusSeverity = ActivitySeverity.Info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBackground))]
    [NotifyPropertyChangedFor(nameof(StatusForeground))]
    [NotifyPropertyChangedFor(nameof(ProgressBarBackground))]
    private bool _hasActiveOperation;

    [ObservableProperty]
    private string? _activeOperationName;

    [ObservableProperty]
    private int? _activeOperationProgress;

    [ObservableProperty]
    private bool _isLogPanelOpen;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private bool _hasWarningsOrErrors;

    /// <summary>
    /// The activity log ViewModel for binding to the log panel.
    /// </summary>
    public ActivityLogViewModel LogViewModel => _logViewModel;

    /// <summary>
    /// Background color based on status severity and active operation state.
    /// Shows green when an operation is in progress.
    /// </summary>
    public string StatusBackground
    {
        get
        {
            // Show green when actively working
            if (HasActiveOperation)
                return "#28A745";
            
            return StatusSeverity switch
            {
                ActivitySeverity.Success => "#28A745",
                ActivitySeverity.Warning => "#FFC107",
                ActivitySeverity.Error => "#DC3545",
                _ => "#007ACC"
            };
        }
    }

    /// <summary>
    /// Foreground color based on status severity.
    /// </summary>
    public string StatusForeground => StatusSeverity switch
    {
        ActivitySeverity.Warning when !HasActiveOperation => "#000000",
        _ => "#FFFFFF"
    };

    /// <summary>
    /// Progress bar background color - green for visibility against status bar.
    /// </summary>
    public string ProgressBarBackground => "#1E7E34"; // Darker green for contrast

    public StatusBarViewModel(IActivityLogService logService)
    {
        _logService = logService;
        _logViewModel = new ActivityLogViewModel(logService);

        // Subscribe to events
        _logService.StatusChanged += OnStatusChanged;
        _logService.OperationStarted += OnOperationStarted;
        _logService.OperationCompleted += OnOperationCompleted;
        _logService.EntryAdded += OnEntryAdded;
        _logService.LogCleared += OnLogCleared;

        // Initialize counts
        UpdateCounts();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = _logService.CurrentStatus;
            StatusSeverity = _logService.CurrentStatusSeverity;
        });
    }

    private void OnOperationStarted(object? sender, ProgressOperation operation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HasActiveOperation = true;
            ActiveOperationName = operation.Name;
            ActiveOperationProgress = operation.ProgressPercent;
            
            // Subscribe to progress updates
            operation.ProgressChanged += OnOperationProgressChanged;
        });
    }

    private void OnOperationCompleted(object? sender, ProgressOperation operation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            operation.ProgressChanged -= OnOperationProgressChanged;
            
            var activeOps = _logService.GetActiveOperations();
            if (activeOps.Count == 0)
            {
                HasActiveOperation = false;
                ActiveOperationName = null;
                ActiveOperationProgress = null;
            }
            else
            {
                var latest = activeOps[^1];
                ActiveOperationName = latest.Name;
                ActiveOperationProgress = latest.ProgressPercent;
            }
        });
    }

    private void OnOperationProgressChanged(object? sender, EventArgs e)
    {
        if (sender is ProgressOperation operation)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ActiveOperationProgress = operation.ProgressPercent;
            });
        }
    }

    private void OnEntryAdded(object? sender, ActivityLogEntry entry)
    {
        Dispatcher.UIThread.Post(UpdateCounts);
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateCounts);
    }

    private void UpdateCounts()
    {
        var entries = _logService.GetEntries();
        WarningCount = entries.Count(e => e.Severity == ActivitySeverity.Warning);
        ErrorCount = entries.Count(e => e.Severity == ActivitySeverity.Error);
        HasWarningsOrErrors = WarningCount > 0 || ErrorCount > 0;
    }

    [RelayCommand]
    private void ToggleLogPanel()
    {
        IsLogPanelOpen = !IsLogPanelOpen;
        _logViewModel.IsPanelOpen = IsLogPanelOpen;
    }

    [RelayCommand]
    private void ShowLogPanel()
    {
        IsLogPanelOpen = true;
        _logViewModel.IsPanelOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logService.StatusChanged -= OnStatusChanged;
        _logService.OperationStarted -= OnOperationStarted;
        _logService.OperationCompleted -= OnOperationCompleted;
        _logService.EntryAdded -= OnEntryAdded;
        _logService.LogCleared -= OnLogCleared;

        _logViewModel.Dispose();

        GC.SuppressFinalize(this);
    }
}
