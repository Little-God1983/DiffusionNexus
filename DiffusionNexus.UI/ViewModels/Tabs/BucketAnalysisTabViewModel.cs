using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Lightweight row model for the bucket distribution bar chart.
/// </summary>
public class BucketBarViewModel
{
    /// <summary>Bucket label (e.g. "1024 × 768").</summary>
    public required string Label { get; init; }

    /// <summary>Number of images in this bucket.</summary>
    public required int ImageCount { get; init; }

    /// <summary>Width of the bar in pixels (scaled relative to the largest bucket).</summary>
    public required double BarWidth { get; init; }

    /// <summary>True if this bucket contains only 1 image (visual warning hint).</summary>
    public required bool IsSingleImage { get; init; }
}

/// <summary>
/// Row model for the per-image assignment table.
/// </summary>
public class ImageAssignmentRowViewModel
{
    /// <summary>File name (no path).</summary>
    public required string FileName { get; init; }

    /// <summary>Original resolution text (e.g. "1920 × 1080").</summary>
    public required string OriginalResolution { get; init; }

    /// <summary>Assigned bucket resolution text.</summary>
    public required string BucketResolution { get; init; }

    /// <summary>Crop percentage formatted (e.g. "12.3%").</summary>
    public required string CropPercentage { get; init; }

    /// <summary>Scale factor formatted (e.g. "1.500×").</summary>
    public required string ScaleFactor { get; init; }

    /// <summary>True when scale factor ≥ 2.0 (heavy upscale).</summary>
    public required bool HasUpscaleWarning { get; init; }

    /// <summary>True when crop ≥ 15%.</summary>
    public required bool HasCropWarning { get; init; }
}

/// <summary>
/// ViewModel for the Bucket Analysis tab.
/// Simulates kohya_ss-style bucketing and shows distribution, per-image details, and issues.
/// When the distribution score is below <see cref="RecommendationThreshold"/>,
/// contextual recommendations are generated and a button to navigate to the
/// Batch Crop/Scale tab is surfaced.
/// </summary>
public class BucketAnalysisTabViewModel : ObservableObject
{
    private const double MaxBarPixels = 300.0;

    /// <summary>
    /// Distribution score below which recommendations are generated.
    /// </summary>
    private const double RecommendationThreshold = 65.0;

    private readonly BucketAnalyzer? _analyzer;

    private string _folderPath = string.Empty;
    private int _baseResolution = 1024;
    private int _stepSize = 64;
    private int _minDimension = 256;
    private int _maxDimension = 2048;
    private double _maxAspectRatio = 2.0;
    private int _batchSize = 1;
    private bool _isAnalyzing;
    private bool _hasResults;
    private bool _hasRecommendations;
    private double _distributionScore;
    private string _scoreLabel = string.Empty;
    private string _summaryText = string.Empty;

    /// <summary>
    /// Creates a new <see cref="BucketAnalysisTabViewModel"/>.
    /// </summary>
    /// <param name="analyzer">The bucket analyzer service.</param>
    public BucketAnalysisTabViewModel(BucketAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        FixDistributionCommand = new RelayCommand(OnFixDistribution);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public BucketAnalysisTabViewModel()
    {
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        FixDistributionCommand = new RelayCommand(OnFixDistribution);
    }

    #region Observable Properties

    /// <summary>Base resolution for bucket generation (target area = BaseResolution²).</summary>
    public int BaseResolution
    {
        get => _baseResolution;
        set => SetProperty(ref _baseResolution, value);
    }

    /// <summary>Step size for bucket dimension increments.</summary>
    public int StepSize
    {
        get => _stepSize;
        set => SetProperty(ref _stepSize, value);
    }

    /// <summary>Minimum dimension for any bucket side.</summary>
    public int MinDimension
    {
        get => _minDimension;
        set => SetProperty(ref _minDimension, value);
    }

    /// <summary>Maximum dimension for any bucket side.</summary>
    public int MaxDimension
    {
        get => _maxDimension;
        set => SetProperty(ref _maxDimension, value);
    }

    /// <summary>Maximum aspect ratio (width/height or height/width).</summary>
    public double MaxAspectRatio
    {
        get => _maxAspectRatio;
        set => SetProperty(ref _maxAspectRatio, value);
    }

    /// <summary>Training batch size.</summary>
    public int BatchSize
    {
        get => _batchSize;
        set => SetProperty(ref _batchSize, value);
    }

    /// <summary>True while analysis is running.</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True when results are available to display.</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set => SetProperty(ref _hasResults, value);
    }

