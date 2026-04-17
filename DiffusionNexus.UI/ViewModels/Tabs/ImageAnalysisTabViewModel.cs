using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Identifies each analysis section inside the Image Analysis dashboard.
/// Add new entries here when future image-level checks are introduced.
/// </summary>
public enum ImageAnalysisSection
{
    /// <summary>kohya_ss-style resolution bucketing analysis.</summary>
    BucketAnalysis,

    /// <summary>Per-image technical quality (blur, exposure, noise, JPEG artifacts).</summary>
    ImageQuality,

    /// <summary>Exact and near-duplicate image detection (SHA-256 + pHash).</summary>
    DuplicateDetection
}

/// <summary>
/// Lightweight model for a dashboard summary card.
/// Each card represents one analysis section and shows a quick status overview.
/// </summary>
public partial class AnalysisSectionCardViewModel : ObservableObject
{
    private string _summary = "Not analyzed yet";
    private bool _hasResults;
    private bool _isSelected;

    /// <summary>Display title shown on the card.</summary>
    public required string Title { get; init; }

    /// <summary>Unicode icon or emoji for visual identification.</summary>
    public required string Icon { get; init; }

    /// <summary>Which analysis section this card represents.</summary>
    public required ImageAnalysisSection Section { get; init; }

    /// <summary>Short description shown below the title.</summary>
    public required string Description { get; init; }

    /// <summary>One-line summary of the last analysis result (e.g. "Score: 87 · 2 issues").</summary>
    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    /// <summary>Whether this section has been analyzed at least once.</summary>
    public bool HasResults
    {
        get => _hasResults;
        set => SetProperty(ref _hasResults, value);
    }

    /// <summary>Whether this card is currently selected in the dashboard.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// ViewModel for the Image Analysis tab — a dashboard that shows summary cards
/// for each image-level analysis section. Clicking a card reveals its detail view below.
/// Currently contains Bucket Analysis; extensible for future image checks.
/// </summary>
public partial class ImageAnalysisTabViewModel : ObservableObject
{
    private AnalysisSectionCardViewModel? _selectedCard;
    private string _folderPath = string.Empty;

