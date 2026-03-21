using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Represents how upscaled images should be saved.
/// </summary>
public enum UpscaleSaveMode
{
    /// <summary>
    /// Create a new dataset version containing the upscaled images.
    /// Captions from the source version are preserved (copied to the new version).
    /// </summary>
    NewVersion,

    /// <summary>
    /// Overwrite the original images in the same folder.
    /// Existing captions are left untouched.
    /// </summary>
    OverwriteInPlace
}

/// <summary>
/// How the positive prompt for upscaling is determined.
/// </summary>
public enum UpscalePromptMode
{
    /// <summary>
    /// User provides a custom positive prompt that is sent to every image.
    /// Uses the Z-Image-Turbo-Upscale workflow.
    /// </summary>
    ManualPrompt,

    /// <summary>
    /// Reads the positive prompt from each image's caption sidecar file (.txt / .caption).
    /// Uses the Z-Image-Turbo-Upscale workflow.
    /// </summary>
    FromCaptions,

    /// <summary>
    /// Extracts the positive prompt from each image's embedded PNG metadata
    /// (ComfyUI / A1111 / Forge generation parameters).
    /// Uses the Z-Image-Turbo-Upscale workflow.
    /// </summary>
    FromMetadata,

    /// <summary>
    /// A vision model (Qwen3-VL) analyses each image and generates the prompt automatically.
    /// Uses the Vision-Z-Image-Turbo-Upscale workflow.
    /// </summary>
    VisionAutoPrompt
}

/// <summary>
/// Extension methods for <see cref="UpscaleSaveMode"/>.
/// </summary>
public static class UpscaleSaveModeExtensions
{
    /// <summary>
    /// Gets a user-friendly display name for the save mode.
    /// </summary>
    public static string GetDisplayName(this UpscaleSaveMode mode) => mode switch
    {
        UpscaleSaveMode.NewVersion => "New Version",
        UpscaleSaveMode.OverwriteInPlace => "Overwrite In Place",
        _ => mode.ToString()
    };
}

/// <summary>
/// Extension methods for <see cref="UpscalePromptMode"/>.
/// </summary>
public static class UpscalePromptModeExtensions
{
    /// <summary>
    /// Gets a user-friendly display name for the prompt mode.
    /// </summary>
    public static string GetDisplayName(this UpscalePromptMode mode) => mode switch
    {
        UpscalePromptMode.ManualPrompt => "Manual Prompt",
        UpscalePromptMode.FromCaptions => "From Captions",
        UpscalePromptMode.FromMetadata => "From Metadata",
        UpscalePromptMode.VisionAutoPrompt => "Vision Auto-Prompt",
        _ => mode.ToString()
    };
}

/// <summary>
/// Represents a single image entry for the batch upscale queue,
/// holding before/after state for comparison.
/// </summary>
public partial class UpscaleImageItemViewModel : ObservableObject
{
    private string _fileName = string.Empty;
    private string _originalPath = string.Empty;
    private string _upscaledPath = string.Empty;
    private Bitmap? _originalThumbnail;
    private Bitmap? _upscaledThumbnail;
    private bool _isProcessed;
    private bool _isProcessing;
    private bool _isSelected;

    /// <summary>
    /// Display file name.
    /// </summary>
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    /// <summary>
    /// Full path to the original image.
    /// </summary>
    public string OriginalPath
    {
        get => _originalPath;
        set => SetProperty(ref _originalPath, value);
    }

    /// <summary>
    /// Full path to the upscaled image (populated after processing).
    /// </summary>
    public string UpscaledPath
    {
        get => _upscaledPath;
        set => SetProperty(ref _upscaledPath, value);
    }

    /// <summary>
    /// Thumbnail of the original image (before).
    /// </summary>
    public Bitmap? OriginalThumbnail
    {
        get => _originalThumbnail;
        set => SetProperty(ref _originalThumbnail, value);
    }

    /// <summary>
    /// Thumbnail of the upscaled image (after).
    /// </summary>
    public Bitmap? UpscaledThumbnail
    {
        get => _upscaledThumbnail;
        set => SetProperty(ref _upscaledThumbnail, value);
    }

