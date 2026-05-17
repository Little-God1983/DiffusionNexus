using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Inference.Captioning;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Result returned from the combined VRAM+destination options dialog. Null
/// when the user cancelled. The row's Download command consumes this.
/// </summary>
public sealed record CaptioningDownloadChoice(int VramGb, string DestinationDirectory);

/// <summary>
/// Row in the Diffusion Nexus Core captioning dialog. One entry per
/// <see cref="CaptioningModelType"/>. For tiered models exposes a Download
/// command that opens the combined options dialog (VRAM tier + destination +
/// disk-space check) and routes progress through <see cref="IActivityLogService"/>
/// so the status-bar progress indicator keeps updating even after the dialog
/// is closed.
/// </summary>
public sealed partial class CaptioningModelRowViewModel : ObservableObject
{
    private readonly CaptioningModelManager _manager;
    private readonly ICaptioningService _captioningService;
    private readonly IActivityLogService? _activityLog;
    private readonly Func<CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> _optionsPicker;

    public CaptioningModelType ModelType { get; }
    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(DownloadButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    private CaptioningModelStatus _status;

    [ObservableProperty] private string _filePath;

    /// <summary>
    /// VRAM tiers this model supports. Empty for the non-tiered legacy entries
    /// (LLaVA / Qwen2.5 / Qwen3 vanilla / Abliterated_Q8) so their rows hide
    /// the Download button entirely.
    /// </summary>
    public int[] SupportedVramTiers { get; }

    public bool IsTieredDownloadable => SupportedVramTiers.Length > 0;

    /// <summary>
    /// Derived from Status — true while the manager reports an active fetch
    /// for this model type. Drives both the per-row "Downloading…" label and
    /// the button's disabled state. The download itself is observed through
    /// the singleton ActivityLogService, so closing and reopening the dialog
    /// still reflects the in-flight state correctly.
    /// </summary>
    public bool IsDownloading => Status == CaptioningModelStatus.Downloading;

    /// <summary>
    /// True when the Download button should be enabled. Hidden entirely for
    /// non-tiered rows (no upstream URL or user-supplied entries).
    /// </summary>
    public bool CanDownload => IsTieredDownloadable && !IsDownloading;

    public string DownloadButtonLabel => Status switch
    {
        CaptioningModelStatus.Downloading => "Downloading…",
        CaptioningModelStatus.Ready or CaptioningModelStatus.Loaded => "Re-download",
        _ => "Download"
    };

    public string StatusLabel => Status switch
    {
        CaptioningModelStatus.Ready => "Ready",
        CaptioningModelStatus.Loaded => "Loaded",
        CaptioningModelStatus.Downloading => "Downloading…",
        CaptioningModelStatus.Corrupted => "Corrupted",
        _ => "Not present"
    };

    /// <summary>
    /// Colour palette matches WorkloadItemViewModel so the dialog feels like a
    /// natural sibling of the ComfyUI Workloads dialog.
    /// </summary>
    public string StatusColor => Status switch
    {
        CaptioningModelStatus.Ready or CaptioningModelStatus.Loaded => "#4CAF50",
        CaptioningModelStatus.Downloading => "#FFC107",
        CaptioningModelStatus.Corrupted => "#F44336",
        _ => "#999999"
    };

    public IAsyncRelayCommand DownloadCommand { get; }

    internal CaptioningModelRowViewModel(
        CaptioningModelManager manager,
        ICaptioningService captioningService,
        IActivityLogService? activityLog,
        CaptioningModelType modelType,
        Func<CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> optionsPicker)
    {
        _manager = manager;
        _captioningService = captioningService;
        _activityLog = activityLog;
        _optionsPicker = optionsPicker;
        ModelType = modelType;
        Name = CaptioningModelManager.GetDisplayName(modelType);
        Description = CaptioningModelManager.GetDescription(modelType);
        SupportedVramTiers = CaptioningModelManager.GetSupportedVramTiers(modelType);

        // Read live status — this means reopening the dialog mid-download
        // sees "Downloading…" and a disabled button, instead of an enabled
        // button that would start a duplicate fetch.
        var info = manager.GetModelInfo(modelType);
        _status = info.Status;
        _filePath = info.FilePath;

        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanDownload);
    }

