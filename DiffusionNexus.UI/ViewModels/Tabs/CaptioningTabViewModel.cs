using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Represents a named preset prompt for image captioning.
/// </summary>
public record CaptionPresetPrompt(string Name, string Prompt)
{
    public override string ToString() => Name;
}

/// <summary>
/// Represents how generated captions should be saved relative to the dataset.
/// </summary>
public enum CaptionSaveMode
{
    /// <summary>
    /// Write caption files into the current version folder alongside the images.
    /// </summary>
    CurrentVersion,

    /// <summary>
    /// Create a new dataset version, copy images and existing captions, then write new captions there.
    /// </summary>
    NewVersion
}

/// <summary>
/// Extension methods for <see cref="CaptionSaveMode"/>.
/// </summary>
public static class CaptionSaveModeExtensions
{
    /// <summary>
    /// Gets a user-friendly display name for the save mode.
    /// </summary>
    public static string GetDisplayName(this CaptionSaveMode mode) => mode switch
    {
        CaptionSaveMode.CurrentVersion => "Caption This Version",
        CaptionSaveMode.NewVersion => "New Version",
        _ => mode.ToString()
    };
}

/// <summary>
/// ViewModel for the Captioning tab in the LoRA Dataset Helper.
/// Manages model selection/download, dataset input, captioning settings, and batch caption generation.
/// </summary>
public partial class CaptioningTabViewModel : ViewModelBase, IDialogServiceAware, IDisposable
{
    private readonly ICaptioningService? _captioningService;
    private readonly IReadOnlyList<ICaptioningBackend> _backends;
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private readonly IAppSettingsService? _settingsService;
    private bool _disposed;

    // Backend Selection
    private ICaptioningBackend? _selectedBackend;

    // Model Selection (used by Local Inference backend)
    private CaptioningModelType _selectedModelType = CaptioningModelType.Qwen3_VL_8B;

    // Model Status
    private CaptioningModelStatus _llavaStatus;
    private CaptioningModelStatus _qwenStatus;
    private CaptioningModelStatus _qwen3Status;
    private double _llavaDownloadProgress;
    private double _qwenDownloadProgress;
    private double _qwen3DownloadProgress;
    private string? _statusMessage;

    // Job Config
    private string _systemPrompt = "Describe the image using 100 English words";
    private string? _triggerWord;
    private string? _blacklistedWords;
    private float _temperature = 0.7f;
    private bool _overrideExisting;
    private CaptionSaveMode _captionSaveMode = CaptionSaveMode.CurrentVersion;

    // Prompt Mode
    private bool _isCustomPromptMode;
    private CaptionPresetPrompt? _selectedPresetPrompt;

    // Input Selection
    private DatasetCardViewModel? _selectedDataset;
    private EditorVersionItem? _selectedDatasetVersion;
    private string? _singleImagePath;
    private bool _isSingleImageMode;
    private bool _isCompareMode;
    private DatasetCardViewModel? _tempDataset;
    private List<string>? _tempImagePaths;

    // Processing
    private bool _isProcessing;
    private double _totalProgress;
    private string _currentProcessingStatus = "Ready";
    private CancellationTokenSource? _captioningCts;

    // Dataset statistics
    private int _datasetImageCount;
    private int _datasetCaptionedCount;

    // Live preview
    private string? _currentImagePath;
    private string? _lastCompletedCaption;
    private int _completedCount;
    private int _totalImageCount;
    private Bitmap? _currentImagePreview;
    private CaptionHistoryItemViewModel? _selectedHistoryItem;
    private Bitmap? _selectedGalleryPreview;
    private CaptionHistoryItemViewModel? _currentProcessingItem;

