using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

public partial class CaptioningViewModel : ObservableObject
{
    private readonly ICaptioningService _captioningService;
    private readonly IDialogService _dialogService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    
    // Model Selection
    private CaptioningModelType _selectedModelType = CaptioningModelType.LLaVA_v1_6_34B;
    
    // Model Status
    private CaptioningModelStatus _llavaStatus;
    private CaptioningModelStatus _qwenStatus;
    private double _llavaDownloadProgress;
    private double _qwenDownloadProgress;
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
    private bool _isSingleImageMode; // Toggle between Dataset and Single Image
    
    // Processing
    private bool _isProcessing;
    private double _totalProgress;
    private string _currentProcessingStatus = "Ready";

    public CaptioningViewModel(
        ICaptioningService captioningService,
        IDialogService dialogService,
        IEnumerable<DatasetCardViewModel> availableDatasets,
        IDatasetEventAggregator? eventAggregator = null,
        DatasetCardViewModel? initialDataset = null,
        int? initialVersion = null)
    {
        _captioningService = captioningService;
        _dialogService = dialogService;
        _eventAggregator = eventAggregator;
        
        AvailableDatasets = new ObservableCollection<DatasetCardViewModel>(availableDatasets ?? []);
        AvailableDatasetVersions = new ObservableCollection<int>();
        AvailableModels = Enum.GetValues<CaptioningModelType>();
        
        DownloadModelCommand = new AsyncRelayCommand<CaptioningModelType>(DownloadModelAsync, CanDownloadModel);
        GenerateCommand = new AsyncRelayCommand(GenerateCaptionsAsync, CanGenerate);
        SelectSingleImageCommand = new AsyncRelayCommand(SelectSingleImageAsync);
        ClearSingleImageCommand = new RelayCommand(() => SingleImagePath = null);
        
        RefreshModelStatuses();

        if (initialDataset != null)
        {
            try 
            {
                SelectedDataset = initialDataset;
                if (initialVersion.HasValue && AvailableDatasetVersions.Contains(initialVersion.Value))
                {
                    SelectedDatasetVersion = initialVersion.Value;
                }
            }
            catch
            {
                // Fallback if setting initial dataset fails
                SelectedDataset = null;
            }
        }
    }
    
    public IReadOnlyList<CaptioningModelType> AvailableModels { get; }
    public ObservableCollection<DatasetCardViewModel> AvailableDatasets { get; }
    public ObservableCollection<int> AvailableDatasetVersions { get; }

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

