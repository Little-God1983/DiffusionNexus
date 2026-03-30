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
    private readonly IAppSettingsService? _settingsService;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private string? _compareOriginalsTempDir;
    private DatasetCardViewModel? _tempDataset;
    private List<string>? _tempImagePaths;

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

    // Input selection
    private DatasetCardViewModel? _selectedDataset;
    private EditorVersionItem? _selectedDatasetVersion;
    private string? _singleImagePath;
    private bool _isSingleImageMode;

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
    /// <param name="settingsService">Optional settings service for dataset storage path resolution.</param>
    public BatchUpscaleTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        IComfyUIWrapperService? comfyUiService = null,
        IAppSettingsService? settingsService = null)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _comfyUiService = comfyUiService;
        _settingsService = settingsService;

        AvailableDatasetVersions = [];
        AvailableSaveModes = Enum.GetValues<UpscaleSaveMode>();

        // TODO: Linux Implementation for Batch Upscale

        RefreshDatasetsCommand = new RelayCommand(
            () => _eventAggregator.PublishRefreshDatasetsRequested(new RefreshDatasetsRequestedEventArgs()));

        StartUpscaleCommand = new AsyncRelayCommand(StartUpscaleAsync, CanStartUpscale);
        CancelUpscaleCommand = new RelayCommand(CancelUpscale, () => IsProcessing);
        SelectCompareItemCommand = new RelayCommand<UpscaleImageItemViewModel>(SelectCompareItem);
        SelectSingleImageCommand = new AsyncRelayCommand(SelectSingleImageAsync);
        ClearSingleImageCommand = new RelayCommand(() => SingleImagePath = null);

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
    /// Whether to upscale a single image instead of a dataset.
    /// </summary>
    public bool IsSingleImageMode
    {
        get => _isSingleImageMode;
        set
        {
            if (SetProperty(ref _isSingleImageMode, value))
            {
                if (value) SelectedDataset = null;
                else SingleImagePath = null;
                StartUpscaleCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsDatasetMode));
            }
        }
    }

    /// <summary>
    /// Whether dataset mode is active (inverse of single image mode).
    /// Used for AXAML visibility bindings (e.g. hiding save settings).
    /// </summary>
    public bool IsDatasetMode => !IsSingleImageMode;

    /// <summary>
    /// Path to a single image for upscaling.
    /// </summary>
    public string? SingleImagePath
    {
        get => _singleImagePath;
        set
        {
            if (SetProperty(ref _singleImagePath, value))
            {
                if (!string.IsNullOrEmpty(value)) IsSingleImageMode = true;
                StartUpscaleCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(SingleImageName));
                OnPropertyChanged(nameof(HasSingleImage));
            }
        }
    }

    /// <summary>
    /// Display name for the selected single image.
    /// </summary>
    public string? SingleImageName => Path.GetFileName(SingleImagePath);

    /// <summary>
    /// Whether a single image is currently loaded.
    /// </summary>
    public bool HasSingleImage => !string.IsNullOrEmpty(SingleImagePath);

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
                    IsSingleImageMode = false;
                    if (!value.IsTemporary)
                    {
                        ClearGallerySelection();
                    }
                    PopulateVersionItems(value, AvailableDatasetVersions, _tempImagePaths);
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

    /// <summary>
    /// Opens a file dialog to select a single image for upscaling.
    /// </summary>
    public IAsyncRelayCommand SelectSingleImageCommand { get; }

    /// <summary>
    /// Clears the single image selection.
    /// </summary>
    public IRelayCommand ClearSingleImageCommand { get; }

    #endregion

    #region Private Methods

    private bool CanStartUpscale()
    {
        if (IsProcessing || _comfyUiService is null) return false;

        if (IsSingleImageMode)
            return !string.IsNullOrEmpty(SingleImagePath) && File.Exists(SingleImagePath);

        return SelectedDataset is not null && DatasetImageCount > 0;
    }

    private async Task StartUpscaleAsync()
    {
        if (_comfyUiService is null)
        {
            CurrentProcessingStatus = "ComfyUI service not available.";
            return;
        }

        // --- Single Image Mode ---
        if (IsSingleImageMode)
        {
            if (string.IsNullOrEmpty(SingleImagePath) || !File.Exists(SingleImagePath))
            {
                CurrentProcessingStatus = "No image selected.";
                return;
            }

            var imageFiles = new List<string> { SingleImagePath };
            var outputPath = GetSingleImageOutputPath(SingleImagePath);
            // No save mode / new-version logic for single images
            await RunUpscaleLoopAsync(imageFiles, newVersionPath: null, singleImageOutputPath: outputPath);
            return;
        }

        // --- Dataset Mode ---
        if (SelectedDataset is null || SelectedDatasetVersion is null)
        {
            CurrentProcessingStatus = "No dataset selected.";
            return;
        }

        // Gallery Selection (temp dataset): convert to persistent dataset first
        if (SelectedDataset.IsTemporary && _tempImagePaths is { Count: > 0 })
        {
            if (SaveMode == UpscaleSaveMode.NewVersion)
            {
                var persistentDataset = await ConvertTempDatasetToPersistentAsync();
                if (persistentDataset is null)
                {
                    // Conversion failed (e.g., storage path not configured) — fall back to temp processing
                    await RunUpscaleLoopAsync(_tempImagePaths, newVersionPath: null, singleImageOutputPath: null);
                    return;
                }
                // Fall through to normal dataset processing with the newly persistent dataset
            }
            else
            {
                // OverwriteInPlace on temp images: process directly, no persistent dataset needed
                await RunUpscaleLoopAsync(_tempImagePaths, newVersionPath: null, singleImageOutputPath: null);
                return;
            }
        }

        var versionPath = SelectedDataset.IsVersionedStructure
            ? SelectedDataset.GetVersionFolderPath(SelectedDatasetVersion.Version)
            : SelectedDataset.FolderPath;

        if (!Directory.Exists(versionPath))
        {
            CurrentProcessingStatus = "Version folder not found.";
            return;
        }

        // Gather image files
        var datasetImageFiles = Directory.EnumerateFiles(versionPath)
            .Where(f => DatasetCardViewModel.IsImageFile(f) && !DatasetCardViewModel.IsVideoThumbnailFile(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (datasetImageFiles.Count == 0)
        {
            CurrentProcessingStatus = "No images found in the selected version.";
            return;
        }

        // Warn when overwriting originals — this is destructive and cannot be undone.
        if (SaveMode == UpscaleSaveMode.OverwriteInPlace && DialogService is not null)
        {
            var confirmed = await DialogService.ShowConfirmAsync(
                "Overwrite Original Images?",
                $"This will permanently replace {datasetImageFiles.Count} original image(s) with their upscaled versions. " +
                "This action cannot be undone.\n\nDo you want to continue?");

            if (!confirmed)
            {
                CurrentProcessingStatus = "Upscale cancelled by user.";
                return;
            }
        }

        // Prepare output folder for NewVersion save mode
        string? newVersionPath = null;
        int? newVersionNumber = null;
        int? branchedFromVersion = SelectedDatasetVersion?.Version;
        if (SaveMode == UpscaleSaveMode.NewVersion && SelectedDataset.IsVersionedStructure)
        {
            var maxVersion = SelectedDataset.GetAllVersionNumbers().DefaultIfEmpty(1).Max();
            newVersionNumber = maxVersion + 1;
            newVersionPath = SelectedDataset.GetVersionFolderPath(newVersionNumber.Value);
            Directory.CreateDirectory(newVersionPath);

            // Record the branch in the dataset metadata
            if (branchedFromVersion.HasValue)
            {
                SelectedDataset.RecordBranch(newVersionNumber.Value, branchedFromVersion.Value);
                SelectedDataset.SaveMetadata();
            }

            Logger.Information("Upscale: created new version folder {Path}", newVersionPath);
        }
        else if (SaveMode == UpscaleSaveMode.NewVersion && !SelectedDataset.IsVersionedStructure)
        {
            // Non-versioned dataset: create v2 folder
            newVersionPath = Path.Combine(Path.GetDirectoryName(SelectedDataset.FolderPath)!,
                Path.GetFileName(SelectedDataset.FolderPath) + "_upscaled");
            Directory.CreateDirectory(newVersionPath);
        }

        await RunUpscaleLoopAsync(datasetImageFiles, newVersionPath, singleImageOutputPath: null);

        // If we created a new version, update the dataset model and publish events
        if (newVersionNumber.HasValue && CompletedCount > 0)
        {
            FinalizeVersionCreation(newVersionNumber.Value, branchedFromVersion);
        }
    }

    /// <summary>
    /// Core upscale processing loop shared by single image and dataset modes.
    /// </summary>
    /// <param name="imageFiles">Image file paths to process.</param>
    /// <param name="newVersionPath">Target folder for dataset new-version mode (null otherwise).</param>
    /// <param name="singleImageOutputPath">Explicit output path for single image mode (null in dataset mode).</param>
    private async Task RunUpscaleLoopAsync(
        List<string> imageFiles,
        string? newVersionPath,
        string? singleImageOutputPath)
    {
        var isSingleImage = singleImageOutputPath is not null;

        // Resolve the workflow file.
        var isVision = PromptMode == UpscalePromptMode.VisionAutoPrompt;
        var workflowRelPath = isVision ? VisionUpscaleWorkflowPath : ManualUpscaleWorkflowPath;
        var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workflowRelPath);

        if (!File.Exists(workflowPath))
        {
            CurrentProcessingStatus = $"Workflow file not found: {Path.GetFileName(workflowRelPath)}";
            Logger.Error("Upscale workflow not found at {Path}", workflowPath);
            return;
        }

        // Clean up any previous temp originals and prepare for OverwriteInPlace comparison
        CleanupCompareOriginalsTempDir();
        if (!isSingleImage && SaveMode == UpscaleSaveMode.OverwriteInPlace)
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

                    // 7. Save based on mode
                    var outputPath = isSingleImage
                        ? singleImageOutputPath!
                        : GetOutputPath(item.OriginalPath, newVersionPath);

                    // Preserve the original in a temp dir before overwriting so
                    // ImageCompareControl can still show a before/after comparison.
                    if (!isSingleImage && SaveMode == UpscaleSaveMode.OverwriteInPlace && _compareOriginalsTempDir is not null)
                    {
                        var tempOriginal = Path.Combine(_compareOriginalsTempDir, item.FileName);
                        File.Copy(item.OriginalPath, tempOriginal, overwrite: true);
                        item.OriginalPath = tempOriginal;
                    }

                    await File.WriteAllBytesAsync(outputPath, imageBytes, ct);

                    item.UpscaledPath = outputPath;

                    // Copy caption files when creating a new version (dataset mode only)
                    if (!isSingleImage && SaveMode == UpscaleSaveMode.NewVersion && newVersionPath is not null)
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

            CurrentProcessingStatus = isSingleImage
                ? $"Done – saved to {Path.GetFileName(singleImageOutputPath)}"
                : $"Done – {CompletedCount}/{TotalImageCount} images upscaled.";
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
    /// Uses <see cref="EfficientImageDecoder"/> to avoid full-resolution decode for large images.
    /// </summary>
    private static async Task LoadThumbnailAsync(UpscaleImageItemViewModel item, string imagePath, bool isOriginal)
    {
        try
        {
            var bitmap = await Task.Run(() => EfficientImageDecoder.DecodeThumbnail(imagePath, 120));
            if (bitmap is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (isOriginal)
                        item.OriginalThumbnail = bitmap;
                    else
                        item.UpscaledThumbnail = bitmap;
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load thumbnail for {Path}", imagePath);
        }
    }

    /// <summary>
    /// Converts a temporary (gallery selection) dataset into a persistent dataset
    /// stored in the configured dataset storage path. Copies source images into V1
    /// of the new dataset so that version increment processing targets a real folder.
    /// </summary>
    /// <returns>The persistent dataset, or null if creation failed.</returns>
    private async Task<DatasetCardViewModel?> ConvertTempDatasetToPersistentAsync()
    {
        if (_settingsService is null || SelectedDataset is null || !SelectedDataset.IsTemporary)
            return null;

        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath) || !Directory.Exists(settings.DatasetStoragePath))
        {
            CurrentProcessingStatus = "Dataset storage path is not configured. Please set it in Settings before creating a new version from gallery images.";
            return null;
        }

        // Auto-generate a unique dataset name
        var baseName = $"Gallery Upscale {DateTime.Now:yyyy-MM-dd HH-mm}";
        var datasetPath = Path.Combine(settings.DatasetStoragePath, baseName);

        // Ensure unique name
        var counter = 2;
        while (Directory.Exists(datasetPath))
        {
            datasetPath = Path.Combine(settings.DatasetStoragePath, $"{baseName} ({counter++})");
        }

        var datasetName = Path.GetFileName(datasetPath);
        Directory.CreateDirectory(datasetPath);

        var v1Path = Path.Combine(datasetPath, "V1");
        Directory.CreateDirectory(v1Path);

        // Copy source images from the temp paths into the real V1
        if (_tempImagePaths is { Count: > 0 })
        {
            foreach (var srcPath in _tempImagePaths)
            {
                if (File.Exists(srcPath))
                {
                    var destFile = Path.Combine(v1Path, Path.GetFileName(srcPath));
                    File.Copy(srcPath, destFile, overwrite: true);
                }
            }
        }

        var imageCount = SelectedDataset.ImageCount;
        var newDataset = new DatasetCardViewModel
        {
            Name = datasetName,
            FolderPath = datasetPath,
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = imageCount,
            TotalImageCountAllVersions = imageCount
        };
        newDataset.SaveMetadata();

        // Remove the temp dataset and register the persistent one
        AvailableDatasets.Remove(SelectedDataset);
        ClearGallerySelection();

        if (!AvailableDatasets.Contains(newDataset))
        {
            AvailableDatasets.Add(newDataset);
        }

        _eventAggregator.PublishDatasetCreated(new DatasetCreatedEventArgs
        {
            Dataset = newDataset
        });

        // Select the new persistent dataset and refresh versions
        SelectedDataset = newDataset;
        AvailableDatasetVersions.Clear();
        PopulateVersionItems(newDataset, AvailableDatasetVersions);
        SelectedDatasetVersion = AvailableDatasetVersions.FirstOrDefault();
        RefreshDatasetStats();

        return newDataset;
    }

    /// <summary>
    /// Updates the dataset model and publishes events after a new version is successfully created.
    /// </summary>
    private void FinalizeVersionCreation(int newVersion, int? branchedFromVersion)
    {
        if (SelectedDataset is null) return;

        SelectedDataset.CurrentVersion = newVersion;
        SelectedDataset.IsVersionedStructure = true;
        SelectedDataset.TotalVersions = SelectedDataset.GetAllVersionNumbers().Count();
        SelectedDataset.RefreshImageInfo();

        _eventAggregator.PublishVersionCreated(new VersionCreatedEventArgs
        {
            Dataset = SelectedDataset,
            NewVersion = newVersion,
            BranchedFromVersion = branchedFromVersion ?? 1
        });
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

        // Temp dataset: use stored count, skip disk scan
        if (SelectedDataset.IsTemporary)
        {
            DatasetImageCount = _tempImagePaths?.Count ?? 0;
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
    /// For temporary datasets (Gallery Selection), uses the stored image count.
    /// </summary>
    private static void PopulateVersionItems(
        DatasetCardViewModel dataset,
        ObservableCollection<EditorVersionItem> versionItems,
        List<string>? tempImagePaths = null)
    {
        if (dataset.IsTemporary)
        {
            versionItems.Add(EditorVersionItem.Create(1, tempImagePaths?.Count ?? dataset.ImageCount));
            return;
        }

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

    /// <summary>
    /// Opens a file dialog to select a single image for upscaling.
    /// </summary>
    private async Task SelectSingleImageAsync()
    {
        if (DialogService is null) return;

        var result = await DialogService.ShowOpenFileDialogAsync("Select Image", null);
        if (!string.IsNullOrEmpty(result))
        {
            SingleImagePath = result;
        }
    }

    /// <summary>
    /// Preselects a dataset and version for upscaling.
    /// Called when navigating from Gallery "Send to" after dataset creation.
    /// </summary>
    public void PreselectDataset(DatasetCardViewModel dataset, int version)
    {
        var matchingDataset = AvailableDatasets.FirstOrDefault(d =>
            string.Equals(d.FolderPath, dataset.FolderPath, StringComparison.OrdinalIgnoreCase));

        SelectedDataset = matchingDataset ?? dataset;
        SelectedDatasetVersion = AvailableDatasetVersions.FirstOrDefault(v => v.Version == version)
                                 ?? AvailableDatasetVersions.FirstOrDefault();

        CurrentProcessingStatus = $"Dataset '{dataset.Name}' V{version} loaded for upscaling";
    }

    /// <summary>
    /// Loads a single image for upscaling. Can be called from drag-drop or external navigation.
    /// </summary>
    public void LoadSingleImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        ClearGallerySelection();
        SingleImagePath = imagePath;
    }

    /// <summary>
    /// Loads multiple images as a temporary batch for upscaling.
    /// Injects a "Gallery Selection" dataset into the combo box, matching the Image Comparer pattern.
    /// </summary>
    public void LoadTemporaryImages(IReadOnlyList<string> imagePaths)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);

        var validPaths = imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (validPaths.Count == 0) return;

        _tempImagePaths = validPaths;
        SingleImagePath = null;
        IsSingleImageMode = false;

        // Create a temporary dataset entry for the combo box
        _tempDataset = new DatasetCardViewModel
        {
            Name = "Gallery Selection",
            FolderPath = "TEMP://BatchUpscale",
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = validPaths.Count,
            TotalImageCountAllVersions = validPaths.Count,
            IsTemporary = true
        };

        // Remove any existing temp dataset and insert at the top
        var existingTemp = AvailableDatasets.FirstOrDefault(d => d.IsTemporary);
        if (existingTemp is not null)
        {
            AvailableDatasets.Remove(existingTemp);
        }
        AvailableDatasets.Insert(0, _tempDataset);

        // Select it (triggers PopulateVersionItems + RefreshDatasetStats)
        SelectedDataset = _tempDataset;
        SelectedDatasetVersion = AvailableDatasetVersions.FirstOrDefault();
        CurrentProcessingStatus = $"Gallery Selection: {validPaths.Count} image(s) ready.";
    }

    /// <summary>
    /// Removes the temporary "Gallery Selection" dataset from the combo box.
    /// </summary>
    private void ClearGallerySelection()
    {
        if (_tempDataset is null) return;

        AvailableDatasets.Remove(_tempDataset);
        _tempDataset = null;
        _tempImagePaths = null;
    }

    /// <summary>
    /// Builds the output path for a single image upscale.
    /// Saves next to the original as {name}_upscaled{ext}.
    /// </summary>
    private static string GetSingleImageOutputPath(string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        return Path.Combine(dir, $"{name}_upscaled{ext}");
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
        ClearGallerySelection();
        _eventAggregator.RefreshDatasetsRequested -= OnRefreshDatasetsRequested;
        GC.SuppressFinalize(this);
    }

    #endregion
}