    /// <summary>
    /// Whether this image has been processed.
    /// </summary>
    public bool IsProcessed
    {
        get => _isProcessed;
        set => SetProperty(ref _isProcessed, value);
    }

    /// <summary>
    /// Whether this image is currently being processed.
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    /// <summary>
    /// Whether this image is selected for comparison.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// ViewModel for the Batch Upscale tab in the Dataset Manager.
/// Provides dataset/version selection, denoising and upscale factor controls,
/// save mode configuration, and before/after image comparison.
/// 
/// <para>
/// <b>Note:</b> This is a UI-only shell. Backend wiring to an actual upscaler
/// workflow is not yet implemented.
/// </para>
/// </summary>
public partial class BatchUpscaleTabViewModel : ViewModelBase, IDialogServiceAware, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<BatchUpscaleTabViewModel>();

    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private readonly IComfyUIWrapperService? _comfyUiService;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private string? _compareOriginalsTempDir;

    // Workflow paths (relative to AppDomain.CurrentDomain.BaseDirectory)
    private const string ManualUpscaleWorkflowPath = "Assets/Workflows/Z-Image-Turbo-Upscale.json";
    private const string VisionUpscaleWorkflowPath = "Assets/Workflows/Vision-Z-Image-Turbo-Upscale.json";

    // Node IDs shared by both workflows
    private const string LoadImageNodeId = "50";
    private const string PositivePromptNodeId = "17";
    private const string NegativePromptNodeId = "35";
    private const string UltimateSDUpscaleNodeId = "39";
    private const string SaveImageNodeId = "42";

    private const string DefaultNegativePrompt =
        "blurry, low quality, artifacts, distorted, deformed, ugly, bad anatomy, watermark, text";

    private static readonly string[] FunProgressMessages =
    [
        "Polishing pixels with care…",
        "Consulting the upscale oracle…",
        "Summoning high-res spirits…",
        "Feeding the U-Net more pixels…",
        "Enhancing latent details…",
        "Sharpening with invisible brushes…",
        "Asking the model nicely for more detail…",
        "Distilling resolution from chaos…",
        "Warming up the super-resolution engine…",
        "Calibrating the pixel magnifier…"
    ];
    private readonly Random _random = new();

    // Dataset selection
    private DatasetCardViewModel? _selectedDataset;
    private EditorVersionItem? _selectedDatasetVersion;

    // Upscale settings
    private double _denoisingStrength = 0.2;
    private double _upscaleFactor = 1.5;
    private UpscaleSaveMode _saveMode = UpscaleSaveMode.NewVersion;

    // Prompt settings
    private UpscalePromptMode _promptMode = UpscalePromptMode.VisionAutoPrompt;
    private string _positivePrompt = string.Empty;
    private string _negativePrompt = DefaultNegativePrompt;

    // Processing state
    private bool _isProcessing;
    private double _totalProgress;
    private string _currentProcessingStatus = "Ready";
    private int _completedCount;
    private int _totalImageCount;

    // Compare state
    private UpscaleImageItemViewModel? _selectedCompareItem;

    // Dataset statistics
    private int _datasetImageCount;

