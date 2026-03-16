using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for a training run card displayed in the Training Runs tab.
/// Represents a single training run (e.g., "SDXL_MyCharacter") within a dataset version.
/// </summary>
public partial class TrainingRunCardViewModel : ObservableObject, IDialogServiceAware
{
    private readonly IDatasetEventAggregator _eventAggregator;
    private string _runFolderPath = string.Empty;
    private TrainingRunSubTab _selectedSubTab = TrainingRunSubTab.Epochs;
    private bool _isViewingDetail;
    private string? _thumbnailPath;
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;

    /// <summary>
    /// The training run metadata.
    /// </summary>
    public TrainingRunInfo RunInfo { get; }

    /// <summary>
    /// Display name of the training run.
    /// </summary>
    public string Name => RunInfo.Name;

    /// <summary>
    /// Optional base model identifier.
    /// </summary>
    public string? BaseModel => RunInfo.BaseModel;

    /// <summary>
    /// Whether the run has a base model specified.
    /// </summary>
    public bool HasBaseModel => !string.IsNullOrWhiteSpace(RunInfo.BaseModel);

    /// <summary>
    /// Creation date for display.
    /// </summary>
    public string CreatedDateText => RunInfo.CreatedAt.ToString("MMM dd, yyyy");

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description => RunInfo.Description;

    /// <summary>
    /// Whether the run has a description.
    /// </summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(RunInfo.Description);

