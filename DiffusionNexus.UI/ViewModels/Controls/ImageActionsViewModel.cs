using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels.Controls;

/// <summary>
/// The set of file paths an Add/Send action should operate on, plus an optional cleanup callback.
/// Hosts whose source is edited in memory (e.g. the Image Editor) export to a temp file and return a
/// <see cref="Cleanup"/> that deletes it. <b>Cleanup runs only for the synchronous Add destinations</b>
/// (the importer copies the file before returning); Send destinations ingest deferred, so they leave
/// the temp for the OS to reclaim rather than delete it out from under the target.
/// </summary>
public sealed record ImageActionPaths(IReadOnlyList<string> Paths, Action? Cleanup = null)
{
    public static readonly ImageActionPaths Empty = new([], null);
}

/// <summary>
/// Reusable "Add Selected To… / Send Selected To…" brain, lifted from the Generation Gallery so any
/// surface showing images (pipeline result strip, Image Editor, …) can offer the same destinations
/// without duplicating the dialog/import/navigation flows.
///
/// <para>The host supplies the images to act on through <see cref="PathProvider"/> and gates the
/// commands via <see cref="CanAct"/>. Individual destinations are shown/hidden through the
/// <c>Show*</c> flags — e.g. the Image Editor sets <see cref="ShowSendToImageEditor"/> to false
/// (sending an image from the editor back to the editor is pointless).</para>
/// </summary>
public partial class ImageActionsViewModel : ObservableObject
{
    private readonly IDatasetState _state;
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IVideoThumbnailService? _videoThumbnailService;
    private readonly IAppSettingsService? _settingsService;

    /// <summary>
    /// Window-bound dialog service. Set by the host once a top-level window exists (the Add flows
    /// need it for the destination dialogs; without it those commands no-op).
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Resolves the images to act on at click time (selected tiles, the current edited image, …).
    /// Returns <see cref="ImageActionPaths.Empty"/> when nothing is available.
    /// </summary>
    public Func<Task<ImageActionPaths>>? PathProvider { get; set; }

    /// <summary>Optional status sink (e.g. the Image Editor surfaces this in its status bar).</summary>
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Caption of the "Add" dropdown button (single-image hosts use "Add To…").</summary>
    [ObservableProperty] private string _addButtonText = "Add Selected To...";

    /// <summary>Caption of the "Send" dropdown button (single-image hosts use "Send To…").</summary>
    [ObservableProperty] private string _sendButtonText = "Send Selected To...";

