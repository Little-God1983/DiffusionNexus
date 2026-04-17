using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Represents a cluster of duplicate images for display in the UI.
/// Shows thumbnails side-by-side with similarity info.
/// </summary>
public class DuplicateClusterItemViewModel : ObservableObject
{
    private bool _isExpanded;

    /// <summary>Group label (e.g. "Exact Duplicate Group 1").</summary>
    public required string GroupLabel { get; init; }

    /// <summary>Hamming distance between images in this cluster.</summary>
    public required int HammingDistance { get; init; }

    /// <summary>Similarity percentage (100% for exact).</summary>
    public required double SimilarityPercent { get; init; }

    /// <summary>Whether these are exact (SHA-256) duplicates.</summary>
    public required bool IsExactDuplicate { get; init; }

    /// <summary>Paths of images in this cluster.</summary>
    public required IReadOnlyList<string> ImagePaths { get; init; }

    /// <summary>File names for display.</summary>
    public IReadOnlyList<string> FileNames => ImagePaths.Select(Path.GetFileName).ToList()!;

    /// <summary>Formatted similarity display.</summary>
    public string SimilarityDisplay => IsExactDuplicate
        ? "100% identical (SHA-256 match)"
        : $"{SimilarityPercent:F1}% similar (Hamming distance: {HammingDistance})";

    /// <summary>Severity color.</summary>
    public string SeverityColor => IsExactDuplicate ? "#FF6B6B" : "#FFA726";

    /// <summary>Severity icon.</summary>
    public string SeverityIcon => IsExactDuplicate ? "\u2716" : "\u26A0";

    /// <summary>Whether this cluster is expanded to show image previews.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Toggles the expanded state.</summary>
    public IRelayCommand ToggleExpandCommand { get; }

    public DuplicateClusterItemViewModel()
    {
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }
}

/// <summary>
/// ViewModel for the Duplicate Detection detail section within the Image Analysis dashboard.
/// Shows duplicate clusters with side-by-side thumbnails and similarity information.
/// </summary>
public class DuplicateDetectionTabViewModel : ObservableObject, IDialogServiceAware
{
    /// <inheritdoc />
    public IDialogService? DialogService { get; set; }
    private readonly DuplicateDetector? _detector;

    private string _folderPath = string.Empty;
    private bool _isAnalyzing;
    private bool _hasResults;
    private double _overallScore;
    private string _overallScoreLabel = string.Empty;
    private int _issueCount;
    private string _summaryText = "Not analyzed yet";
    private int _exactDuplicateCount;
    private int _nearDuplicateCount;
    private int _totalImagesScanned;
    private Issue? _selectedIssue;

    // Maps file paths to cluster items for issue-to-cluster lookup
    private readonly Dictionary<string, DuplicateClusterItemViewModel> _clusterItemsByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new <see cref="DuplicateDetectionTabViewModel"/> with a detector.
    /// </summary>
    public DuplicateDetectionTabViewModel(DuplicateDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        OpenFixerCommand = new AsyncRelayCommand(OpenFixerAsync, () => CanOpenFixer);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public DuplicateDetectionTabViewModel()
    {
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        OpenFixerCommand = new AsyncRelayCommand(OpenFixerAsync, () => CanOpenFixer);
    }

    #region Observable Properties

    /// <summary>Duplicate clusters found during analysis.</summary>
    public ObservableCollection<DuplicateClusterItemViewModel> Clusters { get; } = [];

    /// <summary>Issues from the analysis.</summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    /// <summary>Cluster items for the currently selected issue, shown in the right panel.</summary>
    public ObservableCollection<DuplicateClusterItemViewModel> AffectedImages { get; } = [];

    /// <summary>All clusters sorted by severity, shown when no issue is selected.</summary>
    public ObservableCollection<DuplicateClusterItemViewModel> AllImages { get; } = [];

    /// <summary>Whether analysis is running.</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(CanAnalyze));
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether results are available.</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            if (SetProperty(ref _hasResults, value))
            {
                OnPropertyChanged(nameof(CanOpenFixer));
                OpenFixerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Can the analyze command execute.</summary>
    public bool CanAnalyze => !string.IsNullOrEmpty(_folderPath) && !IsAnalyzing;

    /// <summary>Overall duplicate detection score.</summary>
    public double OverallScore
    {
        get => _overallScore;
        private set => SetProperty(ref _overallScore, value);
    }

    /// <summary>Label for the overall score.</summary>
    public string OverallScoreLabel
    {
        get => _overallScoreLabel;
        private set => SetProperty(ref _overallScoreLabel, value);
    }

    /// <summary>Number of issues detected.</summary>
    public int IssueCount
    {
        get => _issueCount;
        private set => SetProperty(ref _issueCount, value);
    }

    /// <summary>Summary text.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>Number of exact duplicate groups found.</summary>
    public int ExactDuplicateCount
    {
        get => _exactDuplicateCount;
        private set => SetProperty(ref _exactDuplicateCount, value);
    }

    /// <summary>Number of near-duplicate groups found.</summary>
    public int NearDuplicateCount
    {
        get => _nearDuplicateCount;
        private set => SetProperty(ref _nearDuplicateCount, value);
    }

    /// <summary>Total images scanned.</summary>
    public int TotalImagesScanned
    {
        get => _totalImagesScanned;
        private set => SetProperty(ref _totalImagesScanned, value);
    }

    /// <summary>Currently selected issue in the left panel.</summary>
    public Issue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (SetProperty(ref _selectedIssue, value))
            {
                OnPropertyChanged(nameof(HasSelectedIssue));
                OnPropertyChanged(nameof(ShowAllImages));
                PopulateAffectedImages(value);
            }
        }
    }

