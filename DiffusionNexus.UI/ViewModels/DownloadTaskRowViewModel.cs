using CommunityToolkit.Mvvm.ComponentModel;
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
    public Guid Id { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private DownloadTaskStatus _status;

    [ObservableProperty] private int _percent;
    [ObservableProperty] private string? _statusMessage;

    public bool IsActive => Status == DownloadTaskStatus.Active;

    public string StatusLabel => Status switch
    {
        DownloadTaskStatus.Active => "Active",
        DownloadTaskStatus.Queued => "Queued",
        DownloadTaskStatus.Completed => "Done",
        DownloadTaskStatus.Failed => "Failed",
        _ => Status.ToString()
    };

    public string StatusColor => Status switch
    {
        DownloadTaskStatus.Active => "#4CAF50",
        DownloadTaskStatus.Queued => "#999999",
        DownloadTaskStatus.Completed => "#4CAF50",
        DownloadTaskStatus.Failed => "#F44336",
        _ => "#FFFFFF"
    };

    public DownloadTaskRowViewModel(DownloadTask snapshot)
    {
        Id = snapshot.Id;
        Name = snapshot.Name;
        _status = snapshot.Status;
        _percent = snapshot.Percent;
        _statusMessage = snapshot.StatusMessage;
    }

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
    }
}