    // ── Destination toggles (bound to each menu item's IsVisible) ────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddMenu))]
    private bool _showAddToDataset = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddMenu))]
    private bool _showAddToTrainingRun = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSendMenu))]
    private bool _showSendToImageEditor = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSendMenu))]
    private bool _showSendToComparer = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSendMenu))]
    private bool _showSendToBatchUpscale = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSendMenu))]
    private bool _showSendToBatchCrop = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSendMenu))]
    private bool _showSendToCaptioning = true;

    /// <summary>True when at least one "Add" destination is enabled (drives the Add button visibility).</summary>
    public bool ShowAddMenu => ShowAddToDataset || ShowAddToTrainingRun;

    /// <summary>True when at least one "Send" destination is enabled (drives the Send button visibility).</summary>
    public bool ShowSendMenu =>
        ShowSendToImageEditor || ShowSendToComparer || ShowSendToBatchUpscale
        || ShowSendToBatchCrop || ShowSendToCaptioning;

    /// <summary>
    /// Whether the actions can run (host gate, e.g. "has a selection" / "has a loaded image").
    /// Disables every command's button when false.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToTrainingRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToImageEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToComparerCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToBatchUpscaleCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToBatchCropCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToCaptioningCommand))]
    private bool _canAct;

    public ImageActionsViewModel(
        IDatasetState state,
        IDatasetEventAggregator eventAggregator,
        IVideoThumbnailService? videoThumbnailService = null,
        IAppSettingsService? settingsService = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _videoThumbnailService = videoThumbnailService;
        _settingsService = settingsService;
    }

    /// <summary>Acquires the source paths from the host, or <see cref="ImageActionPaths.Empty"/>.</summary>
    private async Task<ImageActionPaths> AcquirePathsAsync()
        => PathProvider is null ? ImageActionPaths.Empty : await PathProvider().ConfigureAwait(true);

    // ── Add Selected To… ─────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AddToDatasetAsync()
    {
        if (DialogService is null || _settingsService is null) return;

        var acquired = await AcquirePathsAsync();
        try
        {
            if (acquired.Paths.Count == 0) return;

            var dialogResult = await DialogService.ShowAddToDatasetDialogAsync(acquired.Paths.Count, _state.Datasets);
            if (!dialogResult.Confirmed) return;

            var targetDataset = await ResolveTargetDatasetAsync(dialogResult);
            if (targetDataset is null) return;

            var targetVersion = await ResolveTargetVersionAsync(targetDataset, dialogResult);
            var destinationFolder = targetDataset.GetVersionFolderPath(targetVersion);

            var importer = new DatasetFileImporter(new FileOperations());
            var importResult = await importer.ImportWithDialogAsync(
                acquired.Paths,
                destinationFolder,
                DialogService,
                _videoThumbnailService,
                moveFiles: false);

            if (importResult.Cancelled) return;

            targetDataset.RefreshImageInfo();
            _eventAggregator.PublishImageAdded(new ImageAddedEventArgs
            {
                Dataset = targetDataset,
                AddedImages = []
            });

            StatusMessage = importResult.TotalAdded > 0
                ? $"Added {importResult.TotalAdded} image(s) to {targetDataset.Name} (V{targetVersion})."
                : "Image(s) already present — nothing added.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add to dataset: {ex.Message}";
            if (DialogService is not null)
                await DialogService.ShowMessageAsync("Add to dataset failed", ex.Message);
        }
        finally
        {
            acquired.Cleanup?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AddToTrainingRunAsync()
    {
        if (DialogService is null) return;

        var acquired = await AcquirePathsAsync();
        try
        {
            if (acquired.Paths.Count == 0) return;

            var dialogResult = await DialogService.ShowAddToTrainingRunDialogAsync(acquired.Paths.Count, _state.Datasets);
            if (!dialogResult.Confirmed) return;

            var dataset = dialogResult.SelectedDataset;
            if (dataset is null || !dialogResult.SelectedVersion.HasValue) return;
            var version = dialogResult.SelectedVersion.Value;

            var trainingRunName = dialogResult.IsNewTrainingRun
                ? dialogResult.NewTrainingRunName
                : dialogResult.SelectedTrainingRunName;
            if (string.IsNullOrWhiteSpace(trainingRunName)) return;

            var versionPath = dataset.GetVersionFolderPath(version);

            if (dialogResult.IsNewTrainingRun)
            {
                TrainingRunMigrationUtility.CreateTrainingRunFolder(versionPath, trainingRunName);

                var runInfo = new TrainingRunInfo
                {
                    Name = trainingRunName,
                    CreatedAt = DateTimeOffset.Now
                };

                if (!dataset.TrainingRuns.ContainsKey(version))
                    dataset.TrainingRuns[version] = [];

                dataset.TrainingRuns[version].Add(runInfo);
                dataset.SaveMetadata();
            }

            var runPath = TrainingRunMigrationUtility.GetTrainingRunPath(versionPath, trainingRunName);
            var destinationFolder = Path.Combine(runPath, "Presentation");
            Directory.CreateDirectory(destinationFolder);

            var importer = new DatasetFileImporter(new FileOperations());
            var importResult = await importer.ImportWithDialogAsync(
                acquired.Paths,
                destinationFolder,
                DialogService,
                _videoThumbnailService,
                moveFiles: false);

            if (importResult.Cancelled) return;

            dataset.RefreshImageInfo();
            _eventAggregator.PublishImageAdded(new ImageAddedEventArgs
            {
                Dataset = dataset,
                AddedImages = []
            });

            StatusMessage = importResult.TotalAdded > 0
                ? $"Added {importResult.TotalAdded} image(s) to training run '{trainingRunName}' ({dataset.Name} V{version})."
                : "Image(s) already present — nothing added.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add to training run: {ex.Message}";
            if (DialogService is not null)
                await DialogService.ShowMessageAsync("Add to training run failed", ex.Message);
        }
        finally
        {
            acquired.Cleanup?.Invoke();
        }
    }

    // ── Send Selected To… ──────────────────────────────────────────────────────────
    // Every Send destination ingests the paths *deferred* (the host posts the load to the dispatcher /
    // the target reads lazily), so we must NOT delete a host-provided temp here — it has to outlive this
    // command. Hosts whose source is a temp export therefore leave it for the OS to reclaim (matching the
    // editor's long-standing Send behaviour). Only the synchronous Add destinations above invoke Cleanup.

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SendToImageEditorAsync()
    {
        var paths = (await AcquirePathsAsync()).Paths;
        if (paths.Count == 0) return;

        var editorImages = paths
            .Select(p => DatasetImageViewModel.FromFile(p, _eventAggregator))
            .ToList();

        var tempDataset = new DatasetCardViewModel
        {
            Name = "Result Selection",
            FolderPath = "TEMP://ImageResults",
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = editorImages.Count,
            TotalImageCountAllVersions = editorImages.Count,
            IsTemporary = true
        };

        _eventAggregator.PublishNavigateToImageEditor(new NavigateToImageEditorEventArgs
        {
            Dataset = tempDataset,
            Image = editorImages[0],
            Images = editorImages
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SendToComparerAsync()
    {
        var paths = (await AcquirePathsAsync()).Paths;
        if (paths.Count < 2)
        {
            if (DialogService is not null)
                await DialogService.ShowMessageAsync(
                    "Selection Required",
                    "Please select at least 2 images to compare.");
            return;
        }

        _eventAggregator.PublishNavigateToImageComparer(new NavigateToImageComparerEventArgs
        {
            ImagePaths = paths.ToList()
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SendToBatchUpscaleAsync()
    {
        var paths = (await AcquirePathsAsync()).Paths;
        if (paths.Count == 0) return;
        _eventAggregator.PublishNavigateToBatchUpscale(new NavigateToBatchUpscaleEventArgs
        {
            ImagePaths = paths.ToList()
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SendToBatchCropAsync()
    {
        var paths = (await AcquirePathsAsync()).Paths;
        if (paths.Count == 0) return;
        _eventAggregator.PublishNavigateToBatchCropScale(new NavigateToBatchCropScaleEventArgs
        {
            ImagePaths = paths.ToList()
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SendToCaptioningAsync()
    {
        var paths = (await AcquirePathsAsync()).Paths;
        if (paths.Count == 0) return;
        _eventAggregator.PublishNavigateToCaptioning(new NavigateToCaptioningEventArgs
        {
            ImagePaths = paths.ToList()
        });
    }

    // ── Dataset/version resolution (shared with the gallery/editor flows) ───────────

    private async Task<DatasetCardViewModel?> ResolveTargetDatasetAsync(AddToDatasetResult dialogResult)
    {
        if (dialogResult.DestinationOption == DatasetDestinationOption.ExistingDataset)
        {
            if (dialogResult.SelectedDataset is null && DialogService is not null)
            {
                await DialogService.ShowMessageAsync(
                    "No Dataset Selected",
                    "Please choose an existing dataset to continue.");
            }
            return dialogResult.SelectedDataset;
        }

        if (DialogService is null || _settingsService is null)
            return null;

        var createResult = await DialogService.ShowCreateDatasetDialogAsync(_state.AvailableCategories);
        if (!createResult.Confirmed || string.IsNullOrWhiteSpace(createResult.Name))
            return null;

        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            await DialogService.ShowMessageAsync(
                "Dataset Storage Not Configured",
                "Please configure a dataset storage path in Settings before creating datasets.");
            return null;
        }

        _state.SetStorageConfigured(true);

        var datasetPath = Path.Combine(settings.DatasetStoragePath, createResult.Name);
        if (Directory.Exists(datasetPath))
        {
            await DialogService.ShowMessageAsync(
                "Dataset Already Exists",
                $"A dataset named '{createResult.Name}' already exists.");
            return null;
        }

        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));

        var newDataset = new DatasetCardViewModel
        {
            Name = createResult.Name,
            FolderPath = datasetPath,
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = 0,
            VideoCount = 0,
            CategoryId = createResult.CategoryId,
            CategoryOrder = createResult.CategoryOrder,
            CategoryName = createResult.CategoryName,
            Type = createResult.Type,
            IsNsfw = createResult.IsNsfw
        };

        newDataset.VersionNsfwFlags[1] = createResult.IsNsfw;
        newDataset.SaveMetadata();
        _state.Datasets.Add(newDataset);

        _eventAggregator.PublishDatasetCreated(new DatasetCreatedEventArgs { Dataset = newDataset });

        return newDataset;
    }

    private async Task<int> ResolveTargetVersionAsync(DatasetCardViewModel dataset, AddToDatasetResult dialogResult)
    {
        if (dialogResult.DestinationOption == DatasetDestinationOption.NewDataset)
            return dataset.CurrentVersion;

        if (dialogResult.VersionOption == DatasetVersionOption.CreateNewVersion)
            return await DatasetVersionUtilities.CreateEmptyVersionAsync(dataset, dataset.CurrentVersion, _eventAggregator);

        return dialogResult.SelectedVersion ?? dataset.CurrentVersion;
    }
}
