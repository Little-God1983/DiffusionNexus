using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Captioning tab in the LoRA Dataset Helper.
/// Manages model selection/download, dataset input, captioning settings, and batch caption generation.
/// </summary>
public partial class CaptioningTabViewModel : ViewModelBase, IDialogServiceAware, IDisposable
{
    private readonly ICaptioningService? _captioningService;
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private bool _disposed;

    // Model Selection
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

    // Input Selection
    private DatasetCardViewModel? _selectedDataset;
    private int? _selectedDatasetVersion;
    private string? _singleImagePath;
    private bool _isSingleImageMode;

    // Processing
    private bool _isProcessing;
    private double _totalProgress;
    private string _currentProcessingStatus = "Ready";

    /// <summary>
    /// Creates a new instance of CaptioningTabViewModel.
    /// </summary>
    public CaptioningTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        ICaptioningService? captioningService = null)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _captioningService = captioningService;

        AvailableDatasetVersions = [];
        AvailableModels = Enum.GetValues<CaptioningModelType>();

        DownloadModelCommand = new AsyncRelayCommand<CaptioningModelType>(DownloadModelAsync, CanDownloadModel);
        GenerateCommand = new AsyncRelayCommand(GenerateCaptionsAsync, CanGenerate);
        SelectSingleImageCommand = new AsyncRelayCommand(SelectSingleImageAsync);
        ClearSingleImageCommand = new RelayCommand(() => SingleImagePath = null);

        RefreshModelStatuses();

        _eventAggregator.RefreshDatasetsRequested += OnRefreshDatasetsRequested;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public CaptioningTabViewModel() : this(null!, null!, null)
    {
    }

    #region Properties

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Whether the captioning service is available.
    /// </summary>
    public bool IsServiceAvailable => _captioningService is not null;

    /// <summary>
    /// Whether the native LLama library loaded successfully.
    /// </summary>
    public bool IsNativeLibraryLoaded => _captioningService?.IsNativeLibraryLoaded ?? false;

    /// <summary>
    /// Available captioning model types.
    /// </summary>
    public IReadOnlyList<CaptioningModelType> AvailableModels { get; }

    /// <summary>
    /// Available datasets from shared state.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> AvailableDatasets => _state.Datasets;

    /// <summary>
    /// Available versions for the selected dataset.
    /// </summary>
    public ObservableCollection<int> AvailableDatasetVersions { get; }

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
    /// Whether the currently selected model is ready for inference.
    /// </summary>
    public bool IsModelReady => SelectedModelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => IsLlavaReady,
        CaptioningModelType.Qwen2_5_VL_7B => IsQwenReady,
        CaptioningModelType.Qwen3_VL_8B => IsQwen3Ready,
        _ => false
    };

    /// <summary>
    /// Whether the currently selected model is not ready.
    /// </summary>
    public bool IsModelMissing => !IsModelReady;

    #endregion

    #region Config Properties

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

                    if (value.IsVersionedStructure)
                    {
                        foreach (var v in value.GetAllVersionNumbers())
                        {
                            AvailableDatasetVersions.Add(v);
                        }
                    }
                    else
                    {
                        AvailableDatasetVersions.Add(1);
                    }

                    SelectedDatasetVersion = value.CurrentVersion;
                }

                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The selected dataset version to caption.
    /// </summary>
    public int? SelectedDatasetVersion
    {
        get => _selectedDatasetVersion;
        set
        {
            if (SetProperty(ref _selectedDatasetVersion, value))
            {
                if (SelectedDataset is not null && value.HasValue && SelectedDataset.CurrentVersion != value.Value)
                {
                    SelectedDataset.CurrentVersion = value.Value;
                }
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
            }
        }
    }

    /// <summary>
    /// Display name for the selected single image.
    /// </summary>
    public string? SingleImageName => Path.GetFileName(SingleImagePath);

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

    #endregion

    #region Private Methods

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
        return _captioningService is not null
            && _captioningService.IsNativeLibraryLoaded
            && !IsProcessing
            && IsModelReady
            && (SelectedDataset is not null || (!string.IsNullOrEmpty(SingleImagePath) && File.Exists(SingleImagePath)));
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
        if (_captioningService is null || !CanGenerate()) return;

        IsProcessing = true;
        TotalProgress = 0;
        CurrentProcessingStatus = "Initializing...";

        try
        {
            var blackList = BlacklistedWords?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .ToList();

            var config = new CaptioningJobConfig(
                ImagePaths: GetImagePaths(),
                SelectedModel: SelectedModelType,
                SystemPrompt: SystemPrompt,
                TriggerWord: TriggerWord,
                BlacklistedWords: blackList,
                DatasetPath: SelectedDataset?.CurrentVersionFolderPath,
                OverrideExisting: OverrideExisting,
                Temperature: Temperature
            );

            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                StatusMessage = $"Validation failed: {string.Join(", ", validationErrors)}";
                return;
            }

            var progress = new Progress<CaptioningProgress>(p =>
            {
                TotalProgress = p.Percentage;
                CurrentProcessingStatus = p.Status;
            });

            var results = await _captioningService.GenerateCaptionsAsync(config, progress);

            var successCount = results.Count(r => r.Success);
            StatusMessage = $"Completed! {successCount}/{results.Count} images captioned.";
            CurrentProcessingStatus = "Done";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Job failed: {ex.Message}";
            CurrentProcessingStatus = "Failed";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private IEnumerable<string> GetImagePaths()
    {
        if (IsSingleImageMode && !string.IsNullOrEmpty(SingleImagePath))
        {
            return [SingleImagePath];
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

    private void OnRefreshDatasetsRequested(object? sender, RefreshDatasetsRequestedEventArgs e)
    {
        OnPropertyChanged(nameof(AvailableDatasets));
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
        }

        _disposed = true;
    }

    #endregion
}
