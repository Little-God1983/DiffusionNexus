using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Bindable row for a single download in the status-bar flyout. Mutated in
/// place from <see cref="StatusBarViewModel.RefreshDownloadTasks"/> snapshots
/// so already-displayed rows animate smoothly instead of being torn down
/// and rebuilt on every progress tick.
/// </summary>
public sealed partial class DownloadTaskRowViewModel : ObservableObject
{
    private readonly IDownloadCoordinator? _coordinator;

    public Guid Id { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private DownloadTaskStatus _status;

    [ObservableProperty] private int _percent;
    [ObservableProperty] private string? _statusMessage;

    public bool IsActive => Status == DownloadTaskStatus.Active;

    /// <summary>
    /// True for downloads the user can still abort. Queued tasks haven't
    /// consumed a slot yet so they can be discarded freely; active tasks
    /// cancel through their CancellationToken and roll back the partial
    /// file. Once a task lands in a terminal state (Completed / Failed /
    /// Cancelled) the button disappears.
    /// </summary>
    public bool CanCancel => Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Active;

    public string StatusLabel => Status switch
    {
        DownloadTaskStatus.Active => "Active",
        DownloadTaskStatus.Queued => "Queued",
        DownloadTaskStatus.Completed => "Done",
        DownloadTaskStatus.Failed => "Failed",
        DownloadTaskStatus.Cancelled => "Cancelled",
        _ => Status.ToString()
    };

    public string StatusColor => Status switch
    {
        DownloadTaskStatus.Active => "#4CAF50",
        DownloadTaskStatus.Queued => "#999999",
        DownloadTaskStatus.Completed => "#4CAF50",
        DownloadTaskStatus.Failed => "#F44336",
        DownloadTaskStatus.Cancelled => "#FFA000",
        _ => "#FFFFFF"
    };

    public IRelayCommand CancelCommand { get; }

    public DownloadTaskRowViewModel(DownloadTask snapshot, IDownloadCoordinator? coordinator)
    {
        _coordinator = coordinator;
        Id = snapshot.Id;
        Name = snapshot.Name;
        _status = snapshot.Status;
        _percent = snapshot.Percent;
        _statusMessage = snapshot.StatusMessage;
        CancelCommand = new RelayCommand(CancelThisDownload, () => CanCancel);
    }

    private void CancelThisDownload() => _coordinator?.Cancel(Id);

    /// <summary>
    /// Applies the latest snapshot from the coordinator. Mutates in place so
    /// existing UI bindings continue to point at the same instance.
    /// </summary>
    public void Update(DownloadTask snapshot)
    {
        if (snapshot.Id != Id) return; // sanity check
        Status = snapshot.Status;
        Percent = snapshot.Percent;
        StatusMessage = snapshot.StatusMessage;
        CancelCommand.NotifyCanExecuteChanged();
    }
}
