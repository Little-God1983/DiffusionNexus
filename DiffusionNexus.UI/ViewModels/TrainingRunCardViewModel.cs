using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
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
    /// Refreshes the epoch and note counts from the file system.
    /// </summary>
    public void RefreshCounts()
    {
        EpochCount = CountEpochFiles();
        NoteCount = CountNoteFiles();
        OnPropertyChanged(nameof(EpochCount));
        OnPropertyChanged(nameof(NoteCount));
        OnPropertyChanged(nameof(EpochCountText));
        OnPropertyChanged(nameof(NoteCountText));
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
}