    /// <summary>
    /// Creates a new <see cref="ImageAnalysisTabViewModel"/>.
    /// </summary>
    /// <param name="bucketAnalyzer">The bucket analyzer service.</param>
    /// <param name="imageChecks">Optional image quality check implementations for the quality tab.</param>
    public ImageAnalysisTabViewModel(BucketAnalyzer bucketAnalyzer, IEnumerable<IImageQualityCheck>? imageChecks = null, DuplicateDetector? duplicateDetector = null)
    {
        ArgumentNullException.ThrowIfNull(bucketAnalyzer);

        BucketAnalysisTab = new BucketAnalysisTabViewModel(bucketAnalyzer);
        BucketAnalysisTab.AnalysisCompleted += OnBucketAnalysisCompleted;
        BucketAnalysisTab.FixDistributionRequested += OnFixDistributionRequested;

        ImageQualityTab = imageChecks is not null
            ? new ImageQualityTabViewModel(imageChecks)
            : new ImageQualityTabViewModel();
        ImageQualityTab.AnalysisCompleted += OnImageQualityAnalysisCompleted;

        DuplicateDetectionTab = duplicateDetector is not null
            ? new DuplicateDetectionTabViewModel(duplicateDetector)
            : new DuplicateDetectionTabViewModel();
        DuplicateDetectionTab.AnalysisCompleted += OnDuplicateDetectionCompleted;

        InitializeCards();
        SelectCardCommand = new RelayCommand<AnalysisSectionCardViewModel?>(SelectCard);

        // Auto-select the first card
        if (Cards.Count > 0)
        {
            SelectCard(Cards[0]);
        }
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ImageAnalysisTabViewModel()
    {
        BucketAnalysisTab = new BucketAnalysisTabViewModel();
        ImageQualityTab = new ImageQualityTabViewModel();
        DuplicateDetectionTab = new DuplicateDetectionTabViewModel();
        InitializeCards();
        SelectCardCommand = new RelayCommand<AnalysisSectionCardViewModel?>(SelectCard);

        if (Cards.Count > 0)
        {
            SelectCard(Cards[0]);
        }
    }

    /// <summary>ViewModel for the Bucket Analysis detail section.</summary>
    public BucketAnalysisTabViewModel BucketAnalysisTab { get; }

    /// <summary>ViewModel for the Image Quality detail section.</summary>
    public ImageQualityTabViewModel ImageQualityTab { get; }

    /// <summary>ViewModel for the Duplicate Detection detail section.</summary>
    public DuplicateDetectionTabViewModel DuplicateDetectionTab { get; }

    /// <summary>Dashboard summary cards — one per analysis section.</summary>
    public ObservableCollection<AnalysisSectionCardViewModel> Cards { get; } = [];

    /// <summary>Currently selected dashboard card (determines which detail view is shown).</summary>
    public AnalysisSectionCardViewModel? SelectedCard
    {
        get => _selectedCard;
        private set
        {
            if (SetProperty(ref _selectedCard, value))
            {
                OnPropertyChanged(nameof(HasSelectedCard));
                OnPropertyChanged(nameof(IsBucketAnalysisSelected));
                OnPropertyChanged(nameof(IsImageQualitySelected));
                OnPropertyChanged(nameof(IsDuplicateDetectionSelected));
            }
        }
    }

    /// <summary>Whether any card is selected.</summary>
    public bool HasSelectedCard => _selectedCard is not null;

    /// <summary>Whether the Bucket Analysis section is currently active.</summary>
    public bool IsBucketAnalysisSelected =>
        _selectedCard?.Section == ImageAnalysisSection.BucketAnalysis;

    /// <summary>Whether the Image Quality section is currently active.</summary>
    public bool IsImageQualitySelected =>
        _selectedCard?.Section == ImageAnalysisSection.ImageQuality;

    /// <summary>Whether the Duplicate Detection section is currently active.</summary>
    public bool IsDuplicateDetectionSelected =>
        _selectedCard?.Section == ImageAnalysisSection.DuplicateDetection;

    /// <summary>Command to select a dashboard card.</summary>
    public IRelayCommand<AnalysisSectionCardViewModel?> SelectCardCommand { get; }

    /// <summary>
    /// Raised when a child analysis tab requests navigation to Batch Crop/Scale.
    /// Bubbles up to <see cref="DatasetQualityTabViewModel"/>.
    /// </summary>
    public event Action? FixDistributionRequested;

    /// <summary>
    /// Updates the dataset folder path and refreshes all child analysis VMs.
    /// Called by the parent ViewModel when the dataset context changes.
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset folder.</param>
    public void RefreshContext(string folderPath)
    {
        _folderPath = folderPath ?? string.Empty;
        BucketAnalysisTab.RefreshContext(_folderPath);
        ImageQualityTab.RefreshContext(_folderPath);
        DuplicateDetectionTab.RefreshContext(_folderPath);

        // Reset card summaries when context changes
        foreach (var card in Cards)
        {
            card.Summary = "Not analyzed yet";
            card.HasResults = false;
        }
    }

    /// <summary>
    /// Updates the Bucket Analysis dashboard card with the latest results.
    /// Called after bucket analysis completes.
    /// </summary>
    /// <param name="score">Distribution evenness score (0–100).</param>
    /// <param name="issueCount">Number of issues detected.</param>
    /// <param name="scoreLabel">Human-readable score label (Poor/Fair/Good/Excellent).</param>
    public void UpdateBucketAnalysisSummary(double score, int issueCount, string scoreLabel)
    {
        var card = FindCard(ImageAnalysisSection.BucketAnalysis);
        if (card is null) return;

        card.Summary = issueCount > 0
            ? $"Score: {score:F0} ({scoreLabel}) · {issueCount} issue{(issueCount != 1 ? "s" : "")}"
            : $"Score: {score:F0} ({scoreLabel}) · No issues";
        card.HasResults = true;
    }

    private void InitializeCards()
    {
        Cards.Add(new AnalysisSectionCardViewModel
        {
            Title = "Bucket Analysis",
            Icon = "📊",
            Section = ImageAnalysisSection.BucketAnalysis,
            Description = "kohya_ss-style resolution bucketing"
        });

        Cards.Add(new AnalysisSectionCardViewModel
        {
            Title = "Image Quality",
            Icon = "\ud83d\udd0d",
            Section = ImageAnalysisSection.ImageQuality,
            Description = "Blur, exposure & sharpness analysis"
        });

        Cards.Add(new AnalysisSectionCardViewModel
        {
            Title = "Duplicate Detection",
            Icon = "\ud83d\udd17",
            Section = ImageAnalysisSection.DuplicateDetection,
            Description = "Exact & near-duplicate image detection"
        });
    }

    private void SelectCard(AnalysisSectionCardViewModel? card)
    {
        if (card is null) return;

        // Deselect previous
        if (_selectedCard is not null)
        {
            _selectedCard.IsSelected = false;
        }

        card.IsSelected = true;
        SelectedCard = card;
    }

    private AnalysisSectionCardViewModel? FindCard(ImageAnalysisSection section)
    {
        foreach (var card in Cards)
        {
            if (card.Section == section)
                return card;
        }
        return null;
    }

    /// <summary>
    /// Updates the Image Quality dashboard card with the latest results.
    /// </summary>
    public void UpdateImageQualitySummary(double score, int issueCount, string scoreLabel)
    {
        var card = FindCard(ImageAnalysisSection.ImageQuality);
        if (card is null) return;

        card.Summary = issueCount > 0
            ? $"Score: {score:F0} ({scoreLabel}) \u00b7 {issueCount} issue{(issueCount != 1 ? "s" : "")}"
            : $"Score: {score:F0} ({scoreLabel}) \u00b7 No issues";
        card.HasResults = true;
    }

    private void OnBucketAnalysisCompleted(double score, int issueCount, string scoreLabel)
    {
        UpdateBucketAnalysisSummary(score, issueCount, scoreLabel);
    }

    private void OnImageQualityAnalysisCompleted(double score, int issueCount, string scoreLabel)
    {
        UpdateImageQualitySummary(score, issueCount, scoreLabel);
    }

    private void OnDuplicateDetectionCompleted(double score, int issueCount, string scoreLabel)
    {
        UpdateDuplicateDetectionSummary(score, issueCount, scoreLabel);
    }

    /// <summary>
    /// Updates the Duplicate Detection dashboard card with the latest results.
    /// </summary>
    public void UpdateDuplicateDetectionSummary(double score, int issueCount, string scoreLabel)
    {
        var card = FindCard(ImageAnalysisSection.DuplicateDetection);
        if (card is null) return;

        card.Summary = issueCount > 0
            ? $"Score: {score:F0} ({scoreLabel}) \u00b7 {issueCount} issue{(issueCount != 1 ? "s" : "")}"
            : $"Score: {score:F0} ({scoreLabel}) \u00b7 No issues";
        card.HasResults = true;
    }

    private void OnFixDistributionRequested()
    {
        FixDistributionRequested?.Invoke();
    }
}