    /// <summary>Distribution evenness score (0–100).</summary>
    public double DistributionScore
    {
        get => _distributionScore;
        private set => SetProperty(ref _distributionScore, value);
    }

    /// <summary>Score label (Poor / Fair / Good / Excellent).</summary>
    public string ScoreLabel
    {
        get => _scoreLabel;
        private set => SetProperty(ref _scoreLabel, value);
    }

    /// <summary>Summary text describing the analysis results.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>True when the distribution score is below the recommendation threshold.</summary>
    public bool HasRecommendations
    {
        get => _hasRecommendations;
        private set => SetProperty(ref _hasRecommendations, value);
    }

    #endregion

    #region Collections

    /// <summary>Bar chart data for used buckets.</summary>
    public ObservableCollection<BucketBarViewModel> UsedBuckets { get; } = [];

    /// <summary>Per-image assignment rows.</summary>
    public ObservableCollection<ImageAssignmentRowViewModel> ImageAssignments { get; } = [];

    /// <summary>Issues detected during analysis.</summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    /// <summary>Contextual recommendations for improving the distribution score.</summary>
    public ObservableCollection<string> Recommendations { get; } = [];

    #endregion

    #region Commands

    /// <summary>Command to start the bucket analysis.</summary>
    public AsyncRelayCommand AnalyzeCommand { get; }

    /// <summary>
    /// Command to navigate to the Batch Crop/Scale tab to fix the distribution.
    /// Visible only when <see cref="HasRecommendations"/> is true.
    /// </summary>
    public RelayCommand FixDistributionCommand { get; }

    /// <summary>
    /// Raised after analysis completes with the score, issue count, and label.
    /// Used by the parent dashboard to update its summary card.
    /// </summary>
    public event Action<double, int, string>? AnalysisCompleted;

    /// <summary>
    /// Raised when the user clicks the "Open in Batch Crop/Scale" button.
    /// The parent ViewModel chain handles navigation.
    /// </summary>
    public event Action? FixDistributionRequested;

    private bool CanAnalyze => !IsAnalyzing && !string.IsNullOrWhiteSpace(_folderPath);

    #endregion

    #region Public Methods

