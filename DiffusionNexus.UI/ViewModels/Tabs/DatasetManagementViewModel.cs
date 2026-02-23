using System.Collections.ObjectModel;
using System.IO;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Timer = System.Timers.Timer;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Dataset Management tab in the LoRA Dataset Helper.
/// Handles dataset listing, creation, deletion, image management, and selection operations.
/// 
/// <para>
/// <b>Responsibilities:</b>
/// <list type="bullet">
/// <item>Loading and displaying datasets</item>
/// <item>Creating and deleting datasets</item>
/// <item>Opening datasets and displaying images</item>
/// <item>Managing image selection and bulk operations</item>
/// <item>Category and type assignment</item>
/// <item>Version management</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Event Integration:</b>
/// This ViewModel publishes events via <see cref="IDatasetEventAggregator"/> when:
/// <list type="bullet">
/// <item>Datasets are created, deleted, or modified</item>
/// <item>Images are added, deleted, or have their ratings changed</item>
/// <item>Navigation to Image Editor is requested</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Disposal:</b>
/// Implements <see cref="IDisposable"/> to properly unsubscribe from events.
/// </para>
/// </summary>
public partial class DatasetManagementViewModel : ObservableObject, IDialogServiceAware, IThumbnailAware, IDisposable
{
    private readonly IAppSettingsService _settingsService;
    private readonly IDatasetStorageService _datasetStorageService;
    private readonly IDatasetBackupService? _backupService;
    private readonly ICaptioningService? _captioningService; // Made optional to avoid breaking existing tests/instantiation if any
    private readonly IVideoThumbnailService? _videoThumbnailService;
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private readonly IActivityLogService? _activityLog;
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private bool _disposed;

    private DatasetCategoryViewModel? _selectedCategory;
    private DatasetType? _selectedType;

    // Backup status fields
    private bool _isAutoBackupConfigured;
    private string _backupStatusText = "No backup set up";
    private Timer? _backupCountdownTimer;
    private DateTimeOffset? _nextBackupTime;
    private bool _isBackupInProgress;

    // Filter fields
    private string _filterText = string.Empty;
    private DatasetType? _filterType;
    private bool _showNsfw = true;
    private bool _selectedNsfw;

    // Sub-tab fields
    private VersionSubTab _selectedSubTab = VersionSubTab.Training;
    private IDialogService? _dialogService;

    #region IThumbnailAware

    /// <inheritdoc />
    public ThumbnailOwnerToken OwnerToken { get; } = new("DatasetManagement");

    /// <inheritdoc />
    public void OnThumbnailActivated()
    {
        _thumbnailOrchestrator?.SetActiveOwner(OwnerToken);
    }

    /// <inheritdoc />
    public void OnThumbnailDeactivated()
    {
        _thumbnailOrchestrator?.CancelRequests(OwnerToken);
    }

    #endregion

    /// <summary>
    /// Gets or sets the currently selected sub-tab within the version detail view.
    /// </summary>
    public VersionSubTab SelectedSubTab
    {
        get => _selectedSubTab;
        set => SetProperty(ref _selectedSubTab, value);
    }

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// Automatically forwards to sub-tab ViewModels.
    /// </summary>
    public IDialogService? DialogService
    {
        get => _dialogService;
        set
        {
            _dialogService = value;
            // Forward DialogService to sub-tab ViewModels
            EpochsTab.DialogService = value;
            NotesTab.DialogService = value;
            PresentationTab.DialogService = value;
            CaptioningTab.DialogService = value;
        }
    }

    /// <summary>
    /// ViewModel for the Epochs sub-tab.
    /// </summary>
    public EpochsTabViewModel EpochsTab { get; }

    /// <summary>
    /// ViewModel for the Notes sub-tab.
    /// </summary>
    public NotesTabViewModel NotesTab { get; }

    /// <summary>
    /// ViewModel for the Presentation sub-tab.
    /// </summary>
    public PresentationTabViewModel PresentationTab { get; }

    /// <summary>
    /// ViewModel for the Captioning sub-tab.
    /// </summary>
    public CaptioningTabViewModel CaptioningTab { get; }

    #region Observable Properties (Delegated to State)

    /// <summary>
    /// Indicates whether the dataset storage path is configured.
    /// </summary>
    public bool IsStorageConfigured => _state.IsStorageConfigured;

    /// <summary>
    /// Indicates whether storage is configured but no datasets exist yet.
    /// </summary>
    public bool IsStorageConfiguredButEmpty => IsStorageConfigured && !IsViewingDataset && FilteredGroupedDatasets.Count == 0;

    /// <summary>
    /// Indicates whether there are datasets to display in the overview.
    /// </summary>
    public bool HasDatasetsToShow => IsStorageConfigured && !IsViewingDataset && FilteredGroupedDatasets.Count > 0;

    /// <summary>
    /// Whether we are currently viewing a dataset's contents (vs overview).
    /// </summary>
    public bool IsViewingDataset => _state.IsViewingDataset;

    /// <summary>
    /// The currently selected/active dataset.
    /// </summary>
    public DatasetCardViewModel? ActiveDataset => _state.ActiveDataset;

    /// <summary>
    /// Safe accessor for the active dataset's name.
    /// </summary> 
    public string DatasetName => ActiveDataset?.Name ?? string.Empty;