    /// <summary>
    /// CommunityToolkit source-generator hook — runs whenever Status changes.
    /// Forces the command's CanExecute to re-evaluate so the button visually
    /// toggles enabled/disabled in sync with the download state.
    /// </summary>
    partial void OnStatusChanged(CaptioningModelStatus value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private async Task DownloadAsync()
    {
        if (!IsTieredDownloadable) return;

        var choice = await _optionsPicker(ModelType, SupportedVramTiers);
        if (choice is null) return; // user cancelled

        // Flip our local Status immediately so the button disables before the
        // async machinery spins up. The manager flips its own _downloadingModels
        // flag inside DownloadModelAsync, so a parallel dialog opened a moment
        // later will see Downloading too.
        Status = CaptioningModelStatus.Downloading;

        var operationName = $"{Name} ({choice.VramGb} GB tier)";
        _activityLog?.StartDownloadProgress(operationName);

        var success = false;
        var failureMessage = string.Empty;

        try
        {
            // Route progress to BOTH the activity log (visible in the status
            // bar regardless of whether the dialog is open) and a local hook
            // we could use later for richer in-dialog UI. The activity log is
            // the load-bearing channel — see LoraDownloadService for the same
            // pattern.
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                var percent = p.TotalBytes > 0
                    ? (int)((double)p.BytesDownloaded / p.TotalBytes * 100.0)
                    : 0;
                _activityLog?.ReportDownloadProgress(percent, p.Status);
            });

            success = await _captioningService.DownloadModelAsync(
                ModelType, choice.VramGb, choice.DestinationDirectory, progress);
            if (!success)
            {
                failureMessage = "Download did not complete successfully.";
            }
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            success = false;
        }
        finally
        {
            // Refresh from disk so the row flips from Downloading to Ready
            // (or back to NotDownloaded on failure).
            var refreshed = _manager.GetModelInfo(ModelType);
            Dispatcher.UIThread.Post(() =>
            {
                Status = refreshed.Status;
                FilePath = refreshed.FilePath;
            });

            _activityLog?.CompleteDownloadProgress(
                success,
                success
                    ? $"{operationName} downloaded successfully to {choice.DestinationDirectory}."
                    : $"{operationName} download failed: {failureMessage}");
        }
    }
}

/// <summary>
/// ViewModel for the Diffusion Nexus Core captioning models dialog. Mirrors
/// the shape of <see cref="WorkloadsViewModel"/> so the view templates stay
/// consistent: a tabular list of items + a footer of contextual info.
/// </summary>
public partial class CaptioningModelsDialogViewModel : ViewModelBase
{
    private readonly CaptioningModelManager _manager;
    private readonly ICaptioningService _captioningService;
    private readonly IActivityLogService? _activityLog;
    private readonly Func<CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> _optionsPicker;

    /// <summary>
    /// The rows displayed in the DataGrid — one per captioning model.
    /// </summary>
    public ObservableCollection<CaptioningModelRowViewModel> Models { get; } = [];

    /// <summary>
    /// Every directory the manager will scan when resolving model files.
    /// Shown below the grid so users can see exactly where their GGUFs are
    /// being looked up — important for diagnosing "why isn't my file showing".
    /// </summary>
    public ObservableCollection<string> SearchPaths { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Creates a dialog viewmodel. <paramref name="optionsPicker"/> is invoked
    /// when the user clicks Download on a tiered model row; it opens the
    /// combined VRAM+destination+space dialog and returns the user's chosen
    /// tier and destination directory (or null on cancel).
    /// <paramref name="activityLog"/> is optional but recommended — when
    /// supplied, download progress shows in the status-bar globally and
    /// survives this dialog being closed and reopened.
    /// </summary>
    public CaptioningModelsDialogViewModel(
        CaptioningModelManager manager,
        ICaptioningService captioningService,
        IActivityLogService? activityLog,
        Func<CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> optionsPicker)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(captioningService);
        ArgumentNullException.ThrowIfNull(optionsPicker);
        _manager = manager;
        _captioningService = captioningService;
        _activityLog = activityLog;
        _optionsPicker = optionsPicker;
        Load();
    }

    private void Load()
    {
        try
        {
            IsLoading = true;
            Models.Clear();
            SearchPaths.Clear();

            foreach (var modelType in Enum.GetValues<CaptioningModelType>())
            {
                Models.Add(new CaptioningModelRowViewModel(
                    _manager, _captioningService, _activityLog, modelType, _optionsPicker));
            }

            foreach (var path in _manager.SearchPaths)
            {
                SearchPaths.Add(path);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
