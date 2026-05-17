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
    private readonly IDownloadCoordinator? _downloadCoordinator;
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
        IDownloadCoordinator? downloadCoordinator,
        CaptioningModelType modelType,
        Func<CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> optionsPicker)
    {
        _manager = manager;
        _captioningService = captioningService;
        _downloadCoordinator = downloadCoordinator;
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

        try
        {
            // If a coordinator is wired in, enqueue through it — that owns the
            // 3-slot concurrency limit and aggregates per-task progress into
            // the status bar. Otherwise fall back to a direct service call so
            // unit-test scenarios still work.
            if (_downloadCoordinator is not null)
            {
                await _downloadCoordinator.EnqueueAsync(operationName, async (taskProgress, ct) =>
                {
                    var fileProgress = new Progress<ModelDownloadProgress>(p =>
                    {
                        var percent = p.TotalBytes > 0
                            ? (int)((double)p.BytesDownloaded / p.TotalBytes * 100.0)
                            : 0;
                        taskProgress.Report(new DownloadTaskProgress(percent, p.Status));
                    });

                    return await _captioningService.DownloadModelAsync(
                        ModelType, choice.VramGb, choice.DestinationDirectory, fileProgress, ct);
                });
            }
            else
            {
                await _captioningService.DownloadModelAsync(
                    ModelType, choice.VramGb, choice.DestinationDirectory, progress: null, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Coordinator swallows download exceptions and marks the task
            // Failed in its own state; we just need to surface that to the row.
            Serilog.Log.Error(ex, "Captioning download for {Model} failed", operationName);
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
    private readonly IDownloadCoordinator? _downloadCoordinator;
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
    /// <paramref name="downloadCoordinator"/> is optional but recommended —
    /// when supplied, downloads run through its 3-slot concurrency queue and
    /// surface as aggregated progress in the status-bar so closing this
    /// dialog mid-fetch doesn't lose visibility.
    /// </summary>
    public CaptioningModelsDialogViewModel(
        CaptioningModelManager manager,
        ICaptioningService captioningService,
        IDownloadCoordinator? downloadCoordinator,
        Func<CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> optionsPicker)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(captioningService);
        ArgumentNullException.ThrowIfNull(optionsPicker);
        _manager = manager;
        _captioningService = captioningService;
        _downloadCoordinator = downloadCoordinator;
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
                    _manager, _captioningService, _downloadCoordinator, modelType, _optionsPicker));
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