    // Status Properties
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
                RefreshGlobalModelStatus();
            }
        }
    }

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

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // Config Properties
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

    // Input Properties
    public DatasetCardViewModel? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (SetProperty(ref _selectedDataset, value))
            {
                AvailableDatasetVersions.Clear();
                if (value != null)
                {
                    IsSingleImageMode = false;
                    
                    // Populate versions
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
                    
                    // Default to current version
                    SelectedDatasetVersion = value.CurrentVersion;
                }
                
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int? SelectedDatasetVersion
    {
        get => _selectedDatasetVersion;
        set
        {
            if (SetProperty(ref _selectedDatasetVersion, value))
            {
                if (SelectedDataset != null && value.HasValue && SelectedDataset.CurrentVersion != value.Value)
                {
                    SelectedDataset.CurrentVersion = value.Value;
                }
            }
        }
    }

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

    public string? SingleImageName => Path.GetFileName(SingleImagePath);

    public bool IsSingleImageMode
    {
        get => _isSingleImageMode;
        set
        {
            if (SetProperty(ref _isSingleImageMode, value))
            {
                // Clear the other selection to avoid confusion
                if (value) SelectedDataset = null;
                else SingleImagePath = null;
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // Computed Status Helpers
    public bool IsLlavaReady => LlavaStatus == CaptioningModelStatus.Ready || LlavaStatus == CaptioningModelStatus.Loaded;
    public bool IsLlavaMissing => LlavaStatus == CaptioningModelStatus.NotDownloaded || LlavaStatus == CaptioningModelStatus.Corrupted;
    public bool IsLlavaDownloading => LlavaStatus == CaptioningModelStatus.Downloading;

    public bool IsQwenReady => QwenStatus == CaptioningModelStatus.Ready || QwenStatus == CaptioningModelStatus.Loaded;
    public bool IsQwenMissing => QwenStatus == CaptioningModelStatus.NotDownloaded || QwenStatus == CaptioningModelStatus.Corrupted;
    public bool IsQwenDownloading => QwenStatus == CaptioningModelStatus.Downloading;

    public bool IsModelReady => SelectedModelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => IsLlavaReady,
        CaptioningModelType.Qwen2_5_VL_7B => IsQwenReady,
        _ => false
    };

    public bool IsModelMissing => !IsModelReady;

    // Processing Status
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

    public double TotalProgress
    {
        get => _totalProgress;
        private set => SetProperty(ref _totalProgress, value);
    }

    public string CurrentProcessingStatus
    {
        get => _currentProcessingStatus;
        private set => SetProperty(ref _currentProcessingStatus, value);
    }

    // Commands
    public IAsyncRelayCommand<CaptioningModelType> DownloadModelCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IAsyncRelayCommand SelectSingleImageCommand { get; }
    public IRelayCommand ClearSingleImageCommand { get; }

    private void RefreshModelStatuses()
    {
        try
        {
            var llavaInfo = _captioningService.GetModelInfo(CaptioningModelType.LLaVA_v1_6_34B);
            LlavaStatus = llavaInfo?.Status ?? CaptioningModelStatus.NotDownloaded;

            var qwenInfo = _captioningService.GetModelInfo(CaptioningModelType.Qwen2_5_VL_7B);
            QwenStatus = qwenInfo?.Status ?? CaptioningModelStatus.NotDownloaded;
        }
        catch
        {
            LlavaStatus = CaptioningModelStatus.NotDownloaded;
            QwenStatus = CaptioningModelStatus.NotDownloaded;
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
        return !IsProcessing && modelType switch
        {
            CaptioningModelType.LLaVA_v1_6_34B => !IsLlavaDownloading,
            CaptioningModelType.Qwen2_5_VL_7B => !IsQwenDownloading,
            _ => false
        };
    }

    private async Task DownloadModelAsync(CaptioningModelType modelType)
    {
        if (IsProcessing) return;

        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (modelType == CaptioningModelType.LLaVA_v1_6_34B)
                    LlavaDownloadProgress = p.Percentage;
                else
                    QwenDownloadProgress = p.Percentage;
                
                StatusMessage = $"Downloading {modelType}: {p.Status}";
            });

            // Update status to downloading immediately for UI feedback
            if (modelType == CaptioningModelType.LLaVA_v1_6_34B) LlavaStatus = CaptioningModelStatus.Downloading;
            else QwenStatus = CaptioningModelStatus.Downloading;

            var success = await _captioningService.DownloadModelAsync(modelType, progress);
            
            StatusMessage = success ? $"{modelType} downloaded successfully" : $"Failed to download {modelType}";
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
        return !IsProcessing 
            && IsModelReady 
            && (SelectedDataset != null || (!string.IsNullOrEmpty(SingleImagePath) && File.Exists(SingleImagePath)));
    }

    private async Task SelectSingleImageAsync()
    {
        var result = await _dialogService.ShowOpenFileDialogAsync("Select Image", null);
        if (!string.IsNullOrEmpty(result))
        {
            SingleImagePath = result;
        }
    }

    private async Task GenerateCaptionsAsync()
    {
        if (!CanGenerate()) return;

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
            if (validationErrors.Any())
            {
                StatusMessage = $"Validation failed: {string.Join(", ", validationErrors)}";
                return;
            }

            var progress = new Progress<CaptioningProgress>(p =>
            {
                TotalProgress = p.Percentage;
                CurrentProcessingStatus = p.Status;
                
                if (p.LastResult != null && p.LastResult.Success && _eventAggregator != null)
                {
                    // Notify that an image was saved/updated
                    // We need to map CaptioningResult to an event
                    // Since captioning updates the .txt file, conceptually it's an image metadata change or just a file change.
                    // DatasetManagementViewModel listens to ImageSaved, but that implies the *image* file.
                    // Captions are usually auto-detected. 
                    // Let's assume re-opening the dataset or refreshing handles it.
                    // But if we are in Single Image Mode, the user might want immediate feedback.
                }
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
            return new[] { SingleImagePath };
        }

        if (SelectedDataset != null)
        {
            // We need to list files from the dataset
            var path = SelectedDataset.CurrentVersionFolderPath;
            if (Directory.Exists(path))
            {
                // Basic image extensions filter
                var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
                return Directory.EnumerateFiles(path)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            }
        }

        return Enumerable.Empty<string>();
    }
}