    /// <summary>
    /// Trigger word used to activate this LoRA/model during inference.
    /// Persists to metadata on change.
    /// </summary>
    public string? TriggerWord
    {
        get => RunInfo.TriggerWord;
        set
        {
            if (!string.Equals(RunInfo.TriggerWord, value, StringComparison.Ordinal))
            {
                RunInfo.TriggerWord = value;
                OnPropertyChanged();
                OnMetadataChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Callback invoked when metadata (e.g., trigger word) changes, allowing the parent to persist.
    /// </summary>
    public Action? OnMetadataChanged { get; set; }

    /// <summary>
    /// Full path to the training run folder.
    /// </summary>
    public string RunFolderPath
    {
        get => _runFolderPath;
        private set => SetProperty(ref _runFolderPath, value);
    }

    /// <summary>
    /// Path to the Epochs subfolder within this training run.
    /// </summary>
    public string EpochsFolderPath => Path.Combine(_runFolderPath, "Epochs");

    /// <summary>
    /// Path to the Notes subfolder within this training run.
    /// </summary>
    public string NotesFolderPath => Path.Combine(_runFolderPath, "Notes");

    /// <summary>
    /// Path to the Presentation subfolder within this training run.
    /// </summary>
    public string PresentationFolderPath => Path.Combine(_runFolderPath, "Presentation");

    /// <summary>
    /// Path to the Release subfolder within this training run.
    /// </summary>
    public string ReleaseFolderPath => Path.Combine(_runFolderPath, "Release");

    /// <summary>
    /// Number of epoch files in this training run.
    /// </summary>
    public int EpochCount { get; private set; }

    /// <summary>
    /// Number of notes in this training run.
    /// </summary>
    public int NoteCount { get; private set; }

    /// <summary>
    /// Display text for epoch count.
    /// </summary>
    public string EpochCountText => EpochCount == 1 ? "1 epoch" : $"{EpochCount} epochs";

    /// <summary>
    /// Display text for note count.
    /// </summary>
    public string NoteCountText => NoteCount == 1 ? "1 note" : $"{NoteCount} notes";

    /// <summary>
    /// Number of presentation files in this training run.
    /// </summary>
    public int PresentationFileCount { get; private set; }

    /// <summary>
    /// Display text for presentation file count.
    /// </summary>
    public string PresentationFileCountText => PresentationFileCount == 1 ? "1 file" : $"{PresentationFileCount} files";

    /// <summary>
    /// Whether there are presentation files.
    /// </summary>
    public bool HasPresentationFiles => PresentationFileCount > 0;

    /// <summary>
    /// Path to the first presentation image for thumbnail preview.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        private set
        {
            if (SetProperty(ref _thumbnailPath, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
                _thumbnail = null;
                _isThumbnailLoading = false;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    /// <summary>
    /// Whether this card has a thumbnail image.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath);

    /// <summary>
    /// The loaded thumbnail bitmap. Loads asynchronously on first access.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is not null)
                return _thumbnail;

            var path = ThumbnailPath;
            if (string.IsNullOrEmpty(path))
                return null;

            var orchestrator = PathToBitmapConverter.ThumbnailOrchestrator;
            if (orchestrator?.TryGetCached(path, out var cached) == true && cached is not null)
            {
                _thumbnail = cached;
                return _thumbnail;
            }

            if (!_isThumbnailLoading)
            {
                _isThumbnailLoading = true;
                _ = LoadThumbnailAsync(path);
            }

            return null;
        }
    }

    /// <summary>
    /// Currently selected sub-tab within the run detail view.
    /// </summary>
    public TrainingRunSubTab SelectedSubTab
    {
        get => _selectedSubTab;
        set => SetProperty(ref _selectedSubTab, value);
    }

    /// <summary>
    /// Whether we are viewing the detail of this training run (vs the card list).
    /// </summary>
    public bool IsViewingDetail
    {
        get => _isViewingDetail;
        set => SetProperty(ref _isViewingDetail, value);
    }

    /// <summary>
    /// Gets or sets the dialog service for confirmations.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// ViewModel for the Epochs sub-tab within this training run.
    /// </summary>
    public EpochsTabViewModel EpochsTab { get; }

    /// <summary>
    /// ViewModel for the Notes sub-tab within this training run.
    /// </summary>
    public NotesTabViewModel NotesTab { get; }

    /// <summary>
    /// ViewModel for the Presentation sub-tab within this training run.
    /// </summary>
    public PresentationTabViewModel PresentationTab { get; }

    public TrainingRunCardViewModel(TrainingRunInfo runInfo, string runFolderPath, IDatasetEventAggregator eventAggregator)
    {
        RunInfo = runInfo ?? throw new ArgumentNullException(nameof(runInfo));
        ArgumentNullException.ThrowIfNull(runFolderPath);
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        _runFolderPath = runFolderPath;

        // Initialize sub-tab ViewModels
        EpochsTab = new EpochsTabViewModel(_eventAggregator);
        NotesTab = new NotesTabViewModel(_eventAggregator);
        PresentationTab = new PresentationTabViewModel(_eventAggregator);

        // Wire up file change callbacks so card counts and thumbnail refresh live
        EpochsTab.OnFilesChanged = RefreshCounts;
        NotesTab.OnFilesChanged = RefreshCounts;
        PresentationTab.OnFilesChanged = RefreshCounts;

        // Load counts
        RefreshCounts();
    }

    /// <summary>
    /// Initializes the sub-tab ViewModels for this training run.
    /// Called when user navigates into this run's detail view.
    /// </summary>
    public void InitializeSubTabs()
    {
        EpochsTab.Initialize(RunFolderPath);
        NotesTab.Initialize(RunFolderPath);
        PresentationTab.Initialize(RunFolderPath);

        // Forward dialog service
        EpochsTab.DialogService = DialogService;
        NotesTab.DialogService = DialogService;
        PresentationTab.DialogService = DialogService;
    }

    /// <summary>
    /// Refreshes the epoch, note, and presentation counts from the file system.
    /// Also discovers the first presentation image for thumbnail preview.
    /// </summary>
    public void RefreshCounts()
    {
        EpochCount = CountEpochFiles();
        NoteCount = CountNoteFiles();
        PresentationFileCount = CountPresentationFiles();
        ThumbnailPath = FindFirstPresentationImage();
        OnPropertyChanged(nameof(EpochCount));
        OnPropertyChanged(nameof(NoteCount));
        OnPropertyChanged(nameof(PresentationFileCount));
        OnPropertyChanged(nameof(EpochCountText));
        OnPropertyChanged(nameof(NoteCountText));
        OnPropertyChanged(nameof(PresentationFileCountText));
        OnPropertyChanged(nameof(HasPresentationFiles));
    }

    private int CountEpochFiles()
    {
        if (!Directory.Exists(EpochsFolderPath))
            return 0;

        return Directory.EnumerateFiles(EpochsFolderPath)
            .Count(f => EpochFileItem.IsEpochFile(f));
    }

    private int CountNoteFiles()
    {
        if (!Directory.Exists(NotesFolderPath))
            return 0;

        return Directory.EnumerateFiles(NotesFolderPath, "*.txt").Count();
    }

    private int CountPresentationFiles()
    {
        if (!Directory.Exists(PresentationFolderPath))
            return 0;

        return Directory.EnumerateFiles(PresentationFolderPath)
            .Count(f => PresentationFileItem.IsSupportedFile(f));
    }

    /// <summary>
    /// Finds the first image in the Presentation folder for use as card thumbnail.
    /// </summary>
    private string? FindFirstPresentationImage()
    {
        if (!Directory.Exists(PresentationFolderPath))
            return null;

        return Directory.EnumerateFiles(PresentationFolderPath)
            .Where(f => PresentationFileItem.IsImageFile(f))
            .OrderBy(f => Path.GetFileName(f))
            .FirstOrDefault();
    }

    /// <summary>
    /// Loads the thumbnail asynchronously via the orchestrator.
    /// </summary>
    private async Task LoadThumbnailAsync(string path)
    {
        var orchestrator = PathToBitmapConverter.ThumbnailOrchestrator;
        if (orchestrator is null)
        {
            _isThumbnailLoading = false;
            return;
        }

        try
        {
            var bitmap = await orchestrator.RequestThumbnailAsync(
                path, new ThumbnailOwnerToken("TrainingRunCard"), ThumbnailPriority.Normal).ConfigureAwait(false);

            if (bitmap is not null)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    _thumbnail = bitmap;
                    _isThumbnailLoading = false;
                    OnPropertyChanged(nameof(Thumbnail));
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            _thumbnail = bitmap;
                            _isThumbnailLoading = false;
                            OnPropertyChanged(nameof(Thumbnail));
                        }
                        catch (InvalidOperationException)
                        {
                            _isThumbnailLoading = false;
                        }
                    });
                }
            }
            else
            {
                _isThumbnailLoading = false;
            }
        }
        catch
        {
            _isThumbnailLoading = false;
        }
    }
}