    /// <summary>
    /// Creates a new instance of CaptioningTabViewModel.
    /// </summary>
    public CaptioningTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        ICaptioningService? captioningService = null,
        IReadOnlyList<ICaptioningBackend>? backends = null,
        IAppSettingsService? settingsService = null,
        IComfyUIReadinessService? readinessService = null)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _captioningService = captioningService;
        _backends = backends ?? [];
        _settingsService = settingsService;

        Readiness = new ComfyUIReadinessViewModel(readinessService, ComfyUIFeature.Captioning);

        AvailableDatasetVersions = [];
        AvailableModels = Enum.GetValues<CaptioningModelType>();

        PresetPrompts =
        [
            new("Tags (SD 1.5, SDXL, Pony, Illustrious)",
                "Identify and list all key objects, characters, actions, and artistic styles present in this image. Present the output as a comma-separated list of descriptive tags without any introductory text."),
            new("Simple Description (modern models)",
                "Provide a simple, clear, and concise one-sentence description of the image, focusing only on the primary subject and action. use 100 words"),
            new("Detailed Description",
                "Describe the image in a comprehensive and structured way. Cover the main subjects, their appearance, their placement within the frame, the background, the lighting conditions, and the overall mood. use 200 words"),
            new("Ultra Detailed Description",
                "Perform an exhaustive visual analysis of the image. Describe every element in extreme detail, including subtle textures, fine details on objects, precise color shades, composition techniques (like depth of field), and atmospheric nuances. Leave no detail unmentioned. use 300 words"),
            new("Cinematic Description",
                "Describe this image through the lens of a director or cinematographer. Focus on the cinematic mood, lighting types (e.g., rim lighting, soft light), camera angles, lens effects, color grading, and the dramatic atmosphere of the scene."),
            new("Detailed Analysis",
                "Provide a thorough analysis of the image. Break down the visual components, interpret the narrative or emotional intent behind the scene, and explain how the different elements interact to create the final image.")
        ];
        _selectedPresetPrompt = PresetPrompts[0];
        _systemPrompt = _selectedPresetPrompt.Prompt;

        // Select ComfyUI backend by default, fall back to first available
        _selectedBackend = _backends.FirstOrDefault(b => !b.DisplayName.Contains("Local", StringComparison.OrdinalIgnoreCase))
                           ?? _backends.FirstOrDefault();

        DownloadModelCommand = new AsyncRelayCommand<CaptioningModelType>(DownloadModelAsync, CanDownloadModel);
        GenerateCommand = new AsyncRelayCommand(GenerateCaptionsAsync, CanGenerate);
        SelectSingleImageCommand = new AsyncRelayCommand(SelectSingleImageAsync);
        ClearSingleImageCommand = new RelayCommand(() => SingleImagePath = null);
        ToggleHistoryItemCommand = new RelayCommand<CaptionHistoryItemViewModel>(ToggleHistoryItem);
        PauseCommand = new RelayCommand(PauseCaptioning, () => IsProcessing);
        RefreshDatasetsCommand = new RelayCommand(
            () => _eventAggregator.PublishRefreshDatasetsRequested(new RefreshDatasetsRequestedEventArgs()));

        // Propagate readiness changes to model/generate status
        Readiness.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ComfyUIReadinessViewModel.IsReady) or nameof(ComfyUIReadinessViewModel.HasChecked))
            {
                OnPropertyChanged(nameof(IsModelReady));
                OnPropertyChanged(nameof(IsModelMissing));
                GenerateCommand.NotifyCanExecuteChanged();
            }
        };

        RefreshModelStatuses();

        _eventAggregator.RefreshDatasetsRequested += OnRefreshDatasetsRequested;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public CaptioningTabViewModel() : this(null!, null!, null, null)
    {
    }

    #region Properties

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Unified readiness check for the Captioning feature (server, nodes, models).
    /// </summary>
    public ComfyUIReadinessViewModel Readiness { get; }

    /// <summary>
    /// Whether any captioning backend is available (local inference or ComfyUI).
    /// </summary>
    public bool IsServiceAvailable => _captioningService is not null || _backends.Count > 0;

    /// <summary>
    /// Whether the native LLama library loaded successfully.
    /// Only relevant when the Local Inference backend is selected.
    /// </summary>
    public bool IsNativeLibraryLoaded => _captioningService?.IsNativeLibraryLoaded ?? false;

    /// <summary>
    /// Whether the selected backend uses local model management (download, load, etc.).
    /// Controls visibility of the model selection panel in the UI.
    /// </summary>
    public bool IsLocalInferenceBackend => SelectedBackend is null
        || SelectedBackend.DisplayName.Contains("Local", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Available captioning backends visible to the user.
    /// NOTE: Local Inference (LlamaSharp) is temporarily hidden until fully implemented — do not delete it.
    /// </summary>
    public IReadOnlyList<ICaptioningBackend> AvailableBackends =>
        _backends.Where(b => !b.DisplayName.Contains("Local", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The currently selected captioning backend.
    /// </summary>
    public ICaptioningBackend? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (SetProperty(ref _selectedBackend, value))
            {
                OnPropertyChanged(nameof(IsLocalInferenceBackend));
                OnPropertyChanged(nameof(IsModelReady));
                OnPropertyChanged(nameof(IsModelMissing));
                GenerateCommand.NotifyCanExecuteChanged();
                Readiness.CheckReadinessCommand.Execute(null);
            }
        }
    }

    /// <summary>
    /// Whether the selected backend is available and ready.
    /// Delegates to the unified <see cref="Readiness"/> check.
    /// </summary>
    public bool IsBackendAvailable => Readiness.IsReady;

    /// <summary>
    /// Available captioning model types (for local inference backend).
    /// </summary>
    public IReadOnlyList<CaptioningModelType> AvailableModels { get; }

    /// <summary>
    /// Available datasets from shared state.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> AvailableDatasets => _state.Datasets;

    /// <summary>
    /// Available versions for the selected dataset.
    /// </summary>
    public ObservableCollection<EditorVersionItem> AvailableDatasetVersions { get; }

    /// <summary>
    /// The selected captioning model type.
    /// </summary>
    public CaptioningModelType SelectedModelType
    {
        get => _selectedModelType;
        set
        {
            if (SetProperty(ref _selectedModelType, value))
            {
                OnPropertyChanged(nameof(IsModelReady));
                OnPropertyChanged(nameof(IsModelMissing));
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    #region Model Status Properties

    /// <summary>
    /// Status of the LLaVA model.
    /// </summary>
    public CaptioningModelStatus LlavaStatus
    {
        get => _llavaStatus;
        private set
        {
            if (SetProperty(ref _llavaStatus, value))
            {
                OnPropertyChanged(nameof(IsLlavaReady));
                OnPropertyChanged(nameof(IsLlavaMissing));
                OnPropertyChanged(nameof(IsLlavaDownloading));
                OnPropertyChanged(nameof(LlavaStatusText));
                RefreshGlobalModelStatus();
            }
        }
    }

    /// <summary>
    /// Status of the Qwen 2.5 VL model.
    /// </summary>
    public CaptioningModelStatus QwenStatus
    {
        get => _qwenStatus;
        private set
        {
            if (SetProperty(ref _qwenStatus, value))
            {
                OnPropertyChanged(nameof(IsQwenReady));
                OnPropertyChanged(nameof(IsQwenMissing));
                OnPropertyChanged(nameof(IsQwenDownloading));
                OnPropertyChanged(nameof(QwenStatusText));
                RefreshGlobalModelStatus();
            }
        }
    }

    /// <summary>
    /// Status of the Qwen 3 VL model.
    /// </summary>
    public CaptioningModelStatus Qwen3Status
    {
        get => _qwen3Status;
        private set
        {
            if (SetProperty(ref _qwen3Status, value))
            {
                OnPropertyChanged(nameof(IsQwen3Ready));
                OnPropertyChanged(nameof(IsQwen3Missing));
                OnPropertyChanged(nameof(IsQwen3Downloading));
                OnPropertyChanged(nameof(Qwen3StatusText));
                RefreshGlobalModelStatus();
            }
        }
    }

    public double LlavaDownloadProgress
    {
        get => _llavaDownloadProgress;
        private set => SetProperty(ref _llavaDownloadProgress, value);
    }

    public double QwenDownloadProgress
    {
        get => _qwenDownloadProgress;
        private set => SetProperty(ref _qwenDownloadProgress, value);
    }

    public double Qwen3DownloadProgress
    {
        get => _qwen3DownloadProgress;
        private set => SetProperty(ref _qwen3DownloadProgress, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // LLaVA computed helpers
    public bool IsLlavaReady => LlavaStatus is CaptioningModelStatus.Ready or CaptioningModelStatus.Loaded;
    public bool IsLlavaMissing => LlavaStatus is CaptioningModelStatus.NotDownloaded or CaptioningModelStatus.Corrupted;
    public bool IsLlavaDownloading => LlavaStatus is CaptioningModelStatus.Downloading;
    public string LlavaStatusText => GetStatusText(LlavaStatus);

    // Qwen 2.5 computed helpers
    public bool IsQwenReady => QwenStatus is CaptioningModelStatus.Ready or CaptioningModelStatus.Loaded;
    public bool IsQwenMissing => QwenStatus is CaptioningModelStatus.NotDownloaded or CaptioningModelStatus.Corrupted;
    public bool IsQwenDownloading => QwenStatus is CaptioningModelStatus.Downloading;
    public string QwenStatusText => GetStatusText(QwenStatus);

    // Qwen 3 computed helpers
    public bool IsQwen3Ready => Qwen3Status is CaptioningModelStatus.Ready or CaptioningModelStatus.Loaded;
    public bool IsQwen3Missing => Qwen3Status is CaptioningModelStatus.NotDownloaded or CaptioningModelStatus.Corrupted;
    public bool IsQwen3Downloading => Qwen3Status is CaptioningModelStatus.Downloading;
    public string Qwen3StatusText => GetStatusText(Qwen3Status);

    /// <summary>
    /// Whether the currently selected backend/model is ready for inference.
    /// For ComfyUI backends this is driven by server availability.
    /// For local inference this checks the model download status.
    /// </summary>
    public bool IsModelReady
    {
        get
        {
            if (!IsLocalInferenceBackend)
            {
                return Readiness.IsReady;
            }

            return SelectedModelType switch
            {
                CaptioningModelType.LLaVA_v1_6_34B => IsLlavaReady,
                CaptioningModelType.Qwen2_5_VL_7B => IsQwenReady,
                CaptioningModelType.Qwen3_VL_8B => IsQwen3Ready,
                _ => false
            };
        }
    }

    /// <summary>
    /// Whether the currently selected model/backend is not ready.
    /// </summary>
    public bool IsModelMissing => !IsModelReady;

    #endregion

    #region Config Properties

    /// <summary>
    /// Available preset prompts for image captioning.
    /// </summary>
    public IReadOnlyList<CaptionPresetPrompt> PresetPrompts { get; }

    /// <summary>
    /// The currently selected preset prompt. Drives SystemPrompt when not in custom mode.
    /// </summary>
    public CaptionPresetPrompt? SelectedPresetPrompt
    {
        get => _selectedPresetPrompt;
        set
        {
            if (SetProperty(ref _selectedPresetPrompt, value) && !IsCustomPromptMode && value is not null)
            {
                SystemPrompt = value.Prompt;
            }
        }
    }

    /// <summary>
    /// When true the user can type a custom system prompt; when false a preset dropdown is used.
    /// </summary>
    public bool IsCustomPromptMode
    {
        get => _isCustomPromptMode;
        set
        {
            if (SetProperty(ref _isCustomPromptMode, value))
            {
                if (value)
                {
                    SystemPrompt = string.Empty;
                }
                else if (_selectedPresetPrompt is not null)
                {
                    SystemPrompt = _selectedPresetPrompt.Prompt;
                }
            }
        }
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetProperty(ref _systemPrompt, value);
    }

    public string? TriggerWord
    {
        get => _triggerWord;
        set => SetProperty(ref _triggerWord, value);
    }

    public string? BlacklistedWords
    {
        get => _blacklistedWords;
        set => SetProperty(ref _blacklistedWords, value);
    }

    public float Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, value);
    }

    public bool OverrideExisting
    {
        get => _overrideExisting;
        set => SetProperty(ref _overrideExisting, value);
    }

    /// <summary>
    /// How generated captions should be saved relative to the dataset.
    /// </summary>
    public CaptionSaveMode SaveMode
    {
        get => _captionSaveMode;
        set
        {
            if (SetProperty(ref _captionSaveMode, value))
            {
                OnPropertyChanged(nameof(SaveModeDescription));
                OnPropertyChanged(nameof(IsNewVersionMode));
            }
        }
    }

    /// <summary>
    /// Human-readable description of the selected save mode.
    /// </summary>
    public string SaveModeDescription => SaveMode switch
    {
        CaptionSaveMode.CurrentVersion => "Writes captions into the current version folder.",
        CaptionSaveMode.NewVersion => "Creates a new version and copies captions. Original images are untouched.",
        _ => string.Empty
    };

    /// <summary>
    /// Available save modes for the ComboBox.
    /// </summary>
    public IReadOnlyList<CaptionSaveMode> AvailableSaveModes { get; } = Enum.GetValues<CaptionSaveMode>();

    /// <summary>
    /// Whether the user selected 'New Version' save mode (used for AXAML visibility bindings).
    /// </summary>
    public bool IsNewVersionMode => SaveMode == CaptionSaveMode.NewVersion;

    #endregion

    #region Dataset Statistics

    /// <summary>
    /// Total number of images in the selected dataset version.
    /// </summary>
    public int DatasetImageCount
    {
        get => _datasetImageCount;
        private set => SetProperty(ref _datasetImageCount, value);
    }

    /// <summary>
    /// Number of images that already have caption (.txt) files.
    /// </summary>
    public int DatasetCaptionedCount
    {
        get => _datasetCaptionedCount;
        private set => SetProperty(ref _datasetCaptionedCount, value);
    }

    /// <summary>
    /// Number of images that still need captions.
    /// </summary>
    public int DatasetUncaptionedCount => DatasetImageCount - DatasetCaptionedCount;

    /// <summary>
    /// Whether dataset statistics are available to display.
    /// </summary>
    public bool HasDatasetStats => DatasetImageCount > 0;

    #endregion

    #region Input Properties

    /// <summary>
    /// The selected dataset to caption.
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

                GenerateCommand.NotifyCanExecuteChanged();
                RefreshDatasetStats();
            }
        }
    }

    /// <summary>
    /// The selected dataset version to caption.
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

    /// <summary>
    /// Path to a single image for captioning.
    /// </summary>
    public string? SingleImagePath
    {
        get => _singleImagePath;
        set
        {
            if (SetProperty(ref _singleImagePath, value))
            {
                if (!string.IsNullOrEmpty(value)) IsSingleImageMode = true;
                GenerateCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(SingleImageName));
                OnPropertyChanged(nameof(HasSingleImage));
                OnPropertyChanged(nameof(SingleImageHasExistingCaption));

                // Auto-enable compare mode when the image already has a caption
                IsCompareMode = HasExistingCaption(value);
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
    /// Whether to open a compare dialog when a single image already has a caption.
    /// Automatically enabled when the selected single image has an existing .txt caption file.
    /// </summary>
    public bool IsCompareMode
    {
        get => _isCompareMode;
        set => SetProperty(ref _isCompareMode, value);
    }

    /// <summary>
    /// Whether the selected single image already has a caption file on disk.
    /// </summary>
    public bool SingleImageHasExistingCaption => HasExistingCaption(SingleImagePath);

    /// <summary>
    /// Whether to caption a single image instead of a dataset.
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
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    #endregion

    #region Processing Properties

    /// <summary>
    /// Whether captioning is in progress.
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                GenerateCommand.NotifyCanExecuteChanged();
                DownloadModelCommand.NotifyCanExecuteChanged();
                PauseCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Overall progress percentage (0-100).
    /// </summary>
    public double TotalProgress
    {
        get => _totalProgress;
        private set => SetProperty(ref _totalProgress, value);
    }

    /// <summary>
    /// Current processing status message.
    /// </summary>
    public string CurrentProcessingStatus
    {
        get => _currentProcessingStatus;
        private set => SetProperty(ref _currentProcessingStatus, value);
    }

    /// <summary>
    /// Path to the image currently being processed.
    /// </summary>
    public string? CurrentImagePath
    {
        get => _currentImagePath;
        private set
        {
            if (SetProperty(ref _currentImagePath, value))
            {
                var old = _currentImagePreview;
                CurrentImagePreview = LoadPreviewBitmap(value);
                old?.Dispose();
            }
        }
    }

    /// <summary>
    /// Preview bitmap for the currently processing image.
    /// </summary>
    public Bitmap? CurrentImagePreview
    {
        get => _currentImagePreview;
        private set => SetProperty(ref _currentImagePreview, value);
    }

    /// <summary>
    /// History of all completed caption results in the current batch.
    /// </summary>
    public ObservableCollection<CaptionHistoryItemViewModel> CaptionHistory { get; } = [];

    /// <summary>
    /// The currently selected history item (for viewing full caption and large preview).
    /// </summary>
    public CaptionHistoryItemViewModel? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (SetProperty(ref _selectedHistoryItem, value))
            {
                OnPropertyChanged(nameof(DisplayCaption));

                var old = _selectedGalleryPreview;
                SelectedGalleryPreview = LoadPreviewBitmap(value?.ImagePath);
                old?.Dispose();
            }
        }
    }

    /// <summary>
    /// Large preview bitmap for the selected gallery item.
    /// </summary>
    public Bitmap? SelectedGalleryPreview
    {
        get => _selectedGalleryPreview;
        private set => SetProperty(ref _selectedGalleryPreview, value);
    }

    /// <summary>
    /// The history item currently being processed (for highlighting in the gallery).
    /// </summary>
    public CaptionHistoryItemViewModel? CurrentProcessingItem
    {
        get => _currentProcessingItem;
        private set => SetProperty(ref _currentProcessingItem, value);
    }

    /// <summary>
    /// Caption text generated for the last completed image.
    /// </summary>
    public string? LastCompletedCaption
    {
        get => _lastCompletedCaption;
        private set => SetProperty(ref _lastCompletedCaption, value);
    }

    /// <summary>
    /// The caption to show in the detail area: selected item's full caption, or last completed.
    /// </summary>
    public string? DisplayCaption => SelectedHistoryItem?.FullCaption ?? LastCompletedCaption;

    /// <summary>
    /// Number of images completed so far.
    /// </summary>
    public int CompletedCount
    {
        get => _completedCount;
        private set => SetProperty(ref _completedCount, value);
    }

    /// <summary>
    /// Total number of images in the current batch.
    /// </summary>
    public int TotalImageCount
    {
        get => _totalImageCount;
        private set => SetProperty(ref _totalImageCount, value);
    }

    #endregion

    #endregion

    #region Commands

    /// <summary>
    /// Command to download a specific model.
    /// </summary>
    public IAsyncRelayCommand<CaptioningModelType> DownloadModelCommand { get; }

    /// <summary>
    /// Command to start generating captions.
    /// </summary>
    public IAsyncRelayCommand GenerateCommand { get; }

    /// <summary>
    /// Command to select a single image file.
    /// </summary>
    public IAsyncRelayCommand SelectSingleImageCommand { get; }

    /// <summary>
    /// Command to clear the selected single image.
    /// </summary>
    public IRelayCommand ClearSingleImageCommand { get; }

    /// <summary>
    /// Command to toggle a history item's expanded state and show its caption in the detail area.
    /// </summary>
    public IRelayCommand<CaptionHistoryItemViewModel> ToggleHistoryItemCommand { get; }

    /// <summary>
    /// Command to pause captioning after the current image finishes.
    /// </summary>
    public IRelayCommand PauseCommand { get; }

    /// <summary>
    /// Command to refresh the available datasets list.
    /// </summary>
    public IRelayCommand RefreshDatasetsCommand { get; }

    #endregion

    #region Private Methods

    private void PauseCaptioning()
    {
        _captioningCts?.Cancel();
        CurrentProcessingStatus = "Stopping after current image...";
    }

    private static string GetStatusText(CaptioningModelStatus status) => status switch
    {
        CaptioningModelStatus.NotDownloaded => "Not downloaded",
        CaptioningModelStatus.Downloading => "Downloading...",
        CaptioningModelStatus.Ready => "Ready",
        CaptioningModelStatus.Corrupted => "Corrupted - re-download required",
        CaptioningModelStatus.Loaded => "Loaded in memory",
        _ => "Unknown"
    };

    private void RefreshModelStatuses()
    {
        if (_captioningService is null) return;

        if (!_captioningService.IsNativeLibraryLoaded)
        {
            StatusMessage = $"LLama native library failed to load: {_captioningService.NativeLibraryError ?? "unknown error"}. Ensure CUDA toolkit is installed or check backend compatibility.";
        }

        try
        {
            var llavaInfo = _captioningService.GetModelInfo(CaptioningModelType.LLaVA_v1_6_34B);
            LlavaStatus = llavaInfo.Status;

            var qwenInfo = _captioningService.GetModelInfo(CaptioningModelType.Qwen2_5_VL_7B);
            QwenStatus = qwenInfo.Status;

            var qwen3Info = _captioningService.GetModelInfo(CaptioningModelType.Qwen3_VL_8B);
            Qwen3Status = qwen3Info.Status;
        }
        catch
        {
            LlavaStatus = CaptioningModelStatus.NotDownloaded;
            QwenStatus = CaptioningModelStatus.NotDownloaded;
            Qwen3Status = CaptioningModelStatus.NotDownloaded;
        }
    }

    private void RefreshGlobalModelStatus()
    {
        OnPropertyChanged(nameof(IsModelReady));
        OnPropertyChanged(nameof(IsModelMissing));
        DownloadModelCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private bool CanDownloadModel(CaptioningModelType modelType)
    {
        if (_captioningService is null || IsProcessing) return false;

        return modelType switch
        {
            CaptioningModelType.LLaVA_v1_6_34B => !IsLlavaDownloading,
            CaptioningModelType.Qwen2_5_VL_7B => !IsQwenDownloading,
            CaptioningModelType.Qwen3_VL_8B => !IsQwen3Downloading,
            _ => false
        };
    }

    private async Task DownloadModelAsync(CaptioningModelType modelType)
    {
        if (_captioningService is null || IsProcessing) return;

        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                switch (modelType)
                {
                    case CaptioningModelType.LLaVA_v1_6_34B:
                        LlavaDownloadProgress = p.Percentage;
                        break;
                    case CaptioningModelType.Qwen2_5_VL_7B:
                        QwenDownloadProgress = p.Percentage;
                        break;
                    case CaptioningModelType.Qwen3_VL_8B:
                        Qwen3DownloadProgress = p.Percentage;
                        break;
                }

                StatusMessage = $"Downloading {modelType}: {p.Status}";
            });

            // Update status to downloading immediately for UI feedback
            switch (modelType)
            {
                case CaptioningModelType.LLaVA_v1_6_34B:
                    LlavaStatus = CaptioningModelStatus.Downloading;
                    break;
                case CaptioningModelType.Qwen2_5_VL_7B:
                    QwenStatus = CaptioningModelStatus.Downloading;
                    break;
                case CaptioningModelType.Qwen3_VL_8B:
                    Qwen3Status = CaptioningModelStatus.Downloading;
                    break;
            }

            var success = await _captioningService.DownloadModelAsync(modelType, progress);
            StatusMessage = success
                ? $"{modelType} downloaded successfully"
                : $"Failed to download {modelType}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error downloading {modelType}: {ex.Message}";
        }
        finally
        {
            RefreshModelStatuses();
        }
    }

    private bool CanGenerate()
    {
        if (IsProcessing) return false;

        var hasInput = SelectedDataset is not null
            || (!string.IsNullOrEmpty(SingleImagePath) && File.Exists(SingleImagePath));
        if (!hasInput) return false;

        // ComfyUI / non-local backend
        if (!IsLocalInferenceBackend)
        {
            return SelectedBackend is not null && Readiness.IsReady;
        }

        // Local inference backend
        return _captioningService is not null
            && _captioningService.IsNativeLibraryLoaded
            && IsModelReady;
    }

    private async Task SelectSingleImageAsync()
    {
        if (DialogService is null) return;

        var result = await DialogService.ShowOpenFileDialogAsync("Select Image", null);
        if (!string.IsNullOrEmpty(result))
        {
            SingleImagePath = result;
        }
    }

    private async Task GenerateCaptionsAsync()
    {
        if (!CanGenerate()) return;

        // In compare mode for single image, capture the existing caption before generation
        var compareModeActive = IsSingleImageMode && IsCompareMode && !string.IsNullOrEmpty(SingleImagePath);
        var existingCaption = compareModeActive ? ReadExistingCaption(SingleImagePath!) : string.Empty;

        IsProcessing = true;
        TotalProgress = 0;
        CurrentProcessingStatus = "Initializing...";
        CurrentImagePath = null;
        LastCompletedCaption = null;
        SelectedHistoryItem = null;
        CompletedCount = 0;
        TotalImageCount = 0;
        ClearHistory();

        _captioningCts = new CancellationTokenSource();

        try
        {
            var blackList = BlacklistedWords?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .ToList();

            // Force OverrideExisting when in compare mode so the backend does not skip the image
            var effectiveOverride = compareModeActive || OverrideExisting;

            // Determine dataset path: create a new version folder when requested
            string? datasetPath = SelectedDataset?.CurrentVersionFolderPath;
            int? newVersionNumber = null;
            int? branchedFromVersion = SelectedDatasetVersion?.Version;
            if (!IsSingleImageMode && SaveMode == CaptionSaveMode.NewVersion && SelectedDataset is not null)
            {
                // For temporary datasets (gallery selection), create a real persistent dataset first
                if (SelectedDataset.IsTemporary)
                {
                    var persistentDataset = await ConvertTempDatasetToPersistentAsync();
                    if (persistentDataset is null)
                    {
                        // Conversion failed (e.g., storage path not configured)
                        return;
                    }
                    branchedFromVersion = 1;
                }

                // Calculate the new version number before creating the folder
                newVersionNumber = SelectedDataset.GetAllVersionNumbers().DefaultIfEmpty(1).Max() + 1;

                datasetPath = PrepareNewVersionFolder(SelectedDataset, !effectiveOverride);
                // New version has fresh caption files (or copies), so always override into that folder
                effectiveOverride = true;
            }

            // Pre-populate gallery with all images (like Batch Upscale)
            var imagePaths = GetImagePaths().ToList();
            foreach (var imgPath in imagePaths)
            {
                var thumbnail = LoadPreviewBitmap(imgPath, 120);
                var existingCaptionText = ReadExistingCaption(imgPath);
                var alreadyCaptioned = !effectiveOverride && existingCaptionText.Length > 0;

                var item = new CaptionHistoryItemViewModel(
                    imgPath,
                    alreadyCaptioned ? existingCaptionText : string.Empty,
                    thumbnail)
                {
                    IsCaptionCompleted = alreadyCaptioned
                };
                CaptionHistory.Add(item);
            }

            var config = new CaptioningJobConfig(
                ImagePaths: imagePaths,
                SelectedModel: SelectedModelType,
                SystemPrompt: SystemPrompt,
                TriggerWord: TriggerWord,
                BlacklistedWords: blackList,
                DatasetPath: datasetPath,
                OverrideExisting: effectiveOverride,
                Temperature: Temperature
            );

            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                StatusMessage = $"Validation failed: {string.Join(", ", validationErrors)}";
                return;
            }

            var ct = _captioningCts.Token;

            var progress = new Progress<CaptioningProgress>(p =>
            {
                TotalProgress = p.Percentage;
                CurrentProcessingStatus = p.Status;
                TotalImageCount = p.TotalCount;
                CompletedCount = p.CompletedCount;
                CurrentImagePath = p.CurrentImagePath;

                // Mark previous processing item as no longer processing
                if (CurrentProcessingItem is not null && CurrentProcessingItem.ImagePath != p.CurrentImagePath)
                {
                    CurrentProcessingItem.IsProcessing = false;
                }

                // Highlight the currently processing image with orange border
                if (p.CurrentImagePath is not null)
                {
                    var processingItem = CaptionHistory.FirstOrDefault(
                        h => h.ImagePath == p.CurrentImagePath);
                    if (processingItem is not null && !processingItem.IsCaptionCompleted)
                    {
                        processingItem.IsProcessing = true;
                        CurrentProcessingItem = processingItem;
                    }
                }

                // Update the completed item with its caption
                if (p.LastResult is { Success: true, WasSkipped: false, Caption: not null })
                {
                    LastCompletedCaption = p.LastResult.Caption;
                    OnPropertyChanged(nameof(DisplayCaption));

                    var existing = CaptionHistory.FirstOrDefault(
                        h => h.ImagePath == p.LastResult.ImagePath);
                    existing?.SetCompletedCaption(p.LastResult.Caption);

                    RefreshDatasetStats();
                }
            });

            IReadOnlyList<CaptioningResult> results;

            if (!IsLocalInferenceBackend && SelectedBackend is not null)
            {
                results = await SelectedBackend.GenerateBatchCaptionsAsync(config, progress, ct);
            }
            else if (_captioningService is not null)
            {
                results = await _captioningService.GenerateCaptionsAsync(config, progress, ct);
            }
            else
            {
                StatusMessage = "No captioning backend is available.";
                return;
            }

            var successCount = results.Count(r => r.Success);
            StatusMessage = $"Completed! {successCount}/{results.Count} images captioned.";
            CurrentProcessingStatus = "Done";

            // If we created a new version, update the dataset model and publish events
            if (newVersionNumber.HasValue && SelectedDataset is not null && successCount > 0)
            {
                FinalizeVersionCreation(SelectedDataset, newVersionNumber.Value, branchedFromVersion);
            }

            // Show compare dialog when in compare mode and we got a new caption
            if (compareModeActive && DialogService is not null)
            {
                var firstSuccess = results.FirstOrDefault(r => r is { Success: true, WasSkipped: false, Caption: not null });
                if (firstSuccess?.Caption is not null)
                {
                    await ShowCaptionCompareAsync(SingleImagePath!, existingCaption, firstSuccess.Caption);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Paused after {CompletedCount}/{TotalImageCount} images. Press Generate to continue (already-captioned images will be skipped).";
            CurrentProcessingStatus = "Paused";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Job failed: {ex.Message}";
            CurrentProcessingStatus = "Failed";
        }
        finally
        {
            if (CurrentProcessingItem is not null)
            {
                CurrentProcessingItem.IsProcessing = false;
            }
            CurrentProcessingItem = null;
            _captioningCts?.Dispose();
            _captioningCts = null;
            IsProcessing = false;
            RefreshDatasetStats();
        }
    }

    /// <summary>
    /// Shows the caption compare dialog and saves the user's chosen caption.
    /// </summary>
    private async Task ShowCaptionCompareAsync(string imagePath, string existingCaption, string newCaption)
    {
        if (DialogService is null) return;

        var result = await DialogService.ShowCaptionCompareDialogAsync(imagePath, existingCaption, newCaption);

        if (result.Confirmed && result.ChosenCaption is not null)
        {
            var dir = Path.GetDirectoryName(imagePath) ?? ".";
            var captionPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(imagePath) + ".txt");
            await File.WriteAllTextAsync(captionPath, result.ChosenCaption);
            StatusMessage = "Caption saved from compare dialog.";
        }
        else
        {
            StatusMessage = "Compare cancelled — the newly generated caption remains on disk.";
        }
    }

    /// <summary>
    /// Converts a temporary (gallery selection) dataset into a persistent dataset
    /// stored in the configured dataset storage path. Copies source images into V1
    /// of the new dataset so that version creation targets a real folder.
    /// </summary>
    /// <returns>The persistent dataset, or null if creation failed.</returns>
    private async Task<DatasetCardViewModel?> ConvertTempDatasetToPersistentAsync()
    {
        if (_settingsService is null || SelectedDataset is null || !SelectedDataset.IsTemporary)
            return null;

        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath) || !Directory.Exists(settings.DatasetStoragePath))
        {
            StatusMessage = "Dataset storage path is not configured. Please set it in Settings before creating a new version from gallery images.";
            return null;
        }

        // Auto-generate a unique dataset name
        var baseName = $"Gallery Caption {DateTime.Now:yyyy-MM-dd HH-mm}";
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

        // Copy temp images from their original locations to V1
        if (_tempImagePaths is { Count: > 0 })
        {
            foreach (var imgPath in _tempImagePaths)
            {
                if (File.Exists(imgPath))
                {
                    File.Copy(imgPath, Path.Combine(v1Path, Path.GetFileName(imgPath)), overwrite: true);
                }
            }
        }

        var imageCount = _tempImagePaths?.Count ?? 0;
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
        SelectedDatasetVersion = AvailableDatasetVersions.FirstOrDefault();
        RefreshDatasetStats();

        return newDataset;
    }

    /// <summary>
    /// Updates the dataset model and publishes events after a new version is successfully created.
    /// </summary>
    private void FinalizeVersionCreation(DatasetCardViewModel dataset, int newVersion, int? branchedFromVersion)
    {
        dataset.RecordBranch(newVersion, branchedFromVersion ?? 1);
        dataset.CurrentVersion = newVersion;
        dataset.IsVersionedStructure = true;
        dataset.TotalVersions = dataset.GetAllVersionNumbers().Count();
        dataset.SaveMetadata();
        dataset.RefreshImageInfo();

        _eventAggregator.PublishVersionCreated(new VersionCreatedEventArgs
        {
            Dataset = dataset,
            NewVersion = newVersion,
            BranchedFromVersion = branchedFromVersion ?? 1
        });
    }

    /// <summary>
    /// Creates a new version folder for the dataset, copies all images from the current version,
    /// and optionally copies existing caption (.txt) files when preserving captions.
    /// </summary>
    /// <returns>The absolute path of the newly created version folder.</returns>
    private static string PrepareNewVersionFolder(DatasetCardViewModel dataset, bool copyExistingCaptions)
    {
        string newVersionPath;

        if (dataset.IsVersionedStructure)
        {
            var maxVersion = dataset.GetAllVersionNumbers().DefaultIfEmpty(1).Max();
            newVersionPath = dataset.GetVersionFolderPath(maxVersion + 1);
        }
        else
        {
            newVersionPath = Path.Combine(
                Path.GetDirectoryName(dataset.FolderPath)!,
                Path.GetFileName(dataset.FolderPath) + "_captioned");
        }

        Directory.CreateDirectory(newVersionPath);

        // Copy all images from the source version into the new folder
        var sourcePath = dataset.CurrentVersionFolderPath;
        string[] imageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

        foreach (var file in Directory.EnumerateFiles(sourcePath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var isImage = imageExtensions.Contains(ext);
            var isCaption = DatasetCardViewModel.IsCaptionFile(file);

            if (isImage || (isCaption && copyExistingCaptions))
            {
                File.Copy(file, Path.Combine(newVersionPath, Path.GetFileName(file)), overwrite: true);
            }
        }

        return newVersionPath;
    }

    private IEnumerable<string> GetImagePaths()
    {
        if (IsSingleImageMode && !string.IsNullOrEmpty(SingleImagePath))
        {
            return [SingleImagePath];
        }

        // Gallery Selection: return stored paths directly
        if (SelectedDataset is not null && SelectedDataset.IsTemporary && _tempImagePaths is { Count: > 0 })
        {
            return _tempImagePaths;
        }

        if (SelectedDataset is not null)
        {
            var path = SelectedDataset.CurrentVersionFolderPath;
            if (Directory.Exists(path))
            {
                string[] extensions = [".jpg", ".jpeg", ".png", ".webp"];
                return Directory.EnumerateFiles(path)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            }
        }

        return [];
    }

    /// <summary>
    /// Checks if a caption (.txt) file already exists for the given image path.
    /// </summary>
    private static bool HasExistingCaption(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
            return false;

        var dir = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(dir))
            return false;

        var captionPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(imagePath) + ".txt");
        return File.Exists(captionPath);
    }

    /// <summary>
    /// Reads the existing caption text for a given image path.
    /// </summary>
    private static string ReadExistingCaption(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath) ?? ".";
        var captionPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(imagePath) + ".txt");
        return File.Exists(captionPath) ? File.ReadAllText(captionPath).Trim() : string.Empty;
    }

    private void OnRefreshDatasetsRequested(object? sender, RefreshDatasetsRequestedEventArgs e)
    {
        OnPropertyChanged(nameof(AvailableDatasets));
        RefreshVersionList();
    }

    /// <summary>
    /// Preselects a dataset and version for captioning.
    /// Called when navigating from Gallery "Send to" after dataset creation.
    /// </summary>
    public void PreselectDataset(DatasetCardViewModel dataset, int version)
    {
        var matchingDataset = AvailableDatasets.FirstOrDefault(d =>
            string.Equals(d.FolderPath, dataset.FolderPath, StringComparison.OrdinalIgnoreCase));

        SelectedDataset = matchingDataset ?? dataset;
        SelectedDatasetVersion = AvailableDatasetVersions.FirstOrDefault(v => v.Version == version)
                                 ?? AvailableDatasetVersions.FirstOrDefault();

        CurrentProcessingStatus = $"Dataset '{dataset.Name}' V{version} loaded for captioning";
    }

    /// <summary>
    /// Loads a single image for captioning. Can be called from drag-drop or external navigation.
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
    /// Loads multiple images as a temporary batch for captioning.
    /// Injects a "Gallery Selection" dataset into the combo box.
    /// </summary>
    public void LoadTemporaryImages(IReadOnlyList<string> imagePaths)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);

        var validPaths = imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (validPaths.Count == 0) return;

        _tempImagePaths = validPaths;
        SingleImagePath = null;
        IsSingleImageMode = false;

        _tempDataset = new DatasetCardViewModel
        {
            Name = "Gallery Selection",
            FolderPath = "TEMP://Captioning",
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = validPaths.Count,
            TotalImageCountAllVersions = validPaths.Count,
            IsTemporary = true
        };

        var existingTemp = AvailableDatasets.FirstOrDefault(d => d.IsTemporary);
        if (existingTemp is not null)
        {
            AvailableDatasets.Remove(existingTemp);
        }
        AvailableDatasets.Insert(0, _tempDataset);

        SelectedDataset = _tempDataset;
        SelectedDatasetVersion = AvailableDatasetVersions.FirstOrDefault();

        // Auto-enable "New Version" so ConvertTempDatasetToPersistentAsync is triggered
        SaveMode = CaptionSaveMode.NewVersion;

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
    /// Re-reads version folders from disk for the currently selected dataset
    /// and updates the version dropdown, preserving the current selection.
    /// </summary>
    private void RefreshVersionList()
    {
        if (SelectedDataset is null)
        {
            return;
        }

        var previousVersionNum = SelectedDatasetVersion?.Version;

        AvailableDatasetVersions.Clear();
        PopulateVersionItems(SelectedDataset, AvailableDatasetVersions, _tempImagePaths);

        // Keep the previous selection if still valid, otherwise pick the latest
        SelectedDatasetVersion = previousVersionNum.HasValue
            ? AvailableDatasetVersions.FirstOrDefault(v => v.Version == previousVersionNum.Value)
              ?? AvailableDatasetVersions.LastOrDefault()
            : AvailableDatasetVersions.LastOrDefault();
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

    private void RefreshDatasetStats()
    {
        if (IsSingleImageMode || SelectedDataset is null)
        {
            DatasetImageCount = 0;
            DatasetCaptionedCount = 0;
            OnPropertyChanged(nameof(DatasetUncaptionedCount));
            OnPropertyChanged(nameof(HasDatasetStats));
            return;
        }

        // Temp dataset: use stored count, skip disk scan
        if (SelectedDataset.IsTemporary)
        {
            DatasetImageCount = _tempImagePaths?.Count ?? 0;
            DatasetCaptionedCount = 0;
            OnPropertyChanged(nameof(DatasetUncaptionedCount));
            OnPropertyChanged(nameof(HasDatasetStats));
            return;
        }

        var path = SelectedDataset.CurrentVersionFolderPath;
        if (!Directory.Exists(path))
        {
            DatasetImageCount = 0;
            DatasetCaptionedCount = 0;
            OnPropertyChanged(nameof(DatasetUncaptionedCount));
            OnPropertyChanged(nameof(HasDatasetStats));
            return;
        }

        string[] extensions = [".jpg", ".jpeg", ".png", ".webp"];
        var imageFiles = Directory.EnumerateFiles(path)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        var captionedCount = imageFiles
            .Count(f => File.Exists(Path.Combine(
                Path.GetDirectoryName(f) ?? path,
                Path.GetFileNameWithoutExtension(f) + ".txt")));

        DatasetImageCount = imageFiles.Count;
        DatasetCaptionedCount = captionedCount;
        OnPropertyChanged(nameof(DatasetUncaptionedCount));
        OnPropertyChanged(nameof(HasDatasetStats));
    }

    private void ToggleHistoryItem(CaptionHistoryItemViewModel? item)
    {
        if (item is null) return;

        item.IsExpanded = !item.IsExpanded;
        SelectedHistoryItem = item.IsExpanded ? item : null;
    }

    private static Bitmap? LoadPreviewBitmap(string? path, int width = 600)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    private void ClearHistory()
    {
        foreach (var item in CaptionHistory)
            item.Dispose();
        CaptionHistory.Clear();
    }

    private void DisposePreviewBitmaps()
    {
        _currentImagePreview?.Dispose();
        _currentImagePreview = null;
        _selectedGalleryPreview?.Dispose();
        _selectedGalleryPreview = null;
        ClearHistory();
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this ViewModel.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _eventAggregator.RefreshDatasetsRequested -= OnRefreshDatasetsRequested;
            ClearGallerySelection();
            DisposePreviewBitmaps();
        }

        _disposed = true;
    }

    #endregion
}
