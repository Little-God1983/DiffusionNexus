using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Services.Startup;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Binds the startup ready-check list to the overlay. CheckChanged is raised
/// on the UI thread by every production caller (startup phases), so no
/// marshaling is needed here.
/// </summary>
public sealed partial class StartupOverlayViewModel : ViewModelBase
{
    public IReadOnlyList<StartupCheckRowViewModel> Rows { get; }

    public StartupOverlayViewModel(StartupProgressService service)
    {
        var rows = service.Checks.ToDictionary(c => c.Id, c => new StartupCheckRowViewModel(c));
        Rows = service.Checks.Select(c => rows[c.Id]).ToList();
        service.CheckChanged += check => rows[check.Id].Refresh(check);
    }
}

public sealed partial class StartupCheckRowViewModel : ObservableObject
{
    public string DisplayName { get; }

    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string? _error;

    public StartupCheckRowViewModel(StartupCheck check)
    {
        DisplayName = check.DisplayName;
        Refresh(check);
    }

    public void Refresh(StartupCheck check)
    {
        IsPending = check.State == StartupCheckState.Pending;
        IsRunning = check.State == StartupCheckState.Running;
        IsDone = check.State == StartupCheckState.Done;
        IsFailed = check.State == StartupCheckState.Failed;
        Error = check.Error;
    }
}