    /// <summary>
    /// Updates the dataset folder path and refreshes command state.
    /// Called by the parent view model when the dataset context changes.
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset folder.</param>
    public void RefreshContext(string folderPath)
    {
        _folderPath = folderPath ?? string.Empty;
        ClearResults();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Private Methods

    private async Task AnalyzeAsync()
    {
        if (_analyzer is null || string.IsNullOrWhiteSpace(_folderPath))
            return;

        IsAnalyzing = true;
        ClearResults();

        try
        {
            var config = BuildConfig();
            var result = await Task.Run(() => _analyzer.AnalyzeFolder(_folderPath, config));
            ApplyResult(result);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private BucketConfig BuildConfig() => new()
    {
        BaseResolution = _baseResolution,
        StepSize = _stepSize,
        MinDimension = _minDimension,
        MaxDimension = _maxDimension,
        MaxAspectRatio = _maxAspectRatio,
        BatchSize = _batchSize
    };

    private void ApplyResult(BucketAnalysisResult result)
    {
        // Bar chart
        int maxCount = result.Distribution.Count > 0
            ? result.Distribution.Max(d => d.ImageCount)
            : 1;

        foreach (var entry in result.Distribution)
        {
            double barWidth = maxCount > 0
                ? (double)entry.ImageCount / maxCount * MaxBarPixels
                : 0;

            UsedBuckets.Add(new BucketBarViewModel
            {
                Label = entry.Bucket.Label,
                ImageCount = entry.ImageCount,
                BarWidth = Math.Max(barWidth, 2), // minimum visible bar
                IsSingleImage = entry.ImageCount == 1
            });
        }

        // Per-image table
        foreach (var a in result.Assignments)
        {
            ImageAssignments.Add(new ImageAssignmentRowViewModel
            {
                FileName = a.FileName,
                OriginalResolution = $"{a.OriginalWidth} × {a.OriginalHeight}",
                BucketResolution = a.AssignedBucket.Label,
                CropPercentage = $"{a.CropPercentage:F1}%",
                ScaleFactor = $"{a.ScaleFactor:F3}×",
                HasUpscaleWarning = a.ScaleFactor >= BucketAnalyzer.UpscaleCriticalThreshold,
                HasCropWarning = a.CropPercentage >= BucketAnalyzer.CropWarningThreshold
            });
        }

        // Issues
        foreach (var issue in result.Issues)
            Issues.Add(issue);

        // Score
        DistributionScore = result.DistributionScore;
        ScoreLabel = result.ScoreLabel;

        // Summary
        SummaryText = $"{result.Assignments.Count} images assigned to {result.Distribution.Count} bucket(s) "
                    + $"out of {result.AllBuckets.Count} available. "
                    + $"Distribution score: {result.DistributionScore:F1} ({result.ScoreLabel}).";

        HasResults = result.Assignments.Count > 0;

        // Generate recommendations when score is below threshold
        GenerateRecommendations(result);

        AnalysisCompleted?.Invoke(result.DistributionScore, result.Issues.Count, result.ScoreLabel);
    }

    /// <summary>
    /// Generates contextual recommendations when the distribution score is below
    /// <see cref="RecommendationThreshold"/>. Recommendations guide the user towards
    /// using the Batch Crop/Scale tab to rebalance their dataset.
    /// </summary>
    private void GenerateRecommendations(BucketAnalysisResult result)
    {
        Recommendations.Clear();

        if (result.DistributionScore >= RecommendationThreshold)
        {
            HasRecommendations = false;
            return;
        }

        // Dominant bucket tip
        if (result.Distribution.Count > 0)
        {
            var dominant = result.Distribution.OrderByDescending(d => d.ImageCount).First();
            var total = result.Assignments.Count;
            var pct = total > 0 ? (double)dominant.ImageCount / total * 100.0 : 0;

            if (pct > 40)
            {
                Recommendations.Add(
                    $"\u26A0 {pct:F0}% of images land in the {dominant.Bucket.Label} bucket. " +
                    "Use Batch Crop/Scale to crop or pad images to different aspect ratios.");
            }
        }

        // Single-image buckets
        var singleBuckets = result.Distribution.Count(d => d.ImageCount == 1);
        if (singleBuckets > 0)
        {
            Recommendations.Add(
                $"\u26A0 {singleBuckets} bucket(s) contain only 1 image. " +
                "Consider cropping those images to a more common aspect ratio.");
        }

        // High crop images
        var highCropCount = result.Assignments.Count(
            a => a.CropPercentage >= BucketAnalyzer.CropWarningThreshold);
        if (highCropCount > 0)
        {
            Recommendations.Add(
                $"\u2702 {highCropCount} image(s) lose \u2265 {BucketAnalyzer.CropWarningThreshold}% " +
                "of pixels during bucketing. Pre-cropping to a standard ratio in " +
                "Batch Crop/Scale reduces waste.");
        }

        // Heavy upscale images
        var upscaleCount = result.Assignments.Count(
            a => a.ScaleFactor >= BucketAnalyzer.UpscaleCriticalThreshold);
        if (upscaleCount > 0)
        {
            Recommendations.Add(
                $"\uD83D\uDD0D {upscaleCount} image(s) require \u2265 " +
                $"{BucketAnalyzer.UpscaleCriticalThreshold:F1}\u00D7 upscaling. " +
                "Scaling these down to match the base resolution avoids blurry training data.");
        }

        // General tip when no specific recommendations were triggered
        if (Recommendations.Count == 0)
        {
            Recommendations.Add(
                "\uD83D\uDCA1 The distribution is uneven. Use Batch Crop/Scale to pre-process images " +
                "to standard aspect ratios before training.");
        }

        HasRecommendations = Recommendations.Count > 0;
    }

    private void OnFixDistribution()
    {
        FixDistributionRequested?.Invoke();
    }

    private void ClearResults()
    {
        UsedBuckets.Clear();
        ImageAssignments.Clear();
        Issues.Clear();
        Recommendations.Clear();
        DistributionScore = 0;
        ScoreLabel = string.Empty;
        SummaryText = string.Empty;
        HasResults = false;
        HasRecommendations = false;
    }

    #endregion
}