    /// <summary>
    /// Safe accessor for the active dataset's description.
    /// </summary>
    public string DatasetDescription
    {
        get => ActiveDataset?.Description ?? string.Empty;
        set
        {
            if (ActiveDataset is not null && ActiveDataset.Description != value)
            {
                ActiveDataset.Description = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Safe accessor for checking if the active dataset has multiple versions.
    /// </summary>
    public bool DatasetHasMultipleVersions => ActiveDataset?.HasMultipleVersions ?? false;

    /// <summary>
    /// Safe accessor for checking if the active dataset supports version increment.
    /// </summary>
    public bool DatasetCanIncrementVersion => ActiveDataset?.CanIncrementVersion ?? false;

    /// <summary>
    /// Whether images are currently loading.
    /// </summary>
    public bool IsLoading
    {
        get => _state.IsLoading;
        set => _state.IsLoading = value;
    }

    /// <summary>
    /// Whether there are unsaved caption changes.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _state.HasUnsavedChanges;
        set => _state.HasUnsavedChanges = value;
    }

    /// <summary>
    /// Whether to flatten version folders in the overview.
    /// </summary>
    public bool FlattenVersions
    {
        get => _state.FlattenVersions;
        set
        {
            if (_state.FlattenVersions != value)
            {
                _state.FlattenVersions = value;
                OnPropertyChanged();
                _ = LoadDatasetsAsync();
            }
        }
    }

    /// <summary>
    /// Indicates whether a file dialog is currently open.
    /// </summary>
    public bool IsFileDialogOpen
    {
        get => _state.IsFileDialogOpen;
        set => _state.IsFileDialogOpen = value;
    }

    /// <summary>
    /// Currently selected version number for the active dataset.
    /// </summary>
    public int SelectedVersion
    {
        get => ActiveDataset?.CurrentVersion ?? 1;
        set
        {
            if (ActiveDataset is not null && ActiveDataset.CurrentVersion != value)
            {
                _ = SwitchVersionAsync(value);
            }
        }
    }

    /// <summary>
    /// Whether the active dataset has no media files.
    /// </summary>
    public bool HasNoImages => _state.HasNoImages;

    /// <summary>
    /// Number of currently selected images.
    /// </summary>
    public int SelectionCount => _state.SelectionCount;

    /// <summary>
    /// Whether any images are currently selected.
    /// </summary>
    public bool HasSelection => _state.HasSelection;

    /// <summary>
    /// Text describing the current selection.
    /// </summary]
    public string SelectionText => SelectionCount == 1 ? "1 selected" : $"{SelectionCount} selected";

    /// <summary>
    /// Selected category for the active dataset.
    /// </summary>
    public DatasetCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value) && ActiveDataset is not null)
            {
                ActiveDataset.CategoryId = value?.Id;
                ActiveDataset.CategoryOrder = value?.Order;
                ActiveDataset.CategoryName = value?.Name;
                ActiveDataset.SaveMetadata();
                _state.StatusMessage = value is not null
                    ? $"Category set to '{value.Name}'"
                    : "Category cleared";

                _eventAggregator.PublishDatasetMetadataChanged(new DatasetMetadataChangedEventArgs
                {
                    Dataset = ActiveDataset,
                    ChangeType = DatasetMetadataChangeType.Category
                });
            }
        }
    }

    /// <summary>
    /// Selected type for the active dataset.
    /// </summary>
    public DatasetType? SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value) && ActiveDataset is not null)
            {
                ActiveDataset.Type = value;
                ActiveDataset.SaveMetadata();
                _state.StatusMessage = value is not null
                    ? $"Type set to '{value.Value.GetDisplayName()}'"
                    : "Type cleared";

                _eventAggregator.PublishDatasetMetadataChanged(new DatasetMetadataChangedEventArgs
                {
                    Dataset = ActiveDataset,
                    ChangeType = DatasetMetadataChangeType.Type
                });
            }
        }
    }

    /// <summary>
    /// Whether the active dataset's current version is marked as NSFW.
    /// </summary>
    public bool SelectedNsfw
    {
        get => _selectedNsfw;
        set
        {
            if (SetProperty(ref _selectedNsfw, value) && ActiveDataset is not null)
            {
                ActiveDataset.IsNsfw = value;
                ActiveDataset.SaveMetadata();
                _state.StatusMessage = value
                    ? "Marked as NSFW"
                    : "NSFW marking removed";

                _eventAggregator.PublishDatasetMetadataChanged(new DatasetMetadataChangedEventArgs
                {
                    Dataset = ActiveDataset,
                    ChangeType = DatasetMetadataChangeType.Nsfw
                });
            }
        }
    }

    /// <summary>
    /// Status message to display to the user.
    /// </summary>
    public string? StatusMessage
    {
        get => _state.StatusMessage;
        set => _state.StatusMessage = value;
    }

    /// <summary>
    /// Whether automatic backup is configured.
    /// </summary>
    public bool IsAutoBackupConfigured
    {
        get => _isAutoBackupConfigured;
        private set => SetProperty(ref _isAutoBackupConfigured, value);
    }

    /// <summary>
    /// Whether a backup is currently in progress.
    /// </summary>
    public bool IsBackupInProgress
    {
        get => _isBackupInProgress;
        private set
        {
            if (SetProperty(ref _isBackupInProgress, value))
            {
                OnPropertyChanged(nameof(BackupButtonContent));
            }
        }
    }

    /// <summary>
    /// Content for the backup button - shows hourglass when running.
    /// </summary>
    public string BackupButtonContent => _isBackupInProgress ? "? Backup Running..." : BackupStatusText;

    /// <summary>
    /// Text to display on the backup status button.
    /// </summary>
    public string BackupStatusText
    {
        get => _backupStatusText;
        private set
        {
            if (SetProperty(ref _backupStatusText, value))
            {
                OnPropertyChanged(nameof(BackupButtonContent));
            }
        }
    }

    #endregion

    #region Filter Properties

    /// <summary>
    /// Text to filter datasets by name or description.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// Type to filter datasets by. Null means show all types.
    /// </summary>
    public DatasetType? FilterType
    {
        get => _filterType;
        set
        {
            if (SetProperty(ref _filterType, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// Whether to show NSFW datasets in the overview. Default is false (hide NSFW).
    /// </summary>
    public bool ShowNsfw
    {
        get => _showNsfw;
        set
        {
            if (SetProperty(ref _showNsfw, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// Whether any filter is currently active.
    /// </summary>
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_filterText) || _filterType.HasValue || !_showNsfw;

    /// <summary>
    /// Number of datasets hidden by filters (search, type, NSFW combined).
    /// </summary>
    public int HiddenCount { get; private set; }

    /// <summary>
    /// Whether any datasets are hidden by filters.
    /// </summary>
    public bool HasHidden => HiddenCount > 0;

    /// <summary>
    /// Text describing how many datasets are hidden by filters.
    /// </summary>
    public string HiddenText => HiddenCount == 1 
        ? "1 dataset hidden" 
        : $"{HiddenCount} datasets hidden";

    /// <summary>
    /// Filtered collection of datasets grouped by category.
    /// </summary>
    public ObservableCollection<DatasetGroupViewModel> FilteredGroupedDatasets { get; } = [];

    #endregion

    #region Collections (Delegated to State)

    /// <summary>
    /// Collection of dataset cards.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> Datasets => _state.Datasets;

    /// <summary>
    /// Collection of datasets grouped by category.
    /// </summary>
    public ObservableCollection<DatasetGroupViewModel> GroupedDatasets => _state.GroupedDatasets;

    /// <summary>
    /// Collection of images in the active dataset.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> DatasetImages => _state.DatasetImages;

    /// <summary>
    /// Available dataset categories.
    /// </summary>
    public ObservableCollection<DatasetCategoryViewModel> AvailableCategories => _state.AvailableCategories;

    /// <summary>
    /// Available dataset types for assigning to a dataset.
    /// </summary>
    public IReadOnlyList<DatasetType> AvailableTypes { get; } = DatasetTypeExtensions.GetAll();

    /// <summary>
    /// Available dataset types for filtering, including "All Types" option (null).
    /// </summary>
    public IReadOnlyList<DatasetType?> AvailableFilterTypes { get; } = [null, .. DatasetTypeExtensions.GetAll().Cast<DatasetType?>()];

    /// <summary>
    /// Available versions for the active dataset.
    /// </summary>
    public ObservableCollection<int> AvailableVersions => _state.AvailableVersions;

    #endregion

    #region Commands

    public IAsyncRelayCommand CheckStorageConfigurationCommand { get; }
    public IAsyncRelayCommand LoadDatasetsCommand { get; }
    public IAsyncRelayCommand<DatasetCardViewModel?> OpenDatasetCommand { get; }
    public IAsyncRelayCommand GoToOverviewCommand { get; }
    public IAsyncRelayCommand CreateDatasetCommand { get; }
    public IAsyncRelayCommand AddImagesCommand { get; }
    public IAsyncRelayCommand IncrementVersionCommand { get; }
    public IRelayCommand SaveAllCaptionsCommand { get; }
    public IAsyncRelayCommand<DatasetCardViewModel?> DeleteDatasetCommand { get; }
    public IRelayCommand OpenContainingFolderCommand { get; }
    public IAsyncRelayCommand OpenViewerCommand { get; }
    public IRelayCommand<DatasetImageViewModel?> SendToImageEditCommand { get; }
    public IRelayCommand SendToBatchCropScaleCommand { get; }
    public IAsyncRelayCommand ExportDatasetCommand { get; }
    public IAsyncRelayCommand<DatasetImageViewModel?> OpenImageViewerCommand { get; }
    public IRelayCommand GoToBackupSettingsCommand { get; }
    public IRelayCommand ClearFiltersCommand { get; }
    public IAsyncRelayCommand BackupNowCommand { get; }
    public IAsyncRelayCommand<DatasetImageViewModel?> DeleteImageCommand { get; }

    // Selection commands
    public IRelayCommand<DatasetImageViewModel?> ToggleSelectionCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand SelectApprovedCommand { get; }
    public IRelayCommand SelectRejectedCommand { get; }
    public IRelayCommand ApproveSelectedCommand { get; }
    public IRelayCommand RejectSelectedCommand { get; }
    public IRelayCommand ClearRatingSelectedCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }
    public IAsyncRelayCommand OpenCaptioningToolCommand { get; } // New Command

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new instance of DatasetManagementViewModel.
    /// </summary>
    public DatasetManagementViewModel(
        IAppSettingsService settingsService,
        IDatasetStorageService datasetStorageService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        ICaptioningService? captioningService = null, // Added as optional
        IVideoThumbnailService? videoThumbnailService = null,
        IDatasetBackupService? backupService = null,
        IActivityLogService? activityLog = null,
        IThumbnailOrchestrator? thumbnailOrchestrator = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _datasetStorageService = datasetStorageService ?? throw new ArgumentNullException(nameof(datasetStorageService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _captioningService = captioningService;
        _videoThumbnailService = videoThumbnailService;
        _backupService = backupService;
        _activityLog = activityLog;
        _thumbnailOrchestrator = thumbnailOrchestrator;

        // Initialize sub-tab ViewModels
        EpochsTab = new EpochsTabViewModel(_eventAggregator);
        NotesTab = new NotesTabViewModel(_eventAggregator);
        PresentationTab = new PresentationTabViewModel(_eventAggregator);
        CaptioningTab = new CaptioningTabViewModel(_eventAggregator, _state, _captioningService);

        // Subscribe to state changes
        _state.StateChanged += OnStateChanged;

        // Subscribe to events from other components
        _eventAggregator.ImageSaved += OnImageSaved;
        _eventAggregator.ImageRatingChanged += OnImageRatingChanged;
        _eventAggregator.SettingsSaved += OnSettingsSaved;
        _eventAggregator.ImageSelectionChanged += OnImageSelectionChanged;

        // Initialize commands
        CheckStorageConfigurationCommand = new AsyncRelayCommand(CheckStorageConfigurationAsync);
        LoadDatasetsCommand = new AsyncRelayCommand(LoadDatasetsAsync);
        OpenDatasetCommand = new AsyncRelayCommand<DatasetCardViewModel?>(OpenDatasetAsync);
        GoToOverviewCommand = new AsyncRelayCommand(GoToOverviewAsync);
        CreateDatasetCommand = new AsyncRelayCommand(CreateDatasetAsync);
        AddImagesCommand = new AsyncRelayCommand(AddImagesAsync);
        IncrementVersionCommand = new AsyncRelayCommand(IncrementVersionAsync);
        SaveAllCaptionsCommand = new RelayCommand(SaveAllCaptions);
        DeleteDatasetCommand = new AsyncRelayCommand<DatasetCardViewModel?>(DeleteDatasetAsync);
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder);
        OpenViewerCommand = new AsyncRelayCommand(OpenViewerAsync, () => !HasNoImages);
        SendToImageEditCommand = new RelayCommand<DatasetImageViewModel?>(SendToImageEdit);
        SendToBatchCropScaleCommand = new RelayCommand(SendToBatchCropScale);
        ExportDatasetCommand = new AsyncRelayCommand(ExportDatasetAsync);
        OpenImageViewerCommand = new AsyncRelayCommand<DatasetImageViewModel?>(OpenImageViewerAsync);
        GoToBackupSettingsCommand = new RelayCommand(GoToBackupSettings);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, () => IsAutoBackupConfigured && !_isBackupInProgress);
        DeleteImageCommand = new AsyncRelayCommand<DatasetImageViewModel?>(DeleteImageAsync);

        // Selection commands
        ToggleSelectionCommand = new RelayCommand<DatasetImageViewModel?>(ToggleSelection);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        ApproveSelectedCommand = new RelayCommand(ApproveSelected);
        RejectSelectedCommand = new RelayCommand(RejectSelected);
        ClearRatingSelectedCommand = new RelayCommand(ClearRatingSelected);
        SelectApprovedCommand = new RelayCommand(SelectApproved);
        SelectRejectedCommand = new RelayCommand(SelectRejected);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        
        OpenCaptioningToolCommand = new AsyncRelayCommand(OpenCaptioningToolAsync);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public DatasetManagementViewModel() : this(null!, null!, null!, null!, null, null, null, null, null)
    {
    }

    #endregion

    #region Event Handlers

    private void OnStateChanged(object? sender, DatasetStateChangedEventArgs e)
    {
        // Forward property changes to UI
        switch (e.PropertyName)
        {
            case nameof(IDatasetState.IsStorageConfigured):
                OnPropertyChanged(nameof(IsStorageConfigured));
                OnPropertyChanged(nameof(IsStorageConfiguredButEmpty));
                OnPropertyChanged(nameof(HasDatasetsToShow));
                break;
            case nameof(IDatasetState.IsViewingDataset):
                OnPropertyChanged(nameof(IsViewingDataset));
                OnPropertyChanged(nameof(HasNoImages));
                // Also notify derived properties that depend on IsViewingDataset
                OnPropertyChanged(nameof(IsStorageConfiguredButEmpty));
                OnPropertyChanged(nameof(HasDatasetsToShow));
                break;
            case nameof(IDatasetState.ActiveDataset):
                OnPropertyChanged(nameof(ActiveDataset));
                OnPropertyChanged(nameof(DatasetName));
                OnPropertyChanged(nameof(DatasetDescription));
                OnPropertyChanged(nameof(DatasetHasMultipleVersions));
                OnPropertyChanged(nameof(DatasetCanIncrementVersion));
                OnPropertyChanged(nameof(SelectedVersion));
                OnPropertyChanged(nameof(SelectedNsfw));
                break;
            case nameof(IDatasetState.IsLoading):
                OnPropertyChanged(nameof(IsLoading));
                break;
            case nameof(IDatasetState.HasUnsavedChanges):
                OnPropertyChanged(nameof(HasUnsavedChanges));
                break;
            case nameof(IDatasetState.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                break;
            case nameof(IDatasetState.SelectionCount):
                OnPropertyChanged(nameof(SelectionCount));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionText));
                break;
            case nameof(IDatasetState.HasNoImages):
                OnPropertyChanged(nameof(HasNoImages));
                break;
        }
    }

    /// <summary>
    /// Handles selection changes from individual image checkboxes.
    /// </summary>
    private void OnImageSelectionChanged(object? sender, ImageSelectionChangedEventArgs e)
    {
        _state.UpdateSelectionCount();
    }

    private async void OnImageSaved(object? sender, ImageSavedEventArgs e)
    {
        FileLogger.LogEntry($"ImagePath={e.ImagePath ?? "(null)"}");
        FileLogger.Log($"ActiveDataset={ActiveDataset?.Name ?? "(null)"}, IsViewingDataset={IsViewingDataset}");
        
        try
        {
            // Refresh the current dataset if we're viewing it
            if (ActiveDataset is not null && IsViewingDataset)
            {
                FileLogger.Log("Refreshing active dataset...");
                ActiveDataset.RefreshImageInfo();
                await RefreshActiveDatasetAsync();
                FileLogger.Log("Refresh completed");
            }
            else
            {
                FileLogger.Log("Not viewing a dataset, skipping refresh");
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception in DatasetManagementViewModel.OnImageSaved", ex);
        }
        
        FileLogger.LogExit();
    }

    private void OnImageRatingChanged(object? sender, ImageRatingChangedEventArgs e)
    {
        // Find the matching image in DatasetImages by file path and sync the rating
        // This handles the case where different ViewModels have separate DatasetImageViewModel instances
        var matchingImage = DatasetImages.FirstOrDefault(img =>
            string.Equals(img.ImagePath, e.Image.ImagePath, StringComparison.OrdinalIgnoreCase));

        if (matchingImage is not null && matchingImage != e.Image)
        {
            // Update the rating on our instance to match - this triggers UI update via PropertyChanged
            matchingImage.RatingStatus = e.NewRating;
        }
    }

    private async void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        // Re-check storage configuration when settings are saved
        // This handles the case where the user configures the Dataset Storage Path in Settings
        await CheckStorageConfigurationAsync();
    }

    #endregion

    #region Command Implementations

    private async Task CheckStorageConfigurationAsync()
    {
        if (_settingsService is null) return;

        var settings = await _settingsService.GetSettingsAsync();
        var isConfigured = !string.IsNullOrWhiteSpace(settings.DatasetStoragePath)
                           && _datasetStorageService.DirectoryExists(settings.DatasetStoragePath);
        _state.SetStorageConfigured(isConfigured);

        // Update backup status
        UpdateBackupStatus(settings);

        // Load categories
        await LoadCategoriesAsync(settings);

        if (isConfigured)
        {
            await LoadDatasetsAsync();
        }
    }

    /// <summary>
    /// Updates the backup status properties based on settings.
    /// </summary>
    private void UpdateBackupStatus(AppSettings settings)
    {
        // Stop existing timer
        StopBackupCountdownTimer();

        var isBackupConfigured = settings.AutoBackupEnabled
            && !string.IsNullOrWhiteSpace(settings.AutoBackupLocation)
            && _datasetStorageService.DirectoryExists(settings.AutoBackupLocation);

        IsAutoBackupConfigured = isBackupConfigured;

        if (isBackupConfigured)
        {
            // Calculate next backup time
            var intervalTicks = TimeSpan.FromDays(settings.AutoBackupIntervalDays).Ticks
                              + TimeSpan.FromHours(settings.AutoBackupIntervalHours).Ticks;
            var interval = TimeSpan.FromTicks(intervalTicks);

            if (interval.TotalMinutes < 1)
            {
                interval = TimeSpan.FromHours(1); // Minimum 1 hour
            }

            var lastBackup = settings.LastBackupAt ?? DateTimeOffset.MinValue;
            _nextBackupTime = lastBackup + interval;

            // If next backup is in the past, it's due now - trigger backup
            if (_nextBackupTime <= DateTimeOffset.UtcNow)
            {
                BackupStatusText = "Backup: Due now";
                _ = ExecuteBackupIfDueAsync();
            }
            else
            {
                UpdateBackupCountdownText();
                StartBackupCountdownTimer();
            }
        }
        else
        {
            _nextBackupTime = null;
            BackupStatusText = "No backup set up";
        }
    }

    /// <summary>
    /// Executes a backup if one is due and not already in progress.
    /// </summary>
    private async Task ExecuteBackupIfDueAsync()
    {
        if (_backupService is null || _isBackupInProgress)
        {
            return;
        }

        var isDue = await _backupService.IsBackupDueAsync();
        if (!isDue)
        {
            return;
        }

        _isBackupInProgress = true;
        BackupStatusText = "Backup: Running...";
        BackupNowCommand.NotifyCanExecuteChanged();

        // Start backup progress tracking in the status bar
        _activityLog?.StartBackupProgress("Backing up datasets");

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    BackupStatusText = $"Backup: {p.ProgressPercent}%";
                    _activityLog?.ReportBackupProgress(p.ProgressPercent, p.Phase);
                });
            });

            // Run backup on a background thread with its own DI scope to avoid
            // concurrent DbContext access (the shared scoped DbContext is not thread-safe).
            var result = await Task.Run(async () =>
            {
                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var scopedBackupService = scope.ServiceProvider.GetRequiredService<IDatasetBackupService>();
                return await scopedBackupService.BackupDatasetsAsync(progress);
            });

            if (result.Success)
            {
                _activityLog?.CompleteBackupProgress(true, $"Backup completed: {result.FilesBackedUp} files");
                StatusMessage = $"Backup completed: {result.FilesBackedUp} files";
                
                // Refresh backup status to show next backup time
                var settings = await _settingsService.GetSettingsAsync();
                UpdateBackupStatus(settings);
            }
            else
            {
                _activityLog?.CompleteBackupProgress(false, $"Backup failed: {result.ErrorMessage}");
                StatusMessage = $"Backup failed: {result.ErrorMessage}";
                BackupStatusText = "Backup: Failed";
            }
        }
        catch (Exception ex)
        {
            _activityLog?.CompleteBackupProgress(false, $"Backup error: {ex.Message}");
            StatusMessage = $"Backup error: {ex.Message}";
            BackupStatusText = "Backup: Error";
        }
        finally
        {
            _isBackupInProgress = false;
            BackupNowCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Manually triggers a backup now.
    /// </summary>
    private async Task BackupNowAsync()
    {
        if (_backupService is null || _isBackupInProgress)
        {
            return;
        }

        _isBackupInProgress = true;
        BackupStatusText = "Backup: Running...";
        BackupNowCommand.NotifyCanExecuteChanged();

        // Start backup progress tracking in the status bar
        _activityLog?.StartBackupProgress("Backing up datasets");

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    BackupStatusText = $"Backup: {p.ProgressPercent}%";
                    _activityLog?.ReportBackupProgress(p.ProgressPercent, p.Phase);
                });
            });

            // Run backup on a background thread with its own DI scope to avoid
            // concurrent DbContext access (the shared scoped DbContext is not thread-safe).
            var result = await Task.Run(async () =>
            {
                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var scopedBackupService = scope.ServiceProvider.GetRequiredService<IDatasetBackupService>();
                return await scopedBackupService.BackupDatasetsAsync(progress);
            });

            if (result.Success)
            {
                _activityLog?.CompleteBackupProgress(true, $"Backup completed: {result.FilesBackedUp} files");
                StatusMessage = $"Backup completed: {result.FilesBackedUp} files";
                
                // Refresh backup status to show next backup time
                var settings = await _settingsService.GetSettingsAsync();
                UpdateBackupStatus(settings);
            }
            else
            {
                _activityLog?.CompleteBackupProgress(false, $"Backup failed: {result.ErrorMessage}");
                StatusMessage = $"Backup failed: {result.ErrorMessage}";
                BackupStatusText = "Backup: Failed";
            }
        }
        catch (Exception ex)
        {
            _activityLog?.CompleteBackupProgress(false, $"Backup error: {ex.Message}");
            StatusMessage = $"Backup error: {ex.Message}";
            BackupStatusText = "Backup: Error";
        }
        finally
        {
            _isBackupInProgress = false;
            BackupNowCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Updates the backup countdown text based on remaining time.
    /// </summary>
    private void UpdateBackupCountdownText()
    {
        if (_nextBackupTime is null)
        {
            BackupStatusText = "No backup set up";
            return;
        }

        var remaining = _nextBackupTime.Value - DateTimeOffset.UtcNow;

        if (remaining.TotalSeconds <= 0)
        {
            BackupStatusText = "Backup: Due now";
            StopBackupCountdownTimer();
            return;
        }

        // Format the remaining time
        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            var hours = remaining.Hours;
            BackupStatusText = hours > 0
                ? $"Backup in {days}d {hours}h"
                : $"Backup in {days}d";
        }
        else if (remaining.TotalHours >= 1)
        {
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            BackupStatusText = minutes > 0
                ? $"Backup in {hours}h {minutes}m"
                : $"Backup in {hours}h";
        }
        else if (remaining.TotalMinutes >= 1)
        {
            var minutes = (int)remaining.TotalMinutes;
            var seconds = remaining.Seconds;
            BackupStatusText = seconds > 0
                ? $"Backup in {minutes}m {seconds}s"
                : $"Backup in {minutes}m";
        }
        else
        {
            var seconds = (int)remaining.TotalSeconds;
            BackupStatusText = $"Backup in {seconds}s";
        }

        // Adjust timer interval based on remaining time for more accurate updates
        AdjustTimerInterval(remaining);
    }

    /// <summary>
    /// Adjusts the backup countdown timer interval based on remaining time.
    /// Updates every second when under 1 minute, every minute otherwise.
    /// </summary>
    private void AdjustTimerInterval(TimeSpan remaining)
    {
        if (_backupCountdownTimer is null) return;

        var desiredInterval = remaining.TotalMinutes < 1 ? 1000 : 60000; // 1 second or 1 minute

        if (Math.Abs(_backupCountdownTimer.Interval - desiredInterval) > 1)
        {
            _backupCountdownTimer.Interval = desiredInterval;
        }
    }

    /// <summary>
    /// Starts the backup countdown timer.
    /// </summary>
    private void StartBackupCountdownTimer()
    {
        // Determine initial interval based on remaining time
        var remaining = _nextBackupTime.HasValue
            ? _nextBackupTime.Value - DateTimeOffset.UtcNow
            : TimeSpan.MaxValue;
        
        var interval = remaining.TotalMinutes < 1 ? 1000 : 60000; // 1 second or 1 minute

        _backupCountdownTimer = new Timer(interval);
        _backupCountdownTimer.Elapsed += OnBackupCountdownTimerElapsed;
        _backupCountdownTimer.AutoReset = true;
        _backupCountdownTimer.Start();
    }

    /// <summary>
    /// Stops the backup countdown timer.
    /// </summary>
    private void StopBackupCountdownTimer()
    {
        if (_backupCountdownTimer is not null)
        {
            _backupCountdownTimer.Stop();
            _backupCountdownTimer.Elapsed -= OnBackupCountdownTimerElapsed;
            _backupCountdownTimer.Dispose();
            _backupCountdownTimer = null;
        }
    }

    /// <summary>
    /// Handles the backup countdown timer tick.
    /// </summary>
    private void OnBackupCountdownTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        // Update on UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateBackupCountdownText();
            
            // Check if backup is now due
            if (_nextBackupTime.HasValue && _nextBackupTime.Value <= DateTimeOffset.UtcNow)
            {
                _ = ExecuteBackupIfDueAsync();
            }
        });
    }

    /// <summary>
    /// Navigates to the Settings page to configure backup.
    /// </summary>
    private void GoToBackupSettings()
    {
        _eventAggregator.PublishNavigateToSettings(new NavigateToSettingsEventArgs());
    }

    private async Task LoadCategoriesAsync(AppSettings? settings = null)
    {
        if (_settingsService is null) return;

        settings ??= await _settingsService.GetSettingsAsync();

        // Always clear before populating to prevent duplicates
        AvailableCategories.Clear();
        
        foreach (var category in settings.DatasetCategories.OrderBy(c => c.Order))
        {
            AvailableCategories.Add(new DatasetCategoryViewModel
            {
                Id = category.Id,
                Order = category.Order,
                Name = category.Name,
                Description = category.Description,
                IsDefault = category.IsDefault
            });
        }
    }

    private async Task LoadDatasetsAsync()
    {
        if (_settingsService is null) return;

        // Prevent concurrent loads
        if (IsLoading) return;

        IsLoading = true;
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath) || !_datasetStorageService.DirectoryExists(settings.DatasetStoragePath))
            {
                _state.SetStorageConfigured(false);
                return;
            }

            _state.SetStorageConfigured(true);
            Datasets.Clear();
            GroupedDatasets.Clear();

            // Build lookup from Order to Category for resolving CategoryId from CategoryOrder
            var categoryByOrder = AvailableCategories.ToDictionary(c => c.Order);

            var folders = _datasetStorageService.GetDirectories(settings.DatasetStoragePath);
            foreach (var folder in folders.OrderBy(f => Path.GetFileName(f)))
            {
                var card = DatasetCardViewModel.FromFolder(folder);
                
                // Resolve CategoryId from CategoryOrder
                if (card.CategoryOrder.HasValue && categoryByOrder.TryGetValue(card.CategoryOrder.Value, out var category))
                {
                    card.CategoryId = category.Id;
                    card.CategoryName = category.Name;
                }
                else
                {
                    card.CategoryId = null;
                    card.CategoryName = null;
                }
                
                Datasets.Add(card);
            }

            // Get valid category IDs
            var validCategoryIds = AvailableCategories.Select(c => c.Id).ToHashSet();

            // Build category groups
            var sortOrder = 0;
            foreach (var category in AvailableCategories)
            {
                var group = DatasetGroupViewModel.FromCategory(category, sortOrder++);
                var categoryDatasets = Datasets.Where(d => d.CategoryId == category.Id);

                foreach (var dataset in categoryDatasets)
                {
                    AddDatasetCardsToGroup(group, dataset);
                }

                if (group.HasDatasets)
                {
                    GroupedDatasets.Add(group);
                }
            }

            // Add uncategorized datasets (including those with invalid/orphaned category orders)
            var uncategorizedDatasets = Datasets
                .Where(d => d.CategoryId is null)
                .ToList();
                
            if (uncategorizedDatasets.Count > 0)
            {
                var uncategorized = DatasetGroupViewModel.CreateUncategorized(sortOrder);
                foreach (var dataset in uncategorizedDatasets)
                {
                    AddDatasetCardsToGroup(uncategorized, dataset);
                }
                GroupedDatasets.Add(uncategorized);
            }

            // Apply filter to populate FilteredGroupedDatasets
            ApplyFilter();

            StatusMessage = Datasets.Count == 0 ? null : $"Found {Datasets.Count} datasets";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading datasets: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void AddDatasetCardsToGroup(DatasetGroupViewModel group, DatasetCardViewModel dataset)
    {
        if (FlattenVersions && dataset.IsVersionedStructure && dataset.TotalVersions > 1)
        {
            foreach (var version in dataset.GetAllVersionNumbers())
            {
                var versionCard = dataset.CreateVersionCard(version);
                group.Datasets.Add(versionCard);
            }
        }
        else
        {
            group.Datasets.Add(dataset);
        }
    }

    private async Task OpenDatasetAsync(DatasetCardViewModel? dataset)
    {
        if (dataset is null) return;

        IsLoading = true;
        try
        {
            _state.SetActiveDataset(dataset);
            
            // Force property change notifications for safe properties since SetActiveDataset might not trigger
            // if the dataset reference is the same but its properties changed (e.g. after version switch)
            OnPropertyChanged(nameof(DatasetName));
            OnPropertyChanged(nameof(DatasetDescription));
            OnPropertyChanged(nameof(DatasetHasMultipleVersions));
            OnPropertyChanged(nameof(DatasetCanIncrementVersion));

            DatasetImages.Clear();

            // Reset to Training tab when opening a dataset
            SelectedSubTab = VersionSubTab.Training;

            // Populate available versions
            AvailableVersions.Clear();
            if (dataset.IsVersionedStructure)
            {
                foreach (var version in dataset.GetAllVersionNumbers())
                {
                    AvailableVersions.Add(version);
                }
            }
            else
            {
                AvailableVersions.Add(1);
            }
            OnPropertyChanged(nameof(SelectedVersion));

            var mediaFolderPath = dataset.CurrentVersionFolderPath;
            // Create the folder structure for new datasets
            _datasetStorageService.CreateDirectory(mediaFolderPath);

            // Initialize sub-tab ViewModels with the current version folder
            EpochsTab.Initialize(dataset.CurrentVersionFolderPath);
            NotesTab.Initialize(dataset.CurrentVersionFolderPath);
            PresentationTab.Initialize(dataset.CurrentVersionFolderPath);

            // Ensure sub-folders exist (Epochs, Notes, Presentation)
            _datasetStorageService.EnsureVersionSubfolders(dataset.CurrentVersionFolderPath);

            // Load media files using the shared MediaFileExtensions utility
            var allFiles = _datasetStorageService.EnumerateFiles(mediaFolderPath).ToList();
            var mediaFiles = allFiles
                .Where(f => MediaFileExtensions.IsDisplayableMediaFile(f))
                .OrderBy(f => f)
                .ToList();

            foreach (var mediaPath in mediaFiles)
            {
                var mediaVm = DatasetImageViewModel.FromFile(mediaPath, _eventAggregator);

                if (mediaVm.IsVideo && _videoThumbnailService is not null)
                {
                    await GenerateVideoThumbnailAsync(mediaVm);
                }

                DatasetImages.Add(mediaVm);
            }

            // Set selected category, type, and NSFW
            _selectedCategory = AvailableCategories.FirstOrDefault(c => c.Id == dataset.CategoryId);
            OnPropertyChanged(nameof(SelectedCategory));

            _selectedType = dataset.Type;
            OnPropertyChanged(nameof(SelectedType));

            _selectedNsfw = dataset.IsNsfw;
            OnPropertyChanged(nameof(SelectedNsfw));

            HasUnsavedChanges = false;

            var imageCount = DatasetImages.Count(m => m.IsImage);
            var videoCount = DatasetImages.Count(m => m.IsVideo);

            if (dataset.HasMultipleVersions)
            {
                StatusMessage = videoCount > 0
                    ? $"Loaded {imageCount} images, {videoCount} videos (Version {dataset.CurrentVersion} of {dataset.TotalVersions})"
                    : $"Loaded {imageCount} images (Version {dataset.CurrentVersion} of {dataset.TotalVersions})";
            }
            else
            {
                StatusMessage = videoCount > 0
                    ? $"Loaded {imageCount} images, {videoCount} videos"
                    : $"Loaded {imageCount} images";
            }

            // Publish event
            _eventAggregator.PublishDatasetImagesLoaded(new DatasetImagesLoadedEventArgs
            {
                Dataset = dataset,
                Images = DatasetImages.ToList()
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading dataset: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Ensures the sub-folders (Epochs, Notes, Presentation) exist within a version folder.
    /// </summary>
    private async Task GenerateVideoThumbnailAsync(DatasetImageViewModel mediaVm)
    {
        if (_videoThumbnailService is null || !mediaVm.IsVideo) return;

        try
        {
            var result = await _videoThumbnailService.GenerateThumbnailAsync(mediaVm.ImagePath);
            if (result.Success && result.ThumbnailPath is not null)
            {
                mediaVm.ThumbnailPath = result.ThumbnailPath;
            }
        }
        catch
        {
            // Ignore thumbnail generation errors
        }
    }

    private async Task GoToOverviewAsync()
    {
        if (HasUnsavedChanges && DialogService is not null)
        {
            var save = await DialogService.ShowConfirmAsync(
                "Unsaved Changes",
                "You have unsaved caption changes. Save them before leaving?");

            if (save)
            {
                SaveAllCaptions();
            }
        }

        _state.ClearSelectionSilent();
        _state.SetActiveDataset(null);
        DatasetImages.Clear();
        HasUnsavedChanges = false;

        await LoadDatasetsAsync();
    }

    private async Task CreateDatasetAsync()
    {
        if (DialogService is null || _settingsService is null) return;

        var settings = await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            StatusMessage = "Please configure the Dataset Storage Path in Settings first.";
            _activityLog?.LogWarning("Dataset", "Dataset storage path not configured");
            _state.SetStorageConfigured(false);
            return;
        }

        if (!_datasetStorageService.DirectoryExists(settings.DatasetStoragePath))
        {
            StatusMessage = "The configured Dataset Storage Path does not exist. Please update it in Settings.";
            _activityLog?.LogError("Dataset", $"Storage path does not exist: {settings.DatasetStoragePath}");
            _state.SetStorageConfigured(false);
            return;
        }

        _state.SetStorageConfigured(true);

        var result = await DialogService.ShowCreateDatasetDialogAsync(AvailableCategories);
        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.Name)) return;

        var sanitizedName = result.Name;
        var datasetPath = Path.Combine(settings.DatasetStoragePath, sanitizedName);

        if (_datasetStorageService.DirectoryExists(datasetPath))
        {
            StatusMessage = $"A dataset named '{sanitizedName}' already exists.";
            _activityLog?.LogWarning("Dataset", $"Dataset '{sanitizedName}' already exists");
            return;
        }

        try
        {
            _datasetStorageService.CreateDirectory(datasetPath);
            var v1Path = Path.Combine(datasetPath, "V1");
            _datasetStorageService.CreateDirectory(v1Path);

            var newDataset = new DatasetCardViewModel
            {
                Name = sanitizedName,
                FolderPath = datasetPath,
                IsVersionedStructure = true,
                CurrentVersion = 1,
                TotalVersions = 1,
                ImageCount = 0,
                VideoCount = 0,
                CategoryId = result.CategoryId,
                CategoryOrder = result.CategoryOrder,
                CategoryName = result.CategoryName,
                Type = result.Type,
                IsNsfw = result.IsNsfw
            };

            // Set the NSFW flag for V1
            newDataset.VersionNsfwFlags[1] = result.IsNsfw;

            newDataset.SaveMetadata();
            Datasets.Add(newDataset);

            StatusMessage = $"Dataset '{sanitizedName}' created successfully.";
            _activityLog?.LogSuccess("Dataset", $"Created dataset '{sanitizedName}'");

            _eventAggregator.PublishDatasetCreated(new DatasetCreatedEventArgs
            {
                Dataset = newDataset
            });

            await OpenDatasetAsync(newDataset);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create dataset: {ex.Message}";
            _activityLog?.LogError("Dataset", $"Failed to create dataset '{sanitizedName}'", ex);
        }
    }

    private async Task AddImagesAsync()
    {
        if (DialogService is null || ActiveDataset is null) return;

        IsFileDialogOpen = true;

        try
        {
            var destFolderPath = ActiveDataset.CurrentVersionFolderPath;
            _datasetStorageService.CreateDirectory(destFolderPath);

            // Get existing filenames for immediate conflict detection
            var existingFileNames = _datasetStorageService.GetFiles(destFolderPath)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList();

            var result = await DialogService.ShowFileDropDialogWithConflictDetectionAsync(
                $"Add Media to: {ActiveDataset.Name}",
                existingFileNames,
                destFolderPath);

            if (result is null || result.Cancelled) return;

            var filesToAdd = result.GetFilesToAdd().ToList();
            if (filesToAdd.Count == 0) return;

            _activityLog?.LogInfo("Import", $"Importing {filesToAdd.Count} files to '{ActiveDataset.Name}'");
            
            IsLoading = true;
            try
            {
                var copied = 0;
                var overridden = 0;
                var renamed = 0;
                var ignored = 0;

                // Process files based on the result from the dialog
                
                var importResult = await DatasetFileImporter.ImportResolvedAsync(
                    result.NonConflictingFiles,
                    result.ConflictResolutions,
                    destFolderPath,
                    _videoThumbnailService,
                    moveFiles: false);

                copied = importResult.Copied;
                overridden = importResult.Overridden;
                renamed = importResult.Renamed;
                ignored = importResult.Ignored;

                // Calculate total added
                var totalAdded = copied + overridden + renamed;

                // Build status message
                var statusParts = new List<string>();
                if (copied > 0) statusParts.Add($"{copied} new");
                if (overridden > 0) statusParts.Add($"{overridden} overridden");
                if (renamed > 0) statusParts.Add($"{renamed} renamed");
                if (ignored > 0) statusParts.Add($"{ignored} ignored");

                StatusMessage = statusParts.Count > 0
                    ? $"Added {totalAdded} files: " + string.Join(", ", statusParts)
                    : "No files added";

                if (totalAdded > 0)
                {
                    _activityLog?.LogSuccess("Import", $"Imported {totalAdded} files to '{ActiveDataset.Name}'" + 
                        (ignored > 0 ? $" ({ignored} conflicts ignored)" : ""));
                }
                else if (ignored > 0)
                {
                    _activityLog?.LogWarning("Import", $"All {ignored} conflicting files were ignored");
                }

                await OpenDatasetAsync(ActiveDataset);

                if (totalAdded > 0)
                {
                    _eventAggregator.PublishImageAdded(new ImageAddedEventArgs
                    {
                        Dataset = ActiveDataset,
                        AddedImages = []
                    });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error adding files: {ex.Message}";
                _activityLog?.LogError("Import", "Failed to import files", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }
        finally
        {
            IsFileDialogOpen = false;
        }
    }

    private void SaveAllCaptions()
    {
        var saved = 0;
        foreach (var image in DatasetImages.Where(i => i.HasUnsavedChanges))
        {
            image.SaveCaptionCommand.Execute(null);
            saved++;
        }

        HasUnsavedChanges = false;
        StatusMessage = saved > 0 ? $"Saved {saved} captions" : "No changes to save";
    }

    private async Task SwitchVersionAsync(int version)
    {
        if (ActiveDataset is null) return;

        if (HasUnsavedChanges && DialogService is not null)
        {
            var save = await DialogService.ShowConfirmAsync(
                "Unsaved Changes",
                "You have unsaved caption changes. Save them before switching versions?");

            if (save) SaveAllCaptions();
        }

        ActiveDataset.CurrentVersion = version;
        ActiveDataset.SaveMetadata();
        ActiveDataset.RefreshImageInfo();

        await OpenDatasetAsync(ActiveDataset);
        StatusMessage = $"Switched to Version {version}";
    }

    private void OpenContainingFolder()
    {
        if (ActiveDataset is null) return;

        var folderPath = ActiveDataset.CurrentVersionFolderPath;
        if (!_datasetStorageService.DirectoryExists(folderPath))
        {
            folderPath = ActiveDataset.FolderPath;
        }

        if (!_datasetStorageService.DirectoryExists(folderPath)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening folder: {ex.Message}";
        }
    }

    private void SendToImageEdit(DatasetImageViewModel? image)
    {
        if (image is null || ActiveDataset is null) return;

        if (image.IsVideo)
        {
            StatusMessage = "Video editing is not supported. Use an external video editor.";
            return;
        }

        // Publish navigation event
        _eventAggregator.PublishNavigateToImageEditor(new NavigateToImageEditorEventArgs
        {
            Image = image,
            Dataset = ActiveDataset
        });

        StatusMessage = $"Sent to Image Edit: {image.FullFileName}";
    }

    /// <summary>
    /// Sends the image to the Captioning tab for single-image captioning.
    /// </summary>
    [RelayCommand]
    private void SendToCaptioning(DatasetImageViewModel? image)
    {
        if (image is null) return;

        _eventAggregator.PublishNavigateToCaptioning(new NavigateToCaptioningEventArgs
        {
            ImagePath = image.ImagePath
        });

        StatusMessage = $"Sent to Captioning: {image.FullFileName}";
    }

    private void SendToBatchCropScale()
    {
        if (ActiveDataset is null) return;

        _eventAggregator.PublishNavigateToBatchCropScale(new NavigateToBatchCropScaleEventArgs
        {
            Dataset = ActiveDataset,
            Version = ActiveDataset.CurrentVersion
        });

        StatusMessage = $"Sent '{ActiveDataset.Name}' V{ActiveDataset.CurrentVersion} to Batch Crop/Scale";
    }

    private async Task OpenImageViewerAsync(DatasetImageViewModel? image)
    {
        if (DialogService is null || image is null) return;

        var index = DatasetImages.IndexOf(image);
        if (index < 0) return;

        await OpenImageViewerAtIndexAsync(index);
    }

    /// <summary>
    /// Opens the image viewer starting at the first image.
    /// </summary>
    private async Task OpenViewerAsync()
    {
        if (DialogService is null || DatasetImages.Count == 0) return;

        await OpenImageViewerAtIndexAsync(0);
    }

    /// <summary>
    /// Opens the image viewer at the specified index.
    /// </summary>
    private async Task OpenImageViewerAtIndexAsync(int index)
    {
        if (DialogService is null) return;
        if (index < 0 || index >= DatasetImages.Count) return;

        // Pass the event aggregator to enable cross-component state synchronization
        await DialogService.ShowImageViewerDialogAsync(
            DatasetImages,
            index,
            eventAggregator: _eventAggregator,
            onSendToImageEditor: img => SendToImageEdit(img),
            onSendToCaptioning: img => SendToCaptioning(img),
            onDeleteRequested: OnImageDeleteRequested);
    }

    private async void OnImageDeleteRequested(DatasetImageViewModel image)
    {
        if (DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Media",
            $"Delete '{image.FullFileName}' and its caption?");

        if (!confirm) return;

        try
        {
            var thumbnailPath = image.IsVideo
                ? DatasetCardViewModel.GetVideoThumbnailPath(image.ImagePath)
                : null;
            _datasetStorageService.DeleteMediaFiles(image.ImagePath, image.CaptionFilePath, thumbnailPath);

            DatasetImages.Remove(image);

            if (ActiveDataset is not null)
            {
                ActiveDataset.ImageCount = DatasetImages.Count(m => m.IsImage);
                ActiveDataset.VideoCount = DatasetImages.Count(m => m.IsVideo);

                _eventAggregator.PublishImageDeleted(new ImageDeletedEventArgs
                {
                    Dataset = ActiveDataset,
                    ImagePath = image.ImagePath
                });
            }

            StatusMessage = $"Deleted '{image.FullFileName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting media: {ex.Message}";
        }
    }

    /// <summary>
    /// Replaces an existing image/video file with a new file while preserving the name and caption.
    /// </summary>
    [RelayCommand]
    private async Task ReplaceImage(DatasetImageViewModel? image)
    {
        if (image == null || DialogService == null || ActiveDataset == null) return;

        var result = await DialogService.ShowReplaceImageDialogAsync(image);
        if (!result.Confirmed || string.IsNullOrEmpty(result.NewFilePath)) return;

        try
        {
            var sourcePath = result.NewFilePath!;
            var destFolder = Path.GetDirectoryName(image.ImagePath)!;

            if (result.Action == ReplaceAction.Replace)
            {
                var oldFileNameWithoutExt = Path.GetFileNameWithoutExtension(image.ImagePath);
                var newExtension = Path.GetExtension(sourcePath);
                var newDestPath = Path.Combine(destFolder, oldFileNameWithoutExt + newExtension);

                // Prevent replacing with self if paths are identical
                if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(image.ImagePath), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool samePath = string.Equals(image.ImagePath, newDestPath, StringComparison.OrdinalIgnoreCase);

                // If target exists and is not the file we currently point to
                if (!samePath && _datasetStorageService.FileExists(newDestPath))
                {
                    var overwrite = await DialogService.ShowConfirmAsync("File Exists", 
                        $"File '{Path.GetFileName(newDestPath)}' already exists. Overwrite it?");
                    if (!overwrite) return;
                }

                // If path changed (extension changed), delete the old file
                if (!samePath)
                {
                    _datasetStorageService.DeleteFile(image.ImagePath);
                }

                _datasetStorageService.CopyFile(sourcePath, newDestPath, true);

                if (!samePath)
                {
                    image.ImagePath = newDestPath;
                }

                image.RefreshThumbnail();
                _activityLog?.LogInfo("Replace", $"Replaced file '{image.FullFileName}' with '{Path.GetFileName(sourcePath)}'");
            }
            else if (result.Action == ReplaceAction.AddAsNew)
            {
                var newFileName = Path.GetFileName(sourcePath);
                var uniquePath = _datasetStorageService.GetUniqueFilePath(destFolder, newFileName);
                
                _datasetStorageService.CopyFile(sourcePath, uniquePath, false);
                
                var newImageVm = DatasetImageViewModel.FromFile(uniquePath, _eventAggregator);
                if (newImageVm.IsVideo && _videoThumbnailService != null)
                {
                    await GenerateVideoThumbnailAsync(newImageVm);
                }
                
                DatasetImages.Add(newImageVm);
                
                if (ActiveDataset != null)
                {
                    ActiveDataset.ImageCount = DatasetImages.Count(m => m.IsImage);
                    ActiveDataset.VideoCount = DatasetImages.Count(m => m.IsVideo);
                }
                
                _activityLog?.LogInfo("Import", $"Added new file '{newImageVm.FullFileName}' alongside '{image.FullFileName}'");
            }
        }
        catch (Exception ex)
        {
            await DialogService.ShowMessageAsync("Error", $"Failed to process file: {ex.Message}");
        }
    }

    private async Task DeleteDatasetAsync(DatasetCardViewModel? dataset)
    {
        if (dataset is null || DialogService is null) return;

        if (dataset.IsVersionCard && dataset.DisplayVersion.HasValue)
        {
            await DeleteVersionAsync(dataset, dataset.DisplayVersion.Value);
        }
        else if (!FlattenVersions && dataset.HasMultipleVersions)
        {
            // In stacked/unflatten view with multiple versions - show version selection dialog
            var result = await DialogService.ShowSelectVersionsToDeleteDialogAsync(dataset);
            if (!result.Confirmed) return;

            await DeleteSelectedVersionsAsync(dataset, result.SelectedVersions, result.DeleteEntireDataset);
        }
        else
        {
            var confirm = await DialogService.ShowConfirmAsync(
                "Delete Dataset",
                $"Are you sure you want to delete '{dataset.Name}'? This will permanently delete all images and captions in ALL versions of this dataset.");

            if (!confirm) return;

            try
            {
                _datasetStorageService.DeleteDirectory(dataset.FolderPath, recursive: true);
                Datasets.Remove(dataset);

                foreach (var group in GroupedDatasets)
                {
                    group.Datasets.Remove(dataset);
                }

                var emptyGroups = GroupedDatasets.Where(g => !g.HasDatasets).ToList();
                foreach (var emptyGroup in emptyGroups)
                {
                    GroupedDatasets.Remove(emptyGroup);
                }

                // Refresh the filtered view to update the UI
                ApplyFilter();

                _eventAggregator.PublishDatasetDeleted(new DatasetDeletedEventArgs
                {
                    Dataset = dataset
                });

                StatusMessage = $"Deleted dataset '{dataset.Name}'";
                _activityLog?.LogSuccess("Dataset", $"Deleted dataset '{dataset.Name}'");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting dataset: {ex.Message}";
                _activityLog?.LogError("Dataset", $"Failed to delete dataset '{dataset.Name}'", ex);
            }
        }
    }

    /// <summary>
    /// Deletes selected versions from a dataset based on user selection from the version selection dialog.
    /// </summary>
    private async Task DeleteSelectedVersionsAsync(DatasetCardViewModel dataset, List<int> versionsToDelete, bool deleteEntireDataset)
    {
        if (deleteEntireDataset)
        {
            // Delete the entire dataset
            try
            {
                _datasetStorageService.DeleteDirectory(dataset.FolderPath, recursive: true);
                Datasets.Remove(dataset);

                foreach (var group in GroupedDatasets)
                {
                    group.Datasets.Remove(dataset);
                }

                var emptyGroups = GroupedDatasets.Where(g => !g.HasDatasets).ToList();
                foreach (var emptyGroup in emptyGroups)
                {
                    GroupedDatasets.Remove(emptyGroup);
                }

                ApplyFilter();

                _eventAggregator.PublishDatasetDeleted(new DatasetDeletedEventArgs
                {
                    Dataset = dataset
                });

                StatusMessage = $"Deleted dataset '{dataset.Name}' (all {versionsToDelete.Count} versions)";
                _activityLog?.LogSuccess("Dataset", $"Deleted dataset '{dataset.Name}' with all versions");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting dataset: {ex.Message}";
                _activityLog?.LogError("Dataset", $"Failed to delete dataset '{dataset.Name}'", ex);
            }
        }
        else
        {
            // Delete selected versions one by one
            var deletedCount = 0;
            var parentDataset = Datasets.FirstOrDefault(d => d.FolderPath == dataset.FolderPath);

            foreach (var version in versionsToDelete)
            {
                try
                {
                    var versionPath = dataset.GetVersionFolderPath(version);
                    _datasetStorageService.DeleteDirectory(versionPath, recursive: true);

                    // Clean up metadata
                    dataset.VersionBranchedFrom.Remove(version);
                    dataset.VersionDescriptions.Remove(version);
                    dataset.VersionNsfwFlags.Remove(version);

                    if (parentDataset is not null && parentDataset != dataset)
                    {
                        parentDataset.VersionBranchedFrom.Remove(version);
                        parentDataset.VersionDescriptions.Remove(version);
                        parentDataset.VersionNsfwFlags.Remove(version);
                    }

                    _eventAggregator.PublishDatasetDeleted(new DatasetDeletedEventArgs
                    {
                        Dataset = dataset,
                        DeletedVersion = version
                    });

                    deletedCount++;
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Error deleting V{version}: {ex.Message}";
                    _activityLog?.LogError("Dataset", $"Failed to delete V{version} of '{dataset.Name}'", ex);
                }
            }

            // Update the current version if it was deleted
            if (parentDataset is not null)
            {
                if (versionsToDelete.Contains(parentDataset.CurrentVersion))
                {
                    var remainingVersions = parentDataset.GetAllVersionNumbers();
                    parentDataset.CurrentVersion = remainingVersions.FirstOrDefault();
                }

                parentDataset.RefreshImageInfo();
                parentDataset.SaveMetadata();
            }

            // Reload datasets to refresh the UI
            await LoadDatasetsAsync();

            StatusMessage = deletedCount == 1
                ? $"Deleted V{versionsToDelete[0]} of '{dataset.Name}'"
                : $"Deleted {deletedCount} versions of '{dataset.Name}'";
            
            _activityLog?.LogSuccess("Dataset", $"Deleted {deletedCount} versions of '{dataset.Name}'");
        }
    }

    private async Task DeleteVersionAsync(DatasetCardViewModel dataset, int version)
    {
        if (DialogService is null) return;

        var versionPath = dataset.GetVersionFolderPath(version);
        var allVersions = dataset.GetAllVersionNumbers();
        var isLastVersion = allVersions.Count == 1;

        var confirmMessage = isLastVersion
            ? $"Are you sure you want to delete V{version} of '{dataset.Name}'?\n\nThis is the only version - the entire dataset will be removed."
            : $"Are you sure you want to delete V{version} of '{dataset.Name}'?\n\nThis will permanently delete all images and captions in this version.";

        var confirm = await DialogService.ShowConfirmAsync("Delete Version", confirmMessage);
        if (!confirm) return;

        try
        {
            if (isLastVersion)
            {
                _datasetStorageService.DeleteDirectory(dataset.FolderPath, recursive: true);
                var parentDataset = Datasets.FirstOrDefault(d => d.FolderPath == dataset.FolderPath);
                if (parentDataset is not null)
                {
                    Datasets.Remove(parentDataset);
                }
            }
            else
            {
                _datasetStorageService.DeleteDirectory(versionPath, recursive: true);

                dataset.VersionBranchedFrom.Remove(version);
                dataset.VersionDescriptions.Remove(version);

                var parentDataset = Datasets.FirstOrDefault(d => d.FolderPath == dataset.FolderPath);
                if (parentDataset is not null)
                {
                    parentDataset.VersionBranchedFrom.Remove(version);
                    parentDataset.VersionDescriptions.Remove(version);

                    if (parentDataset.CurrentVersion == version)
                    {
                        var remainingVersions = parentDataset.GetAllVersionNumbers();
                        parentDataset.CurrentVersion = remainingVersions.FirstOrDefault(v => v != version);
                        if (parentDataset.CurrentVersion == 0)
                        {
                            parentDataset.CurrentVersion = remainingVersions.First();
                        }
                    }

                    parentDataset.RefreshImageInfo();
                    parentDataset.SaveMetadata();
                }
            }

            foreach (var group in GroupedDatasets)
            {
                group.Datasets.Remove(dataset);
            }

            var emptyGroups = GroupedDatasets.Where(g => !g.HasDatasets).ToList();
            foreach (var emptyGroup in emptyGroups)
            {
                GroupedDatasets.Remove(emptyGroup);
            }

            // Refresh the filtered view to update the UI
            ApplyFilter();

            _eventAggregator.PublishDatasetDeleted(new DatasetDeletedEventArgs
            {
                Dataset = dataset,
                DeletedVersion = version
            });

            StatusMessage = isLastVersion
                ? $"Deleted dataset '{dataset.Name}'"
                : $"Deleted V{version} of '{dataset.Name}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting version: {ex.Message}";
        }
    }

    private async Task IncrementVersionAsync()
    {
        if (ActiveDataset is null || DialogService is null) return;

        var currentVersion = ActiveDataset.CurrentVersion;
        var nextVersion = ActiveDataset.GetNextVersionNumber();
        var availableVersions = ActiveDataset.GetAllVersionNumbers();

        // Pass current version's media files for rating-based filtering
        var result = await DialogService.ShowCreateVersionDialogAsync(
            currentVersion,
            availableVersions,
            DatasetImages);

        if (!result.Confirmed) return;

        var copyFiles = result.SourceOption == VersionSourceOption.CopyFromVersion;
        var sourceVersion = result.SourceVersion;

        // If copy is selected but no content types or no rating categories are checked, treat as start fresh
        if (copyFiles && (!result.CopyImages && !result.CopyVideos && !result.CopyCaptions))
        {
            copyFiles = false;
        }
        if (copyFiles && (!result.IncludeProductionReady && !result.IncludeUnrated && !result.IncludeTrash))
        {
            copyFiles = false;
        }

        IsLoading = true;
        try
        {
            var destPath = ActiveDataset.GetVersionFolderPath(nextVersion);

            if (!ActiveDataset.IsVersionedStructure && ActiveDataset.ImageCount > 0)
            {
                await DatasetVersionUtilities.EnsureVersionedStructureAsync(ActiveDataset);
            }

            _datasetStorageService.CreateDirectory(destPath);

            var copied = 0;
            if (copyFiles)
            {
                var sourcePath = ActiveDataset.GetVersionFolderPath(sourceVersion);
                
                // Build a dictionary of file base names with their ratings that match the rating filter
                // (based on DatasetImages which has the rating info)
                var allowedFiles = new Dictionary<string, ImageRatingStatus>(StringComparer.OrdinalIgnoreCase);
                foreach (var img in DatasetImages)
                {
                    var shouldInclude = 
                        (result.IncludeProductionReady && img.IsApproved) ||
                        (result.IncludeUnrated && img.IsUnrated) ||
                        (result.IncludeTrash && img.IsRejected);
                    
                    if (shouldInclude)
                    {
                        // Add the base name (without extension) for matching media and caption files
                        var baseName = Path.GetFileNameWithoutExtension(img.ImagePath);
                        allowedFiles[baseName] = img.RatingStatus;
                    }
                }

                var files = _datasetStorageService.EnumerateFiles(sourcePath)
                    .Where(f => !Path.GetFileName(f).StartsWith("."))
                    .ToList();

                foreach (var sourceFile in files)
                {
                    var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                    var extension = Path.GetExtension(sourceFile).ToLowerInvariant();
                    var shouldCopy = false;

                    // Check if this file type should be copied based on user selection
                    if (result.CopyImages && MediaFileExtensions.IsImageFile(sourceFile))
                    {
                        // For images, check if base name is in allowed set (rating filter)
                        shouldCopy = allowedFiles.ContainsKey(baseName);
                    }
                    else if (result.CopyVideos && MediaFileExtensions.IsVideoFile(sourceFile))
                    {
                        // For videos, check if base name is in allowed set (rating filter)
                        shouldCopy = allowedFiles.ContainsKey(baseName);
                    }
                    else if (result.CopyCaptions && MediaFileExtensions.IsCaptionFile(sourceFile))
                    {
                        // For captions, only copy if the corresponding media file is being copied
                        shouldCopy = allowedFiles.ContainsKey(baseName);
                    }
                    else if (extension == ".rating")
                    {
                        // Skip .rating files in the main loop - we handle them separately below
                        continue;
                    }

                    if (shouldCopy)
                    {
                        var fileName = Path.GetFileName(sourceFile);
                        var destFile = Path.Combine(destPath, fileName);
                        _datasetStorageService.CopyFile(sourceFile, destFile, overwrite: false);
                        copied++;
                    }
                }

                // Copy ratings if the option is selected
                if (result.CopyRatings)
                {
                    foreach (var (baseName, rating) in allowedFiles)
                    {
                        // Only copy rating if it's not Unrated (Unrated means no .rating file)
                        if (rating != ImageRatingStatus.Unrated)
                        {
                            var sourceRatingFile = Path.Combine(sourcePath, baseName + ".rating");
                            var destRatingFile = Path.Combine(destPath, baseName + ".rating");
                            
                            _datasetStorageService.CopyFileIfExists(sourceRatingFile, destRatingFile, overwrite: false);
                        }
                    }
                }
            }

            // Inherit NSFW flag from the parent version by default
            var parentNsfw = ActiveDataset.VersionNsfwFlags.GetValueOrDefault(currentVersion, false);
            ActiveDataset.VersionNsfwFlags[nextVersion] = parentNsfw;

            ActiveDataset.RecordBranch(nextVersion, currentVersion);
            ActiveDataset.CurrentVersion = nextVersion;
            ActiveDataset.IsVersionedStructure = true;
            ActiveDataset.SaveMetadata();
            ActiveDataset.RefreshImageInfo();

            _eventAggregator.PublishVersionCreated(new VersionCreatedEventArgs
            {
                Dataset = ActiveDataset,
                NewVersion = nextVersion,
                BranchedFromVersion = currentVersion
            });

            await OpenDatasetAsync(ActiveDataset);

            StatusMessage = copyFiles
                ? $"Created V{nextVersion} (branched from V{currentVersion}) with {copied} files copied."
                : $"Created V{nextVersion} (branched from V{currentVersion}, empty - ready to add images).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating new version: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExportDatasetAsync()
    {
        if (DialogService is null || ActiveDataset is null)
        {
            StatusMessage = "No dataset selected for export.";
            return;
        }

        if (DatasetImages.Count == 0)
        {
            StatusMessage = "No files in dataset to export.";
            _activityLog?.LogWarning("Export", "No files to export");
            return;
        }

        // Query AI Toolkit instances from the database
        IReadOnlyList<InstallerPackage>? aiToolkitInstances = null;
        try
        {
            var repo = App.Services?.GetService<IInstallerPackageRepository>();
            if (repo is not null)
            {
                var allPackages = await repo.GetAllAsync();
                var toolkitList = allPackages
                    .Where(p => p.Type == InstallerType.AIToolkit)
                    .ToList();
                if (toolkitList.Count > 0)
                    aiToolkitInstances = toolkitList;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Failed to query AI Toolkit instances for export dialog");
        }

        var result = await DialogService.ShowExportDialogAsync(ActiveDataset.Name, DatasetImages, aiToolkitInstances);
        if (!result.Confirmed || result.FilesToExport.Count == 0) return;

        string? destinationPath;
        if (result.ExportType == ExportType.AIToolkit)
        {
            if (string.IsNullOrWhiteSpace(result.AIToolkitInstallationPath))
            {
                StatusMessage = "No AI Toolkit instance selected.";
                return;
            }

            // Use the user-specified folder name, falling back to the dataset name
            var folderName = !string.IsNullOrWhiteSpace(result.AIToolkitFolderName)
                ? result.AIToolkitFolderName.Trim()
                : ActiveDataset.Name;

            destinationPath = Path.Combine(result.AIToolkitInstallationPath, "datasets", folderName);

            if (Directory.Exists(destinationPath))
            {
                var existingFiles = Directory.GetFiles(destinationPath);
                if (existingFiles.Length > 0 && result.AIToolkitConflictMode == AIToolkitConflictMode.Overwrite)
                {
                    Directory.Delete(destinationPath, recursive: true);
                    Directory.CreateDirectory(destinationPath);
                }
                // Merge mode: just proceed — ExportAsSingleFiles uses overwrite:true
            }
            else
            {
                Directory.CreateDirectory(destinationPath);
            }
        }
        else if (result.ExportType == ExportType.Zip)
        {
            var dateStr = DateTime.Today.ToString("yyyy-MM-dd");
            var defaultFileName = $"{ActiveDataset.Name}_V{ActiveDataset.CurrentVersion}-{dateStr}.zip";
            destinationPath = await DialogService.ShowSaveFileDialogAsync("Export Dataset as ZIP", defaultFileName, "*.zip");
        }
        else
        {
            destinationPath = await DialogService.ShowOpenFolderDialogAsync("Select Export Destination Folder");
        }

        if (string.IsNullOrEmpty(destinationPath)) return;

        _activityLog?.LogInfo("Export", $"Exporting '{ActiveDataset.Name}' ({result.FilesToExport.Count} files)");

        IsLoading = true;
        try
        {
            var exportItems = result.FilesToExport
                .Select(file => new DatasetExportItem(
                    file.ImagePath,
                    file.FullFileName,
                    file.CaptionFilePath,
                    Path.GetFileName(file.CaptionFilePath)))
                .ToList();

            var exportedCount = result.ExportType == ExportType.Zip
                ? _datasetStorageService.ExportAsZip(exportItems, destinationPath)
                : _datasetStorageService.ExportAsSingleFiles(exportItems, destinationPath);

            if (result.ExportType == ExportType.AIToolkit)
            {
                StatusMessage = $"Exported {exportedCount} files to AI Toolkit '{result.AIToolkitInstanceName}'.";
                _activityLog?.LogSuccess("Export", $"Exported {exportedCount} files from '{ActiveDataset.Name}' to AI Toolkit '{result.AIToolkitInstanceName}'");
            }
            else
            {
                StatusMessage = $"Exported {exportedCount} files successfully.";
                _activityLog?.LogSuccess("Export", $"Exported {exportedCount} files from '{ActiveDataset.Name}'");
            }

            // Open the export location in Explorer
            OpenFolderInExplorer(destinationPath, result.ExportType == ExportType.Zip);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            _activityLog?.LogError("Export", $"Export failed for '{ActiveDataset.Name}'", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Opens the specified path in Windows Explorer.
    /// If isFile is true, opens the containing folder and selects the file.
    /// </summary>
    private void OpenFolderInExplorer(string path, bool isFile)
    {
        try
        {
            if (isFile && _datasetStorageService.FileExists(path))
            {
                // Open Explorer and select the file
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (_datasetStorageService.DirectoryExists(path))
            {
                // Open the folder
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore errors opening Explorer - not critical
        }
    }

    #endregion

    #region Filter Methods

    /// <summary>
    /// Applies the current filter to the grouped datasets.
    /// </summary>
    private void ApplyFilter()
    {
        FilteredGroupedDatasets.Clear();

        var filterText = _filterText?.Trim() ?? string.Empty;
        var hasTextFilter = !string.IsNullOrWhiteSpace(filterText);
        var hasTypeFilter = _filterType.HasValue;

        // Count all hidden datasets
        var hiddenCount = 0;

        foreach (var group in GroupedDatasets)
        {
            var filteredDatasets = new List<DatasetCardViewModel>();

            foreach (var dataset in group.Datasets)
            {
                // Step 1: Resolve the Safe Representation if in Safe Mode
                DatasetCardViewModel? cardToShow = dataset;

                if (!_showNsfw)
                {
                    // Get a safe snapshot. This returns:
                    // - 'dataset' (this) if it's already safe
                    // - A transient copy pointing to a safe version if it's Mixed but currently NSFW
                    // - null if it's Pure NSFW
                    cardToShow = dataset.GetSafeSnapshot();
                }

                // If card is null (Hidden by Safe Mode), count as hidden and skip
                if (cardToShow is null)
                {
                    hiddenCount++;
                    continue;
                }

                // Step 2: Apply Text and Type filters to the *resolved* card
                // (We filter the snapshot to ensure Text/Description matches the displayed version if needed, 
                // though typically Name is constant).
                if (MatchesBasicFilters(cardToShow, filterText, hasTextFilter, hasTypeFilter))
                {
                    filteredDatasets.Add(cardToShow);
                }
                else
                {
                    hiddenCount++;
                }
            }

            if (filteredDatasets.Count > 0)
            {
                var filteredGroup = new DatasetGroupViewModel
                {
                    CategoryId = group.CategoryId,
                    Name = group.Name,
                    Description = group.Description,
                    SortOrder = group.SortOrder
                };

                foreach (var dataset in filteredDatasets)
                {
                    filteredGroup.Datasets.Add(dataset);
                }

                FilteredGroupedDatasets.Add(filteredGroup);
            }
        }

        HiddenCount = hiddenCount;
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(HiddenText));
        OnPropertyChanged(nameof(IsStorageConfiguredButEmpty));
        OnPropertyChanged(nameof(HasDatasetsToShow));
    }

    /// <summary>
    /// Checks basic Text and Type filters (NSFW handled by GetSafeSnapshot in ApplyFilter).
    /// </summary>
    private bool MatchesBasicFilters(DatasetCardViewModel dataset, string filterText, bool hasTextFilter, bool hasTypeFilter)
    {
        // Check type filter
        if (hasTypeFilter && dataset.Type != _filterType)
        {
            return false;
        }

        // Check text filter (matches name or description)
        if (hasTextFilter)
        {
            var matchesName = dataset.Name?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true;
            var matchesDescription = dataset.Description?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true;

            if (!matchesName && !matchesDescription)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Clears all active filters.
    /// </summary>
    public void ClearFilters()
    {
        _filterText = string.Empty;
        _filterType = null;
        _showNsfw = false;
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(FilterType));
        OnPropertyChanged(nameof(ShowNsfw));
        ApplyFilter();
    }

    #endregion

    #region Selection Methods

    private void ToggleSelection(DatasetImageViewModel? image)
    {
        if (image is null) return;
        image.IsSelected = !image.IsSelected;
        _state.LastClickedImage = image;
        _state.UpdateSelectionCount();
    }

    /// <summary>
    /// Handles selection with modifier keys (Shift for range, Ctrl for toggle).
    /// </summary>
    public void SelectWithModifiers(DatasetImageViewModel? image, bool isShiftPressed, bool isCtrlPressed)
    {
        if (image is null) return;

        if (isShiftPressed && _state.LastClickedImage is not null)
        {
            SelectRange(_state.LastClickedImage, image);
        }
        else if (isCtrlPressed)
        {
            image.IsSelected = !image.IsSelected;
            _state.LastClickedImage = image;
        }
        else
        {
            _state.ClearSelectionSilent();
            image.IsSelected = true;
            _state.LastClickedImage = image;
        }

        _state.UpdateSelectionCount();
    }

    private void SelectRange(DatasetImageViewModel from, DatasetImageViewModel to)
    {
        var fromIndex = DatasetImages.IndexOf(from);
        var toIndex = DatasetImages.IndexOf(to);

        if (fromIndex == -1 || toIndex == -1) return;

        var startIndex = Math.Min(fromIndex, toIndex);
        var endIndex = Math.Max(fromIndex, toIndex);

        for (var i = startIndex; i <= endIndex; i++)
        {
            DatasetImages[i].IsSelected = true;
        }

        _state.LastClickedImage = to;
    }

    private void SelectAll()
    {
        foreach (var image in DatasetImages)
        {
            image.IsSelected = true;
        }
        _state.UpdateSelectionCount();
        StatusMessage = $"Selected all {SelectionCount} items";
    }

    private void ClearSelection()
    {
        _state.ClearSelectionSilent();
        StatusMessage = "Selection cleared";
    }

    private void ApproveSelected() 
        => SetRatingForSelected(ImageRatingStatus.Approved, "Marked {0} items as production-ready");

    private void RejectSelected() 
        => SetRatingForSelected(ImageRatingStatus.Rejected, "Marked {0} items as trash");

    private void ClearRatingSelected() 
        => SetRatingForSelected(ImageRatingStatus.Unrated, "Cleared rating for {0} items");

    /// <summary>
    /// Sets the rating for all selected images and publishes change events.
    /// </summary>
    private void SetRatingForSelected(ImageRatingStatus newRating, string statusMessageFormat)
    {
        var selected = DatasetImages.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var image in selected)
        {
            var previousRating = image.RatingStatus;
            image.RatingStatus = newRating;
            image.SaveRating();

            _eventAggregator.PublishImageRatingChanged(new ImageRatingChangedEventArgs
            {
                Image = image,
                NewRating = newRating,
                PreviousRating = previousRating
            });
        }

        StatusMessage = string.Format(statusMessageFormat, selected.Count);
    }

    private void SelectApproved()
    {
        _state.ClearSelectionSilent();
        foreach (var image in DatasetImages.Where(i => i.RatingStatus == ImageRatingStatus.Approved))
        {
            image.IsSelected = true;
        }
        _state.UpdateSelectionCount();
        StatusMessage = $"Selected {SelectionCount} approved items";
    }

    private void SelectRejected()
    {
        _state.ClearSelectionSilent();
        foreach (var image in DatasetImages.Where(i => i.RatingStatus == ImageRatingStatus.Rejected))
        {
            image.IsSelected = true;
        }
        _state.UpdateSelectionCount();
        StatusMessage = $"Selected {SelectionCount} rejected items";
    }

    private async Task DeleteSelectedAsync()
    {
        if (DialogService is null) return;

        var selectedImages = DatasetImages.Where(i => i.IsSelected).ToList();
        if (selectedImages.Count == 0) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Selected Media",
            $"Are you sure you want to delete {selectedImages.Count} selected media items?");

        if (!confirm) return;

        try
        {
            foreach (var image in selectedImages)
            {
                var thumbnailPath = image.IsVideo
                    ? DatasetCardViewModel.GetVideoThumbnailPath(image.ImagePath)
                    : null;
                _datasetStorageService.DeleteMediaFiles(image.ImagePath, image.CaptionFilePath, thumbnailPath);

                DatasetImages.Remove(image);

                if (ActiveDataset is not null)
                {
                    _eventAggregator.PublishImageDeleted(new ImageDeletedEventArgs
                    {
                        Dataset = ActiveDataset,
                        ImagePath = image.ImagePath
                    });
                }
            }

            if (ActiveDataset is not null)
            {
                ActiveDataset.ImageCount = DatasetImages.Count(m => m.IsImage);
                ActiveDataset.VideoCount = DatasetImages.Count(m => m.IsVideo);
            }

            _state.UpdateSelectionCount();
            StatusMessage = $"Deleted {selectedImages.Count} items";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting selected media: {ex.Message}";
        }
    }

    private async Task DeleteImageAsync(DatasetImageViewModel? image)
    {
        if (image is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Image",
            $"Delete '{image.FullFileName}' and its caption?");

        if (!confirm) return;

        try
        {
            var thumbnailPath = image.IsVideo
                ? DatasetCardViewModel.GetVideoThumbnailPath(image.ImagePath)
                : null;
            _datasetStorageService.DeleteMediaFiles(image.ImagePath, image.CaptionFilePath, thumbnailPath);

            DatasetImages.Remove(image);

            if (ActiveDataset is not null)
            {
                ActiveDataset.ImageCount = DatasetImages.Count(m => m.IsImage);
                ActiveDataset.VideoCount = DatasetImages.Count(m => m.IsVideo);

                _eventAggregator.PublishImageDeleted(new ImageDeletedEventArgs
                {
                    Dataset = ActiveDataset,
                    ImagePath = image.ImagePath
                });
            }

            StatusMessage = $"Deleted '{image.FullFileName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting image: {ex.Message}";
        }
    }

    private async Task OpenCaptioningToolAsync()
    {
        if (DialogService is null) return;
        
        if (_captioningService is null)
        {
            StatusMessage = "Captioning service is not available.";
            return;
        }

        try
        {
            // Pass the Datasets collection to the dialog
            await DialogService.ShowCaptioningDialogAsync(
                _captioningService, 
                Datasets ?? [], 
                _eventAggregator,
                ActiveDataset,
                ActiveDataset?.CurrentVersion);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening captioning tool: {ex.Message}";
            _activityLog?.LogError("Captioning", "Failed to open captioning tool", ex);
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Refreshes the active dataset.
    /// </summary>
    public async Task RefreshActiveDatasetAsync()
    {
        if (ActiveDataset is not null)
        {
            ActiveDataset.RefreshImageInfo();
            if (IsViewingDataset)
            {
                await OpenDatasetAsync(ActiveDataset);
            }
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases all resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this ViewModel.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Stop the backup countdown timer
            StopBackupCountdownTimer();

            // Unsubscribe from events to prevent memory leaks
            _state.StateChanged -= OnStateChanged;
            _eventAggregator.ImageSaved -= OnImageSaved;
            _eventAggregator.ImageRatingChanged -= OnImageRatingChanged;
            _eventAggregator.SettingsSaved -= OnSettingsSaved;
            _eventAggregator.ImageSelectionChanged -= OnImageSelectionChanged;

            CaptioningTab.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