    /// <summary>Whether an issue is selected.</summary>
    public bool HasSelectedIssue => _selectedIssue is not null;

    /// <summary>Whether to show all clusters (no specific issue selected).</summary>
    public bool ShowAllImages => _selectedIssue is null && HasResults;

    /// <summary>Can the fixer be opened (issues exist to fix).</summary>
    public bool CanOpenFixer => HasResults && Issues.Count > 0;

    #endregion

    #region Commands

    /// <summary>Analyze command.</summary>
    public IAsyncRelayCommand AnalyzeCommand { get; }

    /// <summary>Opens the duplicate fixer window.</summary>
    public IAsyncRelayCommand OpenFixerCommand { get; }

    #endregion

    /// <summary>
    /// Raised when analysis completes, with (score, issueCount, label).
    /// </summary>
    public event Action<double, int, string>? AnalysisCompleted;

    /// <summary>
    /// Updates the dataset folder path and resets state.
    /// </summary>
    public void RefreshContext(string folderPath)
    {
        _folderPath = folderPath ?? string.Empty;
        HasResults = false;
        Issues.Clear();
        Clusters.Clear();
        AffectedImages.Clear();
        AllImages.Clear();
        _clusterItemsByPath.Clear();
        SelectedIssue = null;
        SummaryText = "Not analyzed yet";
        ExactDuplicateCount = 0;
        NearDuplicateCount = 0;
        TotalImagesScanned = 0;
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Applies externally computed results (when run via the full pipeline).
    /// </summary>
    public void ApplyResults(ImageCheckResult result, IReadOnlyList<DuplicateCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(clusters);

        Issues.Clear();
        Clusters.Clear();
        AffectedImages.Clear();
        AllImages.Clear();
        _clusterItemsByPath.Clear();

        foreach (var issue in result.Issues)
            Issues.Add(issue);

        int exactIdx = 0;
        int nearIdx = 0;

        foreach (var cluster in clusters)
        {
            string label;
            if (cluster.IsExactDuplicate)
            {
                exactIdx++;
                label = $"Exact Duplicate Group {exactIdx}";
            }
            else
            {
                nearIdx++;
                label = $"Near-Duplicate Group {nearIdx}";
            }

            var clusterVm = new DuplicateClusterItemViewModel
            {
                GroupLabel = label,
                HammingDistance = cluster.HammingDistance,
                SimilarityPercent = cluster.SimilarityPercent,
                IsExactDuplicate = cluster.IsExactDuplicate,
                ImagePaths = cluster.ImagePaths
            };

            Clusters.Add(clusterVm);

            // Index by each image path for issue-to-cluster lookup
            foreach (var path in cluster.ImagePaths)
                _clusterItemsByPath[path] = clusterVm;
        }

        // AllImages: exact duplicates first, then near-duplicates
        foreach (var clusterVm in Clusters.OrderBy(c => c.IsExactDuplicate ? 0 : 1).ThenByDescending(c => c.SimilarityPercent))
            AllImages.Add(clusterVm);

        ExactDuplicateCount = clusters.Count(c => c.IsExactDuplicate);
        NearDuplicateCount = clusters.Count(c => !c.IsExactDuplicate);
        TotalImagesScanned = result.PerImageScores.Count;

        string label2 = result.Score switch
        {
            >= 85 => "Excellent",
            >= 65 => "Good",
            >= 40 => "Fair",
            _ => "Poor"
        };

        OverallScore = Math.Round(result.Score, 1);
        OverallScoreLabel = label2;
        IssueCount = Issues.Count;
        HasResults = true;

        SummaryText = Issues.Count > 0
            ? $"Score: {result.Score:F0} ({label2}) \u00b7 {Issues.Count} issue{(Issues.Count != 1 ? "s" : "")}"
            : $"Score: {result.Score:F0} ({label2}) \u00b7 No duplicates found";

        // Auto-select first issue if any
        SelectedIssue = Issues.Count > 0 ? Issues[0] : null;

        AnalysisCompleted?.Invoke(result.Score, Issues.Count, label2);
    }

    private void PopulateAffectedImages(Issue? issue)
    {
        AffectedImages.Clear();

        if (issue is null)
            return;

        // Find clusters that contain any of the affected files
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in issue.AffectedFiles)
        {
            if (_clusterItemsByPath.TryGetValue(filePath, out var cluster) && seen.Add(cluster.GroupLabel))
            {
                AffectedImages.Add(cluster);
            }
        }
    }

    private async Task OpenFixerAsync()
    {
        if (DialogService is null || Clusters.Count == 0)
            return;

        await DialogService.ShowDuplicateFixerAsync(Clusters);
    }

    private async Task AnalyzeAsync()
    {
        if (_detector is null || string.IsNullOrEmpty(_folderPath))
            return;

        IsAnalyzing = true;
        try
        {
            var images = await Task.Run(() =>
            {
                var imgList = new List<ImageFileInfo>();
                if (!Directory.Exists(_folderPath)) return imgList;

                foreach (var file in Directory.EnumerateFiles(_folderPath))
                {
                    if (!SupportedMediaTypes.IsImageFile(file))
                        continue;
                    imgList.Add(new ImageFileInfo(file, 0, 0));
                }
                return imgList;
            });

            var config = new DatasetConfig
            {
                FolderPath = _folderPath,
                LoraType = LoraType.Character
            };

            var result = await _detector.RunAsync(images, config);

            ApplyResults(result, _detector.LastClusters);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