    /// <summary>
    /// Creates a new instance of BatchUpscaleTabViewModel.
    /// </summary>
    /// <param name="comfyUiService">Optional ComfyUI wrapper service for executing upscale workflows.</param>
    public BatchUpscaleTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        IComfyUIWrapperService? comfyUiService = null)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _comfyUiService = comfyUiService;

        AvailableDatasetVersions = [];
        AvailableSaveModes = Enum.GetValues<UpscaleSaveMode>();

        // TODO: Linux Implementation for Batch Upscale

        RefreshDatasetsCommand = new RelayCommand(
            () => _eventAggregator.PublishRefreshDatasetsRequested(new RefreshDatasetsRequestedEventArgs()));

        StartUpscaleCommand = new AsyncRelayCommand(StartUpscaleAsync, CanStartUpscale);
        CancelUpscaleCommand = new RelayCommand(CancelUpscale, () => IsProcessing);
        SelectCompareItemCommand = new RelayCommand<UpscaleImageItemViewModel>(SelectCompareItem);

        _eventAggregator.RefreshDatasetsRequested += OnRefreshDatasetsRequested;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public BatchUpscaleTabViewModel() : this(null!, null!)
    {
    }

    #region Properties

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Available datasets from shared state.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> AvailableDatasets => _state.Datasets;

    /// <summary>
    /// Available versions for the selected dataset.
    /// </summary>
    public ObservableCollection<EditorVersionItem> AvailableDatasetVersions { get; }

    /// <summary>
    /// Available save mode options.
    /// </summary>
    public IReadOnlyList<UpscaleSaveMode> AvailableSaveModes { get; }

    /// <summary>
    /// The selected dataset to upscale.
    /// </summary>
    public DatasetCardViewModel? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (SetProperty(ref _selectedDataset, value))
            {
                AvailableDatasetVersions.Clear();
                if (value is not null)
                {
                    PopulateVersionItems(value, AvailableDatasetVersions);
                }

                StartUpscaleCommand.NotifyCanExecuteChanged();
                RefreshDatasetStats();
                OnPropertyChanged(nameof(IsSelectedVersionEmpty));
            }
        }
    }

    /// <summary>
    /// The selected dataset version to upscale.
    /// </summary>
    public EditorVersionItem? SelectedDatasetVersion
    {
        get => _selectedDatasetVersion;
        set
        {
            if (SetProperty(ref _selectedDatasetVersion, value))
            {
                if (SelectedDataset is not null && value is not null && SelectedDataset.CurrentVersion != value.Version)
                {
                    SelectedDataset.CurrentVersion = value.Version;
                }

                RefreshDatasetStats();
            }
        }
    }

    #endregion

    #region Upscale Settings

    /// <summary>
    /// Denoising strength (how much change). Range: 0.0 – 1.0.
    /// </summary>
    public double DenoisingStrength
    {
        get => _denoisingStrength;
        set => SetProperty(ref _denoisingStrength, value);
    }

    /// <summary>
    /// Upscale factor. Range: 1.1 – 4.0.
    /// </summary>
    public double UpscaleFactor
    {
        get => _upscaleFactor;
        set => SetProperty(ref _upscaleFactor, value);
    }

    /// <summary>
    /// How upscaled images should be saved.
    /// </summary>
    public UpscaleSaveMode SaveMode
    {
        get => _saveMode;
        set
        {
            if (SetProperty(ref _saveMode, value))
            {
                OnPropertyChanged(nameof(SaveModeDescription));
            }
        }
    }

    /// <summary>
    /// Human-readable description of the selected save mode.
    /// </summary>
    public string SaveModeDescription => SaveMode switch
    {
        UpscaleSaveMode.NewVersion => "Creates a new version and copies captions. Original images are untouched.",
        UpscaleSaveMode.OverwriteInPlace => "Replaces original images in the current folder. Captions are preserved.",
        _ => string.Empty
    };

    /// <summary>
    /// How the positive prompt for upscaling is determined.
    /// </summary>
    public UpscalePromptMode PromptMode
    {
        get => _promptMode;
        set
        {
            if (SetProperty(ref _promptMode, value))
            {
                OnPropertyChanged(nameof(IsManualPromptMode));
                OnPropertyChanged(nameof(PromptModeDescription));
            }
        }
    }

    /// <summary>
    /// Whether manual prompt mode is active (shows the prompt text box).
    /// </summary>
    public bool IsManualPromptMode => PromptMode == UpscalePromptMode.ManualPrompt;

    /// <summary>
    /// Human-readable description of the selected prompt mode.
    /// </summary>
    public string PromptModeDescription => PromptMode switch
    {
        UpscalePromptMode.ManualPrompt => "Your prompt is sent to every image. Good when all images share a theme.",
        UpscalePromptMode.FromCaptions => "Uses each image's caption file (.txt) as the positive prompt.",
        UpscalePromptMode.FromMetadata => "Extracts the positive prompt from each image's embedded generation metadata.",
        UpscalePromptMode.VisionAutoPrompt => "A vision model (Qwen3-VL) analyses each image and writes the prompt for you.",
        _ => string.Empty
    };

    /// <summary>
    /// Available prompt mode options.
    /// </summary>
    public IReadOnlyList<UpscalePromptMode> AvailablePromptModes { get; } = Enum.GetValues<UpscalePromptMode>();

    /// <summary>
    /// Positive prompt text (used in ManualPrompt mode).
    /// </summary>
    public string PositivePrompt
    {
        get => _positivePrompt;
        set => SetProperty(ref _positivePrompt, value);
    }

    /// <summary>
    /// Negative prompt text (applied in all modes).
    /// </summary>
    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value);
    }

    /// <summary>
    /// Whether the ComfyUI service is available.
    /// </summary>
    public bool IsComfyUIAvailable => _comfyUiService is not null;

    #endregion

    #region Processing State

    /// <summary>
    /// Whether batch upscaling is in progress.
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                StartUpscaleCommand.NotifyCanExecuteChanged();
                CancelUpscaleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Total progress percentage (0–100).
    /// </summary>
    public double TotalProgress
    {
        get => _totalProgress;
        private set => SetProperty(ref _totalProgress, value);
    }

    /// <summary>
    /// Current status text.
    /// </summary>
    public string CurrentProcessingStatus
    {
        get => _currentProcessingStatus;
        private set => SetProperty(ref _currentProcessingStatus, value);
    }

    /// <summary>
    /// Number of completed images.
    /// </summary>
    public int CompletedCount
    {
        get => _completedCount;
        private set => SetProperty(ref _completedCount, value);
    }

    /// <summary>
    /// Total number of images to process.
    /// </summary>
    public int TotalImageCount
    {
        get => _totalImageCount;
        private set => SetProperty(ref _totalImageCount, value);
    }

    #endregion

    #region Dataset Statistics

    /// <summary>
    /// Total images in the selected dataset version.
    /// </summary>
    public int DatasetImageCount
    {
        get => _datasetImageCount;
        private set
        {
            if (SetProperty(ref _datasetImageCount, value))
            {
                OnPropertyChanged(nameof(HasDatasetStats));
                OnPropertyChanged(nameof(IsSelectedVersionEmpty));
                StartUpscaleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether dataset statistics are available.
    /// </summary>
    public bool HasDatasetStats => DatasetImageCount > 0;

    /// <summary>
    /// Whether the selected version exists but contains no images.
    /// </summary>
    public bool IsSelectedVersionEmpty => SelectedDataset is not null && SelectedDatasetVersion is not null && DatasetImageCount == 0;

    #endregion

    #region Compare

    /// <summary>
    /// Items in the upscale queue with before/after state.
    /// </summary>
    public ObservableCollection<UpscaleImageItemViewModel> UpscaleItems { get; } = [];

    /// <summary>
    /// The item currently selected for before/after comparison.
    /// </summary>
    public UpscaleImageItemViewModel? SelectedCompareItem
    {
        get => _selectedCompareItem;
        set => SetProperty(ref _selectedCompareItem, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Refreshes the dataset list.
    /// </summary>
    public IRelayCommand RefreshDatasetsCommand { get; }

    /// <summary>
    /// Starts batch upscaling via ComfyUI.
    /// </summary>
    public IAsyncRelayCommand StartUpscaleCommand { get; }

    /// <summary>
    /// Cancels a running upscale operation.
    /// </summary>
    public IRelayCommand CancelUpscaleCommand { get; }

    /// <summary>
    /// Selects an item for before/after comparison.
    /// </summary>
    public IRelayCommand<UpscaleImageItemViewModel> SelectCompareItemCommand { get; }

    #endregion

    #region Private Methods

    private bool CanStartUpscale() =>
        !IsProcessing && SelectedDataset is not null && DatasetImageCount > 0 && _comfyUiService is not null;

    private async Task StartUpscaleAsync()
    {
        if (_comfyUiService is null)
        {
            CurrentProcessingStatus = "ComfyUI service not available.";
            return;
        }

        if (SelectedDataset is null || SelectedDatasetVersion is null)
        {
            CurrentProcessingStatus = "No dataset selected.";
            return;
        }

        var versionPath = SelectedDataset.IsVersionedStructure
            ? SelectedDataset.GetVersionFolderPath(SelectedDatasetVersion.Version)
            : SelectedDataset.FolderPath;

        if (!Directory.Exists(versionPath))
        {
            CurrentProcessingStatus = "Version folder not found.";
            return;
        }

        // Resolve the workflow file.
        // Only VisionAutoPrompt uses the vision workflow; all other modes set the
        // prompt text explicitly on node 17, so they use the manual workflow.
        var isVision = PromptMode == UpscalePromptMode.VisionAutoPrompt;
        var workflowRelPath = isVision ? VisionUpscaleWorkflowPath : ManualUpscaleWorkflowPath;
        var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workflowRelPath);

        if (!File.Exists(workflowPath))
        {
            CurrentProcessingStatus = $"Workflow file not found: {Path.GetFileName(workflowRelPath)}";
            Logger.Error("Upscale workflow not found at {Path}", workflowPath);
            return;
        }

        // Gather image files
        var imageFiles = Directory.EnumerateFiles(versionPath)
            .Where(f => DatasetCardViewModel.IsImageFile(f) && !DatasetCardViewModel.IsVideoThumbnailFile(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageFiles.Count == 0)
        {
            CurrentProcessingStatus = "No images found in the selected version.";
            return;
        }

        // Warn when overwriting originals — this is destructive and cannot be undone.
        if (SaveMode == UpscaleSaveMode.OverwriteInPlace && DialogService is not null)
        {
            var confirmed = await DialogService.ShowConfirmAsync(
                "Overwrite Original Images?",
                $"This will permanently replace {imageFiles.Count} original image(s) with their upscaled versions. " +
                "This action cannot be undone.\n\nDo you want to continue?");

            if (!confirmed)
            {
                CurrentProcessingStatus = "Upscale cancelled by user.";
                return;
            }
        }

        // Prepare output folder for NewVersion save mode
        string? newVersionPath = null;
        if (SaveMode == UpscaleSaveMode.NewVersion && SelectedDataset.IsVersionedStructure)
        {
            var maxVersion = SelectedDataset.GetAllVersionNumbers().DefaultIfEmpty(1).Max();
            var newVersion = maxVersion + 1;
            newVersionPath = SelectedDataset.GetVersionFolderPath(newVersion);
            Directory.CreateDirectory(newVersionPath);
            Logger.Information("Upscale: created new version folder {Path}", newVersionPath);
        }
        else if (SaveMode == UpscaleSaveMode.NewVersion && !SelectedDataset.IsVersionedStructure)
        {
            // Non-versioned dataset: create v2 folder
            newVersionPath = Path.Combine(Path.GetDirectoryName(SelectedDataset.FolderPath)!,
                Path.GetFileName(SelectedDataset.FolderPath) + "_upscaled");
            Directory.CreateDirectory(newVersionPath);
        }

        // Clean up any previous temp originals and prepare for OverwriteInPlace comparison
        CleanupCompareOriginalsTempDir();
        if (SaveMode == UpscaleSaveMode.OverwriteInPlace)
        {
            _compareOriginalsTempDir = Path.Combine(Path.GetTempPath(), "DiffusionNexus", "upscale-compare", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_compareOriginalsTempDir);
        }

        // Build the item list for the UI
        UpscaleItems.Clear();
        foreach (var imgPath in imageFiles)
        {
            var item = new UpscaleImageItemViewModel
            {
                FileName = Path.GetFileName(imgPath),
                OriginalPath = imgPath
            };
            await LoadThumbnailAsync(item, imgPath, isOriginal: true);
            UpscaleItems.Add(item);
        }

        // Begin processing
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsProcessing = true;
        CompletedCount = 0;
        TotalImageCount = imageFiles.Count;
        TotalProgress = 0;

        try
        {
            for (var i = 0; i < UpscaleItems.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = UpscaleItems[i];
                item.IsProcessing = true;
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] Uploading {item.FileName}…";

                // 1. Upload image to ComfyUI
                var uploadedFilename = await _comfyUiService.UploadImageAsync(item.OriginalPath, ct);

                // 2. Resolve the positive prompt for this image
                var imagePositivePrompt = ResolvePositivePrompt(item.OriginalPath);

                // 3. Build node modifiers
                var seed = (long)(_random.NextDouble() * long.MaxValue);
                var nodeModifiers = BuildNodeModifiers(uploadedFilename, seed, isVision, imagePositivePrompt);

                // 4. Queue the workflow
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] Queuing workflow for {item.FileName}…";
                var promptId = await _comfyUiService.QueueWorkflowAsync(workflowPath, nodeModifiers, ct);

                // 5. Wait for completion with progress
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] {FunProgressMessages[_random.Next(FunProgressMessages.Length)]}";
                var progress = new Progress<string>(msg =>
                {
                    CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] {msg}";
                });
                await _comfyUiService.WaitForCompletionAsync(promptId, progress, ct);

                // 6. Download result
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] Downloading result…";
                var result = await _comfyUiService.GetResultAsync(promptId, ct);

                if (result.Images.Count > 0)
                {
                    var imageBytes = await _comfyUiService.DownloadImageAsync(result.Images[0], ct);

                    // 7. Save based on save mode
                    var outputPath = GetOutputPath(item.OriginalPath, newVersionPath);

                    // Preserve the original in a temp dir before overwriting so
                    // ImageCompareControl can still show a before/after comparison.
                    if (SaveMode == UpscaleSaveMode.OverwriteInPlace && _compareOriginalsTempDir is not null)
                    {
                        var tempOriginal = Path.Combine(_compareOriginalsTempDir, item.FileName);
                        File.Copy(item.OriginalPath, tempOriginal, overwrite: true);
                        item.OriginalPath = tempOriginal;
                    }

                    await File.WriteAllBytesAsync(outputPath, imageBytes, ct);

                    item.UpscaledPath = outputPath;

                    // Copy caption files when creating a new version
                    if (SaveMode == UpscaleSaveMode.NewVersion && newVersionPath is not null)
                    {
                        CopyCaptionFiles(item.OriginalPath, newVersionPath);
                    }

                    // Load the upscaled thumbnail
                    await LoadThumbnailAsync(item, outputPath, isOriginal: false);

                    Logger.Information("Upscaled {File} -> {Output}", item.FileName, outputPath);
                }
                else
                {
                    Logger.Warning("No output image returned for {File}", item.FileName);
                }

                item.IsProcessing = false;
                item.IsProcessed = true;
                CompletedCount = i + 1;
                TotalProgress = (double)(i + 1) / TotalImageCount * 100;
            }

            CurrentProcessingStatus = $"Done – {CompletedCount}/{TotalImageCount} images upscaled.";

            // Refresh dataset list so the new version appears
            if (SaveMode == UpscaleSaveMode.NewVersion)
            {
                _eventAggregator.PublishRefreshDatasetsRequested(new RefreshDatasetsRequestedEventArgs());
            }
        }
        catch (OperationCanceledException)
        {
            CurrentProcessingStatus = $"Cancelled after {CompletedCount}/{TotalImageCount} images.";
            Logger.Information("Batch upscale cancelled by user after {Count} images", CompletedCount);
        }
        catch (Exception ex)
        {
            CurrentProcessingStatus = $"Error: {ex.Message}";
            Logger.Error(ex, "Batch upscale failed at image {Count}/{Total}", CompletedCount, TotalImageCount);
        }
        finally
        {
            // Clear processing flag on any item that was left mid-processing
            foreach (var item in UpscaleItems.Where(x => x.IsProcessing))
            {
                item.IsProcessing = false;
            }

            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private Dictionary<string, Action<JsonNode>> BuildNodeModifiers(
        string uploadedFilename, long seed, bool isVision, string positivePrompt)
    {
        var modifiers = new Dictionary<string, Action<JsonNode>>
        {
            [LoadImageNodeId] = node =>
            {
                node["inputs"]!["image"] = uploadedFilename;
            },
            [NegativePromptNodeId] = node =>
            {
                node["inputs"]!["text"] = _negativePrompt;
            },
            [UltimateSDUpscaleNodeId] = node =>
            {
                node["inputs"]!["upscale_by"] = _upscaleFactor;
                node["inputs"]!["denoise"] = _denoisingStrength;
                node["inputs"]!["seed"] = seed;
            },
            [SaveImageNodeId] = node =>
            {
                node["inputs"]!["filename_prefix"] = "DiffNexus_Upscale";
            }
        };

        // In vision mode, node 17 is wired to the Qwen3_VQA output chain, so we leave it alone.
        // All other modes set the prompt text explicitly on node 17.
        if (!isVision)
        {
            modifiers[PositivePromptNodeId] = node =>
            {
                node["inputs"]!["text"] = positivePrompt;
            };
        }

        return modifiers;
    }

    private string GetOutputPath(string originalPath, string? newVersionPath)
    {
        var fileName = Path.GetFileName(originalPath);
        return SaveMode switch
        {
            UpscaleSaveMode.NewVersion when newVersionPath is not null
                => Path.Combine(newVersionPath, fileName),
            _ => originalPath // OverwriteInPlace: replace the original
        };
    }

    /// <summary>
    /// Copies all caption/text sidecar files that share the same base name as the source image.
    /// </summary>
    private static void CopyCaptionFiles(string sourceImagePath, string destFolder)
    {
        var dir = Path.GetDirectoryName(sourceImagePath);
        if (dir is null) return;

        var baseName = Path.GetFileNameWithoutExtension(sourceImagePath);
        foreach (var captionFile in Directory.EnumerateFiles(dir)
                     .Where(f => DatasetCardViewModel.IsCaptionFile(f) &&
                                 Path.GetFileNameWithoutExtension(f)
                                     .Equals(baseName, StringComparison.OrdinalIgnoreCase)))
        {
            var destPath = Path.Combine(destFolder, Path.GetFileName(captionFile));
            File.Copy(captionFile, destPath, overwrite: true);
        }
    }

    /// <summary>
    /// Loads a thumbnail bitmap for the given item on the UI thread.
    /// </summary>
    private static async Task LoadThumbnailAsync(UpscaleImageItemViewModel item, string imagePath, bool isOriginal)
    {
        try
        {
            await using var stream = File.OpenRead(imagePath);
            var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(stream, 120));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (isOriginal)
                    item.OriginalThumbnail = bitmap;
                else
                    item.UpscaledThumbnail = bitmap;
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load thumbnail for {Path}", imagePath);
        }
    }

    private void CancelUpscale()
    {
        _cts?.Cancel();
        CurrentProcessingStatus = "Cancelling…";
    }

    /// <summary>
    /// Deletes the temporary directory used to store originals for comparison in OverwriteInPlace mode.
    /// </summary>
    private void CleanupCompareOriginalsTempDir()
    {
        if (_compareOriginalsTempDir is null || !Directory.Exists(_compareOriginalsTempDir))
            return;

        try
        {
            Directory.Delete(_compareOriginalsTempDir, recursive: true);
            Logger.Debug("Cleaned up compare-originals temp dir {Dir}", _compareOriginalsTempDir);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to clean up compare-originals temp dir {Dir}", _compareOriginalsTempDir);
        }

        _compareOriginalsTempDir = null;
    }

    /// <summary>
    /// Resolves the positive prompt for a single image based on the current <see cref="PromptMode"/>.
    /// </summary>
    private string ResolvePositivePrompt(string imagePath)
    {
        return PromptMode switch
        {
            UpscalePromptMode.ManualPrompt => _positivePrompt,
            UpscalePromptMode.FromCaptions => ReadCaptionForImage(imagePath) ?? _positivePrompt,
            UpscalePromptMode.FromMetadata => ReadMetadataPrompt(imagePath) ?? _positivePrompt,
            _ => string.Empty // VisionAutoPrompt — prompt is generated by the vision model node
        };
    }

    /// <summary>
    /// Reads the first non-empty caption sidecar (.txt, .caption) that matches the image base name.
    /// </summary>
    private static string? ReadCaptionForImage(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath);
        if (dir is null) return null;

        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        foreach (var ext in SupportedMediaTypes.CaptionExtensions)
        {
            var captionPath = Path.Combine(dir, baseName + ext);
            if (!File.Exists(captionPath)) continue;

            try
            {
                var text = File.ReadAllText(captionPath).Trim();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            catch (IOException ex)
            {
                Logger.Warning(ex, "Failed to read caption file {Path}", captionPath);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the positive prompt from embedded PNG generation metadata.
    /// </summary>
    private static string? ReadMetadataPrompt(string imagePath)
    {
        try
        {
            var parser = new ImageMetadataParser();
            var data = parser.Parse(imagePath);
            return data.HasData && !string.IsNullOrWhiteSpace(data.PositivePrompt)
                ? data.PositivePrompt
                : null;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to read metadata prompt from {Path}", imagePath);
            return null;
        }
    }

    /// <summary>
    /// Scans the first few images in a version folder and picks the best prompt mode:
    /// captions > metadata > vision auto-prompt.
    /// </summary>
    private void DetectBestPromptMode(string versionPath)
    {
        var imageFiles = Directory.EnumerateFiles(versionPath)
            .Where(f => DatasetCardViewModel.IsImageFile(f) && !DatasetCardViewModel.IsVideoThumbnailFile(f))
            .Take(5)
            .ToList();

        if (imageFiles.Count == 0)
            return;

        // Check if any image has a caption file
        var hasCaptions = imageFiles.Any(f => ReadCaptionForImage(f) is not null);
        if (hasCaptions)
        {
            PromptMode = UpscalePromptMode.FromCaptions;
            return;
        }

        // Check if any image has metadata with a positive prompt
        var hasMetadata = imageFiles.Any(f => ReadMetadataPrompt(f) is not null);
        if (hasMetadata)
        {
            PromptMode = UpscalePromptMode.FromMetadata;
            return;
        }

        PromptMode = UpscalePromptMode.VisionAutoPrompt;
    }

    private void SelectCompareItem(UpscaleImageItemViewModel? item)
    {
        if (SelectedCompareItem is not null)
        {
            SelectedCompareItem.IsSelected = false;
        }

        SelectedCompareItem = item;

        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private void RefreshDatasetStats()
    {
        if (SelectedDataset is null || SelectedDatasetVersion is null)
        {
            DatasetImageCount = 0;
            return;
        }

        var versionPath = SelectedDataset.IsVersionedStructure
            ? SelectedDataset.GetVersionFolderPath(SelectedDatasetVersion.Version)
            : SelectedDataset.FolderPath;

        if (!System.IO.Directory.Exists(versionPath))
        {
            DatasetImageCount = 0;
            return;
        }

        DatasetImageCount = System.IO.Directory.EnumerateFiles(versionPath)
            .Count(f => DatasetCardViewModel.IsImageFile(f) && !DatasetCardViewModel.IsVideoThumbnailFile(f));

        if (DatasetImageCount > 0)
        {
            DetectBestPromptMode(versionPath);
        }
    }

    private void OnRefreshDatasetsRequested(object? sender, RefreshDatasetsRequestedEventArgs e)
    {
        OnPropertyChanged(nameof(AvailableDatasets));
    }

    /// <summary>
    /// Populates an EditorVersionItem collection from a dataset's version folders.
    /// </summary>
    private static void PopulateVersionItems(
        DatasetCardViewModel dataset,
        ObservableCollection<EditorVersionItem> versionItems)
    {
        if (dataset.IsVersionedStructure)
        {
            foreach (var v in dataset.GetAllVersionNumbers())
            {
                var versionPath = dataset.GetVersionFolderPath(v);
                var imageCount = Directory.Exists(versionPath)
                    ? Directory.EnumerateFiles(versionPath)
                        .Count(f => MediaFileExtensions.IsImageFile(f) && !MediaFileExtensions.IsVideoThumbnailFile(f))
                    : 0;
                versionItems.Add(EditorVersionItem.Create(v, imageCount));
            }
        }
        else
        {
            var imageCount = Directory.Exists(dataset.FolderPath)
                ? Directory.EnumerateFiles(dataset.FolderPath)
                    .Count(f => MediaFileExtensions.IsImageFile(f) && !MediaFileExtensions.IsVideoThumbnailFile(f))
                : 0;
            versionItems.Add(EditorVersionItem.Create(1, imageCount));
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        CleanupCompareOriginalsTempDir();
        _eventAggregator.RefreshDatasetsRequested -= OnRefreshDatasetsRequested;
        GC.SuppressFinalize(this);
    }

    #endregion
}
